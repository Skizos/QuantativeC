using Microsoft.Extensions.Time.Testing;
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
        Assert.Contains("SMALL is listed on 'TEST-MARKET', not Nasdaq Stockholm's main market (XSTO), and its trading model is Unknown", output, StringComparison.Ordinal);
        Assert.Contains(
            "warn  Intraday bars  collected to 2026-09-24; missing 2026-09-23, 2026-09-25 (the catch-up still reaches them as 10-minute bars); lost 2026-09-15, 2026-09-16, 2026-09-17, 2026-09-18 (older than a week)",
            output, StringComparison.Ordinal);

        string[] steps = Steps(output);
        Assert.StartsWith("1. Take SMALL off the allowlist: qa universe remove SMALL", steps[0], StringComparison.Ordinal);
        Assert.Contains(steps, s => s.EndsWith("Catch up the missed intraday day(s) while Avanza still has them (until 2026-09-30 for the first): qa intraday import", StringComparison.Ordinal));
        Assert.DoesNotContain(steps, s => s.Contains("qa paper run", StringComparison.Ordinal)); // every decision would fail
    }

    [Fact]
    public void Status_ReportsABrokenFile_AsAFailLine_NotACrash()
    {
        File.WriteAllText(Path.Combine(Config, "paper.json"), "{ not json");
        (int code, string output, _) = Status();
        Assert.Equal(0, code);
        Assert.Contains("FAIL  Paper account", output, StringComparison.Ordinal);
        Assert.StartsWith("1. Fix paper account:", Steps(output)[0], StringComparison.Ordinal);
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
