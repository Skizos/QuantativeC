using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// The "where am I, what next" verbs: <c>qa status</c> (offline overview with next steps) and <c>qa paper strategy</c>
/// (the strategy saved in paper.json, so <c>qa paper run</c> needs no arguments).
/// </summary>
public sealed class CliUsabilityTests : IDisposable
{
    private static readonly DateTimeOffset Saturday = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero); // 12:00 Stockholm

    private static readonly int[] IntradayDays = [14, 21, 22, 24];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-cli-usability", Guid.NewGuid().ToString("N"));
    private readonly FakeAvanza _server = new();
    private readonly FakeTimeProvider _time = new(Saturday);

    public CliUsabilityTests()
    {
        Directory.CreateDirectory(Config);
        string repo = Path.Combine(RepoRoot(), "config");
        foreach (string f in new[] { "risk-limits.json", "costs.avanza-start.json", "costs.avanza-mini.json", "market-calendar.XSTO.2026.json", "market-calendar.XSTO.2027.json" })
        {
            File.Copy(Path.Combine(repo, f), Path.Combine(Config, f));
        }

        File.WriteAllText(Path.Combine(Config, "paper.json"), """{ "format": "qa-paper/1", "costs": "avanza-start", "cash": 5000, "decision_time": "09:10", "note": "kept" }""");
        File.WriteAllText(Path.Combine(Config, "universe.json"), """{ "format": "qa-universe/1", "instruments": [] }""");
    }

    private string Config => Path.Combine(_root, "config");

    private string Store => Path.Combine(_root, "q.duckdb");

    private string State => Path.Combine(_root, "state");

    private string Audit => Path.Combine(_root, "audit");

    private string KillFile => Path.Combine(_root, "KILL");

    private string Ledger => Path.Combine(_root, "ledger.jsonl");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private (int Code, string Output, string Error) Qa(params string[] args)
    {
        var services = new AvanzaCliServices(
            (options, secrets, prompt, logger, redactor) => AvanzaConnection.CreateForTest(
                new AvanzaOptions { StateDirectory = options.StateDirectory, LoginMethod = options.LoginMethod, RequestsPerSecond = 10, Burst = 20 },
                secrets, logger, redactor, TimeProvider.System, _server, prompt),
            _ => FakeSecrets.Store())
        { Time = _time };
        var output = new StringWriter();
        var error = new StringWriter();
        int code = QaCli.Run(args, output, error, services);
        return (code, output.ToString(), error.ToString());
    }

    private (int Code, string Output, string Error) Status() =>
        Qa("status", "--config-dir", Config, "--store", Store, "--state-dir", State, "--audit-dir", Audit, "--kill-file", KillFile,
            "--promotion-dir", Path.Combine(_root, "promotion"), "--ledger", Ledger);

    private static string[] Steps(string output) =>
        [.. output[output.IndexOf("Next steps:", StringComparison.Ordinal)..].Split('\n').Skip(1).Select(l => l.Trim()).TakeWhile(l => l.Length > 0)];

    [Fact]
    public void Status_OnAFreshSetup_SaysWhatIsMissing_InTheOrderToFixIt_AndWritesNothing()
    {
        (int code, string output, string error) = Status();
        Assert.True(code == 0, error);
        Assert.Contains("QuantAnalyst status, Saturday 2026-09-26 12:00 Stockholm time", output, StringComparison.Ordinal);
        Assert.Contains("ok    Kill switch", output, StringComparison.Ordinal);
        Assert.Contains("for 5,000 SEK: orders up to 500 SEK, 1,000 SEK per name, loss stop at -100 SEK a day", output, StringComparison.Ordinal);
        Assert.Contains("todo  Allowlist", output, StringComparison.Ordinal);
        Assert.Contains("todo  Strategy", output, StringComparison.Ordinal);
        Assert.Contains("0 of 10 clean Paper days", output, StringComparison.Ordinal);

        string[] steps = Steps(output);
        Assert.StartsWith("1. Choose what may be traded (SEK shares): qa history import ERIC-B, then qa universe add ERIC-B", steps[0], StringComparison.Ordinal);
        Assert.Contains("qa paper strategy ma-cross --param fast=20 --param slow=100", steps[1], StringComparison.Ordinal);
        Assert.DoesNotContain(steps, s => s.Contains("qa paper run", StringComparison.Ordinal)); // not before the setup is done

        Assert.Equal(["config"], Directory.GetFileSystemEntries(_root).Select(Path.GetFileName)); // read-only
    }

    [Fact]
    public void Status_WhenReady_SaysWhenToStartTheNextSession_AndAnActiveKillComesFirst()
    {
        Assert.Equal(0, Qa("history", "import", "ERIC-B", "--from", "2026-09-24", "--to", "2026-09-25", "--store", Store, "--state-dir", State, "--login", "totp").Code);
        Assert.Equal(0, Qa("universe", "add", "ERIC-B", "--config-dir", Config, "--store", Store).Code);
        Assert.Equal(0, Qa("paper", "strategy", "buy-and-hold", "--config-dir", Config, "--ledger", Ledger).Code);

        (int code, string output, _) = Status();
        Assert.Equal(0, code);
        Assert.Contains("ok    Allowlist      1: ERIC B", output, StringComparison.Ordinal);
        Assert.Contains("ERIC B 2 bars to 2026-09-25", output, StringComparison.Ordinal);
        Assert.Contains("warn  Strategy       buy-and-hold(entry=5), not yet backtested on imported history", output, StringComparison.Ordinal);
        Assert.Contains("warn  Invested       at most 20 % of the account: R7 allows 20 % per name and the allowlist has 1", output, StringComparison.Ordinal);
        string[] steps = Steps(output);
        Assert.StartsWith("1. Next session: Monday 2026-09-28. Start it that morning before 09:10: qa paper run", steps[0], StringComparison.Ordinal);
        Assert.Contains(steps, s => s.Contains("qa backtest run --strategy buy-and-hold --param entry=5", StringComparison.Ordinal));

        // Monday 08:30: today's session, before the decision time.
        _time.SetUtcNow(new DateTimeOffset(2026, 9, 28, 6, 30, 0, TimeSpan.Zero));
        Assert.StartsWith("1. Start today's session before 09:10: qa paper run", Steps(Status().Output)[0], StringComparison.Ordinal);

        Assert.Equal(0, Qa("kill", "--reason", "test", "--kill-file", KillFile, "--state-dir", State, "--audit-dir", Audit).Code);
        (_, output, _) = Status();
        Assert.Contains("FAIL  Kill switch", output, StringComparison.Ordinal);
        steps = Steps(output);
        Assert.Contains("qa kill --reset", steps[0], StringComparison.Ordinal);
        Assert.DoesNotContain(steps, s => s.Contains("qa paper run", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_FlagsAShareThatDoesNotTradeContinuously_AndTheIntradayDaysStillToCatchUp()
    {
        // Plan 18, P5: a share off the main market would stop every Paper decision. P6: missed intraday evenings.
        string ticks = InstrumentRecord.CanonicalTickTable(new TickSizeTable([new TickSizeBand(0m, 99_999m, 0.01m)]));
        using (HistoryStore store = HistoryStore.Open(Store))
        {
            store.UpsertInstrument(new InstrumentRecord(new OrderbookId("5240"), null, "ERIC B", "Ericsson B", "SEK", "XSTO", "STOCK", TradingModel.Continuous, 1m, ticks, new DateOnly(2026, 9, 1)),
                "test", "test", Saturday);
            store.UpsertInstrument(new InstrumentRecord(new OrderbookId("9999"), null, "SMALL", "Small AB", "SEK", "TEST-MARKET", "STOCK", TradingModel.Unknown, 1m, ticks, new DateOnly(2026, 9, 1)),
                "test", "test", Saturday);
            store.RegisterSource(AvanzaChartImporter.AvanzaPriceChart);
            Bar[] bars = [.. IntradayDays.Select(d => new Bar(new DateTimeOffset(2026, 9, d, 7, 0, 0, TimeSpan.Zero), 100m, 101m, 99m, 100m, 1_000))];
            store.UpsertIntradayBars(new OrderbookId("5240"), ChartResolution.FiveMinutes, bars, AvanzaChartImporter.AvanzaPriceChart, "test", Saturday);
        }

        File.WriteAllText(Path.Combine(Config, "universe.json"),
            """{ "format": "qa-universe/1", "instruments": [ { "orderbook_id": "5240", "ticker": "ERIC B", "name": "Ericsson B" }, { "orderbook_id": "9999", "ticker": "SMALL", "name": "Small AB" } ] }""");

        (int code, string output, string error) = Status();
        Assert.True(code == 0, error);
        Assert.Contains("FAIL  Allowlist", output, StringComparison.Ordinal);
        Assert.Contains("SMALL is listed on 'TEST-MARKET', not Nasdaq Stockholm's main market (XSTO), and it can't be told yet whether it trades continuously", output, StringComparison.Ordinal);
        Assert.Contains(
            "warn  Intraday bars  collected to 2026-09-24; missing 2026-09-23, 2026-09-25 (the catch-up still reaches them as 10-minute bars); lost 2026-09-15, 2026-09-16, 2026-09-17, 2026-09-18 (older than a week)",
            output, StringComparison.Ordinal);

        string[] steps = Steps(output);
        Assert.StartsWith("1. Take SMALL off the allowlist: qa universe remove SMALL", steps[0], StringComparison.Ordinal);
        Assert.Contains(steps, s => s.EndsWith("Catch up the missed intraday day(s) while Avanza still has them (until 2026-09-30 for the first): qa intraday import", StringComparison.Ordinal));
        Assert.DoesNotContain(steps, s => s.Contains("qa paper run", StringComparison.Ordinal)); // every decision would fail
    }

    [Fact]
    public void ReportWeek_ComparesThePaperWeekWithTheRecordedBacktest_AndShowsTheIntradayCollection()
    {
        // Plan 20. Saturday after the week: two Paper days (Mon +0.20 %, Tue -0.40 %, about half invested), none after.
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero));
        File.WriteAllText(Path.Combine(Config, "universe.json"),
            """{ "format": "qa-universe/1", "instruments": [ { "orderbook_id": "5240", "ticker": "ERIC B", "name": "Ericsson B" } ] }""");
        Assert.Equal(0, Qa("paper", "strategy", "buy-and-hold", "--config-dir", Config, "--ledger", Ledger).Code);
        new Analytics.Backtesting.TrialLedger(Ledger).Append(new Analytics.Backtesting.TrialRecord
        {
            Id = "-",
            RecordedAtUtc = Saturday,
            Runner = "owner",
            Study = "s",
            Strategy = "buy-and-hold",
            Parameters = new Dictionary<string, string> { ["entry"] = "5" },
            Universe = ["ERIC B"],
            DataSource = AvanzaChartImporter.AvanzaPriceChart.Name,
            PointInTime = false,
            SurvivorshipFree = false,
            From = new DateOnly(2016, 1, 4),
            To = new DateOnly(2026, 9, 25),
            Seed = 1,
            CostModel = "avanza-start",
            CostsVerified = true,
            HoldoutTouched = false,
            Status = Analytics.Backtesting.TrialStatus.Ok,
            Metrics = new Analytics.Backtesting.TrialMetrics(500, 0.05, 0.79, 0, 3, 0.1, 0.05, 0.16, 0.2, 1, 100, 0.9, null, 1, null),
        });
        // Plan 24: ERIC B closes at 70.00, then 70.70 with a 0.35 dividend going ex on Tuesday: the list made 1.5 % on Tuesday.
        foreach ((int day, decimal start, decimal end, decimal cash, decimal close) in new[] { (28, 5000m, 5010m, 2500m, 70m), (29, 5010m, 4990m, 2490m, 70.7m) })
        {
            var log = new Trading.Audit.AuditLog(Audit, new FakeTimeProvider(new DateTimeOffset(2026, 9, day, 7, 0, 0, TimeSpan.Zero)));
            log.Append("session-start", new { mode = "Paper" });
            log.Append("close-mark", new { orderbookId = "5240", ticker = "ERIC B", currency = "SEK", last = close, sekPerUnit = 1m });
            log.Append("end-of-day", new { day = new { startOfDayValue = start, accountValue = end, cash, feesPaid = 0m } });
        }

        string ticks = InstrumentRecord.CanonicalTickTable(new TickSizeTable([new TickSizeBand(0m, 99_999m, 0.01m)]));
        using (HistoryStore store = HistoryStore.Open(Store))
        {
            store.UpsertInstrument(new InstrumentRecord(new OrderbookId("5240"), null, "ERIC B", "Ericsson B", "SEK", "XSTO", "STOCK", TradingModel.Continuous, 1m, ticks, new DateOnly(2026, 9, 1)),
                "test", "test", Saturday);
            Bar Nine(int d) => new(new DateTimeOffset(2026, 9, d, 7, 0, 0, TimeSpan.Zero), 70m, 71m, 69m, 70m, 1_000);
            store.RegisterSource(AvanzaChartImporter.AvanzaPriceChart);
            store.UpsertIntradayBars(new OrderbookId("5240"), ChartResolution.FiveMinutes, [Nine(28)], AvanzaChartImporter.AvanzaPriceChart, "test", Saturday);
            store.UpsertIntradayBars(new OrderbookId("5240"), ChartResolution.TenMinutes, [Nine(29)], AvanzaChartImporter.AvanzaPriceChart, "test", Saturday);
            store.RegisterSource(CorporateDataImporter.AvanzaStockDetails);
            store.UpsertDividends(new OrderbookId("5240"), [new DividendEvent(new DateOnly(2026, 9, 29), null, 0.35m, "SEK", "ORDINARY")], CorporateDataImporter.AvanzaStockDetails, "test", Saturday);

            // Plan 24 B: the market index's closes (as 'qa benchmark import' stores them), Monday 2,000 and Tuesday 2,010.
            store.UpsertDailyBars(new OrderbookId("19002"), [new DailyBar(new DateOnly(2026, 9, 28), 2000m, 2000m, 2000m, 2000m, 0), new DailyBar(new DateOnly(2026, 9, 29), 2010m, 2010m, 2010m, 2010m, 0)],
                AvanzaChartImporter.AvanzaPriceChart, "test", Saturday);
        }

        Assert.Equal(0, Qa("benchmark", "set", "--orderbook-id", "19002", "--name", "OMX Stockholm 30", "--config-dir", Config).Code);

        string policy = Path.Combine(Config, Analytics.Backtesting.IntradayHoldout.FileName);
        File.Copy(Path.Combine(RepoRoot(), "config", Analytics.Backtesting.IntradayHoldout.FileName), policy);
        int needed = Analytics.Backtesting.IntradayReport.MinDays + Analytics.Backtesting.IntradayHoldout.Load(policy).Days;

        string weeks = Path.Combine(_root, "weeks");
        string[] common = ["--audit-dir", Audit, "--weeks-dir", weeks, "--store", Store, "--config-dir", Config, "--ledger", Ledger];
        (int code, string output, string error) = Qa(["report", "week", .. common]); // default: the week of the last session
        Assert.True(code == 0, error);
        Assert.Contains("Week 2026-W40, Mon 2026-09-28 to Sun 2026-10-04", output, StringComparison.Ordinal);
        Assert.Contains("  Fri 2026-10-02  no session", output, StringComparison.Ordinal);
        Assert.Contains("Week: -0.20% (value 4,990.00 SEK), fees 0.00 SEK; 2 of 5 trading day(s) clean, 3 without a session; Confirm gate: 2 of 10 clean Paper days in a row.", output, StringComparison.Ordinal);
        Assert.Contains("Against the backtest T000001, buy-and-hold(entry=5) on ERIC B, 2016-01-04..2026-09-25, costs avanza-start", output, StringComparison.Ordinal);
        Assert.Contains("  this week: 2 day(s) at 50 % invested: Paper -0.20%; the backtest expects +0.05% (95 % range -1.35% to +1.45%): within the range", output, StringComparison.Ordinal);
        Assert.Contains("Against holding the list (equal weights, dividends included, no costs; the same close prices as Paper):", output, StringComparison.Ordinal);
        Assert.Contains("  this week: 1 day(s): Paper -0.40%; the list +1.50% (at Paper's 50 % invested +0.75%): Paper 1.15 points behind at the same exposure", output, StringComparison.Ordinal);
        Assert.Contains("  the market (OMX Stockholm 30) over the same days: this week +0.50% (1 day(s)), since 2026-09-29 +0.50% (1 day(s))", output, StringComparison.Ordinal);
        Assert.Contains(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Intraday bars, 5 trading day(s): ERIC B 2 (1 at 10 minutes) (missing 09-30, 10-01, 10-02); 2 day(s) collected so far of the {needed} the go/no-go needs."), output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(weeks, "2026-W40.json")));

        Assert.Contains("Week 2026-W40", Qa(["report", "week", "--week", "2026-w40", .. common]).Output, StringComparison.Ordinal);
        Assert.Contains("\"week\": \"2026-W40\"", Qa(["report", "week", "--date", "2026-10-01", "--json", .. common]).Output, StringComparison.Ordinal);
        Assert.Contains("not both", Qa(["report", "week", "--week", "2026-W40", "--date", "2026-10-01", .. common]).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Benchmark_IsSetByItsNumber_ImportedFromThePublicChart_AndShown()
    {
        // Plan 24 B: no login; the index's chart answer is checked like a share's (here the share fixture stands in).
        Assert.Contains("No benchmark set. Open the index on avanza.se", Qa("benchmark", "--config-dir", Config, "--store", Store).Output, StringComparison.Ordinal);
        Assert.Contains("--orderbook-id must be the number", Qa("benchmark", "set", "--orderbook-id", "omxs30", "--name", "x", "--config-dir", Config).Error, StringComparison.Ordinal);
        Assert.Equal(0, Qa("benchmark", "set", "--orderbook-id", "19002", "--name", "OMX Stockholm 30", "--config-dir", Config).Code);
        _server.Always(AvanzaRoutes.PriceChart, _ => FakeAvanza.Json(Fixtures.Bytes("price-chart-5240.json")));

        (int code, string output, string error) = Qa("benchmark", "import", "--from", "2026-09-20", "--config-dir", Config, "--store", Store);

        Assert.True(code == 0, output + error);
        Assert.StartsWith("Benchmark OMX Stockholm 30: closes to 2026-09-25 (2 new, 0 restated)", output, StringComparison.Ordinal);
        Assert.Contains(_server.Requests, r => r.PathAndQuery.StartsWith(AvanzaRoutes.PriceChart.Path("19002"), StringComparison.Ordinal));
        Assert.DoesNotContain(_server.Requests, r => r.Method != "GET"); // no login
        Assert.Contains("Benchmark: OMX Stockholm 30 (Avanza orderbook 19002): 2 closes, 2026-09-24 to 2026-09-25.", Qa("benchmark", "--config-dir", Config, "--store", Store).Output, StringComparison.Ordinal);
        Assert.NotEqual(0, Qa("universe", "add", "19002", "--config-dir", Config, "--store", Store).Code); // never tradeable: no instrument record
    }

    [Fact]
    public void Status_ReportsABrokenFile_AsAFailLine_NotACrash()
    {
        File.WriteAllText(Path.Combine(Config, "paper.json"), "{ not json");
        (int code, string output, _) = Status();
        Assert.Equal(0, code);
        Assert.Contains("FAIL  Paper account", output, StringComparison.Ordinal);
        Assert.StartsWith("1. Fix paper account:", Steps(output)[0], StringComparison.Ordinal);

        // A cost file that is missing (paper.json names one that isn't there) is a FAIL line too, not a crash.
        File.WriteAllText(Path.Combine(Config, "paper.json"), """{ "format": "qa-paper/1", "costs": "avanza-nonesuch", "cash": 5000, "decision_time": "09:10" }""");
        (code, output, _) = Status();
        Assert.Equal(0, code);
        Assert.Matches(@"FAIL  Paper account\s+Cost model .*costs\.avanza-nonesuch\.json not found", output);
    }

    [Fact]
    public void PaperStrategy_SavesShowsAndClears_AndRefusesInvalidOnes()
    {
        string paperJson = Path.Combine(Config, "paper.json");
        (int code, string output, _) = Qa("paper", "strategy", "--config-dir", Config);
        Assert.Equal(0, code);
        Assert.Contains("No strategy saved", output, StringComparison.Ordinal);

        (code, output, string error) = Qa("paper", "strategy", "ma-cross", "--param", "fast=20", "--param", "slow=100", "--config-dir", Config, "--ledger", Ledger);
        Assert.True(code == 0, error);
        Assert.Contains("Saved ma-cross(fast=20, slow=100)", output, StringComparison.Ordinal);
        Assert.Contains("qa backtest run --strategy ma-cross --param fast=20 --param slow=100", output, StringComparison.Ordinal); // not backtested yet
        Assert.Contains("\"note\": \"kept\"", File.ReadAllText(paperJson), StringComparison.Ordinal);

        Assert.Contains("Saved strategy: ma-cross(fast=20, slow=100)", Qa("paper", "strategy", "--config-dir", Config).Output, StringComparison.Ordinal);

        string before = File.ReadAllText(paperJson);
        (code, _, error) = Qa("paper", "strategy", "ma-cross", "--param", "fast=200", "--param", "slow=100", "--config-dir", Config);
        Assert.Equal(1, code);
        Assert.Contains("need 1 <= fast < slow", error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(paperJson));
        Assert.Contains("Unknown strategy", Qa("paper", "strategy", "moon", "--config-dir", Config).Error, StringComparison.Ordinal);

        Assert.Equal(0, Qa("paper", "strategy", "--clear", "--config-dir", Config).Code);
        Assert.Contains("No strategy saved", Qa("paper", "strategy", "--config-dir", Config).Output, StringComparison.Ordinal);
    }

    [Fact]
    public void PaperRun_WithoutAStrategy_StopsBeforeAnyLogin_AndSaysHowToSaveOne()
    {
        File.WriteAllText(Path.Combine(Config, "universe.json"), """{ "format": "qa-universe/1", "instruments": [ { "orderbook_id": "5240", "ticker": "ERIC B", "name": "Ericsson B" } ] }""");
        (int code, _, string error) = Qa("paper", "run", "--config-dir", Config, "--store", Store, "--state-dir", State, "--audit-dir", Audit,
            "--kill-file", KillFile, "--promotion-dir", Path.Combine(_root, "promotion"), "--login", "totp");
        Assert.Equal(1, code);
        Assert.Contains("No strategy: save one once with 'qa paper strategy ma-cross", error, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);

        (code, _, error) = Qa("paper", "run", "--param", "fast=5", "--config-dir", Config, "--login", "totp");
        Assert.Equal(1, code);
        Assert.Contains("--param needs --strategy", error, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public void RootHelp_PointsAtQaStatus()
    {
        (_, string output, _) = Qa("--help");
        Assert.Contains("Start with: qa status", output, StringComparison.Ordinal);
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
