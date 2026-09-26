using System.Text.Json;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Cli;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Analytics.Tests;

/// <summary>
/// qa backtest / qa trials end to end (temp ledgers; synthetic data or a temp DuckDB store). The runs use a fixture
/// config folder (locked holdout from 2025-10-01, unverified costs), so they do not change when the owner verifies
/// the cost model or unlocks the holdout in the repository's config/.
/// </summary>
public sealed class BacktestCliTests : IDisposable
{
    private const string FixtureHoldout =
        "{\"format\":\"qa-holdout/1\",\"locked\":true,\"start\":\"2025-10-01\",\"unlocked_by\":null,\"unlocked_on\":null,\"reason\":null}";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qa-backtest-cli", Guid.NewGuid().ToString("N"));

    public BacktestCliTests()
    {
        Directory.CreateDirectory(_config);
        File.WriteAllText(Path.Combine(_config, HoldoutPolicy.FileName), FixtureHoldout);
        File.WriteAllText(Path.Combine(_config, "costs.avanza-small.json"), FixtureCosts());
    }

    private string _config => Path.Combine(_dir, "config");

    private string Ledger => Path.Combine(_dir, "ledger.jsonl");

    private static string RepoConfig => Path.Combine(RepoRoot(), "config");

    private static string FixtureCosts(string verifiedOn = "null", string participationCap = "0.10") =>
        "{\"format\":\"qa-costs/1\",\"name\":\"avanza-small\",\"currency\":\"SEK\",\"courtage\":{\"min\":39.0,\"rate\":0.0015},"
        + $"\"fx_fee_rate\":0.0025,\"slippage_bps\":5.0,\"half_spread_bps\":5.0,\"participation_cap\":{participationCap},"
        + $"\"source_url\":\"https://www.avanza.se/priser-och-avgifter.html\",\"verified_on\":{verifiedOn}}}";

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RepositoryConfigFiles_Load_AndLabelTheirStatus()
    {
        // Whatever the owner has set (locked or not, verified or not), the committed files must load and say so.
        HoldoutPolicy holdout = HoldoutPolicy.Load(Path.Combine(RepoConfig, HoldoutPolicy.FileName));
        Assert.True(holdout.Start.Year >= 2000);

        CostModel costs = CostModel.Load(Path.Combine(RepoConfig, "costs.avanza-small.json"));
        Assert.True(costs.CourtageMin >= 0 && costs.CourtageRate >= 0);
        if (costs.Verified)
        {
            Assert.Contains($"verified {costs.VerifiedOn:yyyy-MM-dd}", costs.Label, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("UNVERIFIED", costs.Label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VerifiedCosts_AreLabelledVerified_InOutputAndLedger()
    {
        File.WriteAllText(Path.Combine(_config, "costs.avanza-small.json"), FixtureCosts(verifiedOn: "\"2026-09-26\""));
        (int code, string output, string error) = Qa("backtest", "run", "--strategy", "buy-and-hold", "--synthetic", "2x60");

        Assert.True(code == 0, error);
        Assert.Contains("avanza-small (verified 2026-09-26)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("UNVERIFIED", output, StringComparison.Ordinal);
        TrialRecord logged = Assert.Single(new TrialLedger(Ledger).ReadAll());
        Assert.True(logged.CostsVerified);
        Assert.Null(logged.Note);
    }

    [Fact]
    public void BadPolicyFiles_AreErrors_NeverDefaults()
    {
        string dir = Path.Combine(_dir, "bad");
        Directory.CreateDirectory(dir);
        string policy = Path.Combine(dir, HoldoutPolicy.FileName);
        Assert.Throws<BacktestConfigException>(() => HoldoutPolicy.Load(policy)); // missing
        File.WriteAllText(policy, "{\"format\":\"qa-holdout/1\",\"locked\":false,\"start\":\"2025-10-01\"}");
        Assert.Throws<BacktestConfigException>(() => HoldoutPolicy.Load(policy)); // unlocked without who/when/why
        File.WriteAllText(policy, "{\"format\":\"qa-holdout/1\",\"locked\":true}");
        Assert.Throws<BacktestConfigException>(() => HoldoutPolicy.Load(policy)); // no start
        File.WriteAllText(policy, "not json");
        Assert.Throws<BacktestConfigException>(() => HoldoutPolicy.Load(policy));

        string costs = Path.Combine(dir, "costs.x.json");
        File.WriteAllText(costs, FixtureCosts(participationCap: "1.5"));
        Assert.Throws<BacktestConfigException>(() => CostModel.Load(costs));
        File.WriteAllText(costs, FixtureCosts(verifiedOn: "\"26/09/2026\""));
        Assert.Throws<BacktestConfigException>(() => CostModel.Load(costs)); // verified_on must be yyyy-MM-dd
    }

    [Fact]
    public void Run_Synthetic_PrintsLabelsAndMetrics_AndLogs()
    {
        (int code, string output, string error) = Qa("backtest", "run", "--strategy", "ma-cross", "--param", "fast=5", "--param", "slow=20",
            "--synthetic", "5x300", "--seed", "11");

        Assert.True(code == 0, error);
        Assert.Contains("synthetic-gbm — survivorship-free, point-in-time", output, StringComparison.Ordinal);
        Assert.Contains("seed 11", output, StringComparison.Ordinal);
        Assert.DoesNotContain("clipped", output, StringComparison.Ordinal); // synthetic data ends before the holdout
        Assert.Contains("UNVERIFIED", output, StringComparison.Ordinal);
        Assert.Contains("Trial T000001: ma-cross fast=5 slow=20 → ok", output, StringComparison.Ordinal);
        Assert.Contains("Deflated Sharpe", output, StringComparison.Ordinal);
        Assert.Contains("not financial advice", output, StringComparison.Ordinal);
        TrialRecord logged = Assert.Single(new TrialLedger(Ledger).ReadAll());
        Assert.Equal(11UL, logged.Seed);
        Assert.Equal("5", logged.Parameters["fast"]);
    }

    [Fact]
    public void Run_Json_IsTheLedgerRecord()
    {
        (int code, string output, string error) = Qa("backtest", "run", "--strategy", "buy-and-hold", "--synthetic", "3x100", "--order", "moo", "--json");

        Assert.True(code == 0, error);
        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement record = doc.RootElement.GetProperty("record");
        Assert.Equal("T000001", record.GetProperty("id").GetString());
        Assert.Equal("Ok", record.GetProperty("status").GetString());
        Assert.Equal(99, record.GetProperty("metrics").GetProperty("observations").GetInt32());
    }

    [Fact]
    public void Sweep_LogsEveryCombination_AndReportsPboAndDsr()
    {
        (int code, string output, string error) = Qa("backtest", "sweep", "--strategy", "ma-cross", "--grid", "fast=5,10", "--grid", "slow=20,40",
            "--synthetic", "4x400");

        Assert.True(code == 0, error);
        Assert.Contains("4 configurations run and logged (4 ok)", output, StringComparison.Ordinal);
        Assert.Contains("PBO (CSCV", output, StringComparison.Ordinal);
        Assert.Contains("Deflated Sharpe Ratio of the best", output, StringComparison.Ordinal);
        Assert.Equal(4, new TrialLedger(Ledger).ReadAll().Count);
    }

    [Fact]
    public void Trials_ListAndVerify_AndDetectTampering()
    {
        Assert.Equal(0, Qa("backtest", "run", "--strategy", "buy-and-hold", "--synthetic", "2x60").Code);
        Assert.Equal(0, Qa("backtest", "run", "--strategy", "random-targets", "--param", "seed=4", "--synthetic", "2x60").Code);

        (int code, string output, _) = Qa("trials", "list");
        Assert.Equal(0, code);
        Assert.Contains("T000001", output, StringComparison.Ordinal);
        Assert.Contains("random-targets", output, StringComparison.Ordinal);
        Assert.Contains("2 of 2 trial(s)", output, StringComparison.Ordinal);

        Assert.Contains("OK: 2 trial(s)", Qa("trials", "verify").Output, StringComparison.Ordinal);
        string[] lines = File.ReadAllLines(Ledger);
        File.WriteAllLines(Ledger, [lines[1]]);
        (int bad, _, string error) = Qa("trials", "verify");
        Assert.Equal(1, bad);
        Assert.Contains("NOT intact", error, StringComparison.Ordinal);
    }

    [Fact]
    public void LockedHoldout_ClipsByDefault_AndRefusesAnExplicitRangeIntoIt()
    {
        string config = Path.Combine(_dir, "early-holdout");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, HoldoutPolicy.FileName), FixtureHoldout.Replace("2025-10-01", "2015-06-01", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(config, "costs.avanza-small.json"), FixtureCosts());

        (int code, string output, string error) = Qa("backtest", "run", "--strategy", "buy-and-hold", "--synthetic", "2x300", "--config-dir", config);
        Assert.True(code == 0, error);
        Assert.Contains("Data clipped to end 2015-05-31: the final holdout from 2015-06-01 is locked.", output, StringComparison.Ordinal);

        (code, output, _) = Qa("backtest", "run", "--strategy", "buy-and-hold", "--synthetic", "2x300", "--config-dir", config, "--to", "2015-12-31");
        Assert.Equal(2, code);
        Assert.Contains("REJECTED (holdout)", output, StringComparison.Ordinal);
        Assert.Equal([TrialStatus.Ok, TrialStatus.RejectedHoldout], new TrialLedger(Ledger).ReadAll().Select(r => r.Status));
    }

    [Fact]
    public void MissingHoldoutPolicy_StopsTheRun()
    {
        string empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        (int code, _, string error) = Qa("backtest", "run", "--strategy", "buy-and-hold", "--synthetic", "2x60", "--config-dir", empty);
        Assert.Equal(1, code);
        Assert.Contains("do not run without it", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Ledger));
    }

    [Fact]
    public void BadArguments_AreReadableErrors()
    {
        Assert.Contains("exactly one data source", Qa("backtest", "run", "--strategy", "buy-and-hold").Error, StringComparison.Ordinal);
        Assert.Contains("<instruments>x<bars>", Qa("backtest", "run", "--strategy", "buy-and-hold", "--synthetic", "5by10").Error, StringComparison.Ordinal);
        Assert.Contains("unknown parameter", Qa("backtest", "run", "--strategy", "ma-cross", "--param", "fast=5", "--param", "slow=9", "--param", "x=1", "--synthetic", "2x60").Error, StringComparison.Ordinal);
        Assert.Contains("not key=value", Qa("backtest", "run", "--strategy", "ma-cross", "--param", "fast", "--synthetic", "2x60").Error, StringComparison.Ordinal);
        Assert.Contains("--order", Qa("backtest", "run", "--strategy", "buy-and-hold", "--synthetic", "2x60", "--order", "stop").Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_OnTheHistoryStore_LabelsTheBias_AndRefusesNonContinuousInstruments()
    {
        string storePath = Path.Combine(_dir, "quant.duckdb");
        var knownAt = new DateTimeOffset(2026, 9, 25, 16, 0, 0, TimeSpan.Zero);
        using (HistoryStore store = HistoryStore.Open(storePath))
        {
            store.RegisterSource(AvanzaChartImporter.AvanzaPriceChart);
            AddInstrument(store, "5240", "ERIC B", TradingModel.Continuous, knownAt, 60m);
            AddInstrument(store, "9999", "FNAUCT", TradingModel.Unknown, knownAt, 10m);
        }

        // Stored as Avanza's "ERIC B"; typed the way the docs spell it.
        (int code, string output, string error) = Qa("backtest", "run", "--strategy", "buy-and-hold", "--tickers", "ERIC-B", "--store", storePath);
        Assert.True(code == 0, error);
        Assert.Contains("avanza-price-chart — NOT survivorship-free, NOT point-in-time", output, StringComparison.Ordinal);
        Assert.Contains("current names only", output, StringComparison.Ordinal);
        TrialRecord logged = Assert.Single(new TrialLedger(Ledger).ReadAll());
        Assert.False(logged.SurvivorshipFree);
        Assert.Equal(["ERIC B"], logged.Universe);
        Assert.True(logged.To < new DateOnly(2025, 10, 1), "the locked holdout clips real data");

        (code, _, error) = Qa("backtest", "run", "--strategy", "buy-and-hold", "--tickers", "FNAUCT", "--store", storePath);
        Assert.Equal(1, code);
        Assert.Contains("continuous trading", error, StringComparison.Ordinal);
    }

    private static void AddInstrument(HistoryStore store, string id, string ticker, TradingModel model, DateTimeOffset knownAt, decimal price)
    {
        var ticks = new TickSizeTable([new TickSizeBand(0m, 99.99m, 0.01m), new TickSizeBand(100m, 999.9m, 0.1m)]);
        store.UpsertInstrument(
            new InstrumentRecord(new OrderbookId(id), "SE0000000000", ticker, ticker, "SEK", model == TradingModel.Continuous ? "XSTO" : "SSME", "STOCK", model, 1m,
                InstrumentRecord.CanonicalTickTable(ticks), new DateOnly(2026, 9, 25)),
            AvanzaChartImporter.AvanzaPriceChart.Name, "test", knownAt);
        var bars = new List<DailyBar>();
        var rng = new SeededRandom(ulong.Parse(id, System.Globalization.CultureInfo.InvariantCulture));
        for (DateOnly d = new(2025, 6, 2); d <= new DateOnly(2025, 12, 31); d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            decimal next = Math.Round(price * (decimal)Math.Exp(0.01 * rng.NextGaussian()), 2);
            bars.Add(new DailyBar(d, price, Math.Max(price, next) + 0.5m, Math.Min(price, next) - 0.5m, next, 2_000_000));
            price = next;
        }

        store.UpsertDailyBars(new OrderbookId(id), bars, AvanzaChartImporter.AvanzaPriceChart, "test", knownAt);
    }

    private (int Code, string Output, string Error) Qa(params string[] args)
    {
        var all = new List<string>(args);
        if (args[0] is "backtest" or "trials")
        {
            all.AddRange(["--ledger", Ledger]);
        }

        if (args[0] == "backtest" && !args.Contains("--config-dir"))
        {
            all.AddRange(["--config-dir", _config]);
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = QaCli.Run([.. all], output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "QuantAnalyst.sln")))
            {
                return d.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
