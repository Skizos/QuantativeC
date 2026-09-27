using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Observation;
using QuantAnalyst.Trading.Oms;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// The Paper spy (plan 06 gate): a whole <c>qa paper run</c> against the fake Avanza server, on a fake clock through
/// Monday 2026-09-28's decision time, with live depth pushed so orders really are placed and filled on paper. The
/// server records every request: <b>none</b> may go to an order route, and only login steps may be POSTs.
/// </summary>
public sealed class PaperSpyTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 7, 9, 40, TimeSpan.Zero); // 09:09:40 Stockholm

    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-paper-spy", Guid.NewGuid().ToString("N"));
    private readonly FakeAvanza _server = new();
    private readonly FakeTimeProvider _time = new(Start);

    public PaperSpyTests()
    {
        Directory.CreateDirectory(Config);
        string repo = Path.Combine(RepoRoot(), "config");
        foreach (string f in new[] { "risk-limits.json", "costs.avanza-start.json", "market-calendar.XSTO.2026.json", "market-calendar.XSTO.2027.json" })
        {
            File.Copy(Path.Combine(repo, f), Path.Combine(Config, f));
        }

        File.WriteAllText(Path.Combine(Config, "paper.json"), """{ "format": "qa-paper/1", "costs": "avanza-start", "cash": 45000, "decision_time": "09:10" }""");
        File.WriteAllText(Path.Combine(Config, "universe.json"), """{ "format": "qa-universe/1", "instruments": [] }""");
    }

    private string Config => Path.Combine(_root, "config");

    private string Store => Path.Combine(_root, "q.duckdb");

    /// <summary>The observer the next <c>qa paper run</c> reports to (the Windows app's seam); none by default, like the terminal.</summary>
    private ISessionObserver? _observer;

    private string State => Path.Combine(_root, "state");

    private string Audit => Path.Combine(_root, "audit");

    private string KillFile => Path.Combine(_root, "KILL");

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

    private (int Code, string Output, string Error) Qa(TimeProvider time, params string[] args)
    {
        var services = new AvanzaCliServices(
            (options, secrets, prompt, logger, redactor) => AvanzaConnection.CreateForTest(
                new AvanzaOptions { StateDirectory = options.StateDirectory, LoginMethod = options.LoginMethod, RequestsPerSecond = 10, Burst = 20 },
                secrets, logger, redactor, time, _server, prompt),
            _ => FakeSecrets.Store())
        { Time = time, SessionObserver = _observer };
        var output = new StringWriter();
        var error = new StringWriter();
        int code = QaCli.Run(args, output, error, services);
        return (code, output.ToString(), error.ToString());
    }

    private void PrepareHistoryAndUniverse()
    {
        Assert.Equal(0, Qa(TimeProvider.System, "history", "import", "ERIC-B", "--from", "2026-09-24", "--to", "2026-09-25", "--store", Store,
            "--state-dir", State, "--login", "totp").Code);
        Assert.Equal(0, Qa(TimeProvider.System, "universe", "add", "ERIC-B", "--config-dir", Config, "--store", Store).Code);
    }

    private string[] PaperArgs(double seconds, bool savedStrategy = false) =>
        ["paper", "run", .. savedStrategy ? Array.Empty<string>() : ["--strategy", "buy-and-hold"], "--duration", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
         "--config-dir", Config, "--store", Store, "--state-dir", State, "--audit-dir", Audit, "--kill-file", KillFile,
         "--promotion-dir", Path.Combine(_root, "promotion"), "--reports-dir", Path.Combine(_root, "reports"), "--login", "totp"];

    /// <summary>Runs the CLI on a worker while this thread moves the fake clock and keeps the depth stream alive.</summary>
    private async Task<(int Code, string Output, string Error)> RunPaper(double seconds, Action<DateTimeOffset>? onTick = null, bool savedStrategy = false)
    {
        var conn = new SseConnection();
        _server.Serve(conn);
        await conn.Event("info", "connected", "e0", 1000);
        Task<(int, string, string)> run = Task.Run(() => Qa(_time, PaperArgs(seconds, savedStrategy)), TestContext.Current.CancellationToken);

        // Login and setup need no clock; hold the fake clock until the session streams (or stops), so it starts at
        // 09:09:40 however slow the machine is.
        await Task.WhenAny(conn.Connected.Task, run).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        int step = 0;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!run.IsCompleted && DateTime.UtcNow < deadline)
        {
            if (step++ % 8 == 0)
            {
                await conn.Depth("5240", 70.84m, 70.86m, $"e{step}");
            }

            await Task.Delay(15, TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromMilliseconds(500));
            onTick?.Invoke(_time.GetUtcNow());
        }

        Assert.True(run.IsCompleted, "qa paper run did not finish");
        return await run;
    }

    [Fact]
    public async Task AnObserver_SeesTheDayQuotesDecisionFillsAndValue_AndTheSessionIsTheSame()
    {
        PrepareHistoryAndUniverse();
        var seen = new RecordingObserver();
        _observer = seen;

        (int code, string output, string error) = await RunPaper(seconds: 60);

        Assert.True(code == 0, output + error);
        Assert.True(output.Contains("Buy 7 ERIC B: Accepted (Filled, filled 7/7 @ 70.86)", StringComparison.Ordinal), output); // as without an observer

        SessionStarted started = Assert.Single(seen.Of<SessionStarted>());
        Assert.StartsWith("buy-and-hold", started.Strategy, StringComparison.Ordinal);
        ObservedInstrument eric = Assert.Single(started.Instruments);
        Assert.Equal(("5240", "ERIC B"), (eric.OrderbookId.Value, eric.Ticker));
        Assert.NotNull(eric.PreviousClose); // Friday's stored close
        Assert.NotNull(started.OpenUtc);

        QuoteTick[] quotes = seen.Of<QuoteTick>();
        Assert.NotEmpty(quotes);
        Assert.All(quotes, q => Assert.Equal("5240", q.OrderbookId.Value));
        Assert.All(quotes.Zip(quotes.Skip(1)), p => Assert.True(p.Second.AtUtc - p.First.AtUtc >= TimeSpan.FromSeconds(1))); // at most one a second

        DecisionTick decision = Assert.Single(seen.Of<DecisionTick>());
        Assert.Equal(1, decision.Orders);
        Assert.Contains(seen.Of<OrderTick>(), o => o.State == OmsState.Filled && o.Filled == 7 && o.AveragePrice == 70.86m);

        AccountTick[] values = seen.Of<AccountTick>();
        Assert.True(values.Length >= 2, "the account at the start and at the end at least");
        Assert.Contains(values, a => a.Positions.Any(p => p.Ticker == "ERIC B" && p.Quantity == 7));
    }

    [Fact]
    public async Task AnObserverThatThrows_ChangesNothing()
    {
        PrepareHistoryAndUniverse();
        _observer = new ThrowingObserver();

        (int code, string output, string error) = await RunPaper(seconds: 60);

        Assert.True(code == 0, output + error);
        Assert.True(output.Contains("Buy 7 ERIC B: Accepted (Filled, filled 7/7 @ 70.86)", StringComparison.Ordinal), output);
        Assert.Contains("Reconciliation: clean", output, StringComparison.Ordinal);
    }

    private sealed class RecordingObserver : ISessionObserver
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<object> _events = new();

        public T[] Of<T>() => [.. _events.OfType<T>()];

        public void Started(SessionStarted e) => _events.Enqueue(e);

        public void Quote(QuoteTick e) => _events.Enqueue(e);

        public void Account(AccountTick e) => _events.Enqueue(e);

        public void Order(OrderTick e) => _events.Enqueue(e);

        public void Decision(DecisionTick e) => _events.Enqueue(e);
    }

    private sealed class ThrowingObserver : ISessionObserver
    {
        public void Started(SessionStarted e) => throw new InvalidOperationException("broken");

        public void Quote(QuoteTick e) => throw new InvalidOperationException("broken");

        public void Account(AccountTick e) => throw new InvalidOperationException("broken");

        public void Order(OrderTick e) => throw new InvalidOperationException("broken");

        public void Decision(DecisionTick e) => throw new InvalidOperationException("broken");
    }

    [Fact]
    public async Task APaperSession_PlacesAndFillsOrders_WithoutASingleOrderRouteRequest()
    {
        PrepareHistoryAndUniverse();
        int before = _server.Requests.Count;

        (int code, string output, string error) = await RunPaper(seconds: 60);

        Assert.True(code == 0, output + error);
        Assert.DoesNotContain("History:", output, StringComparison.Ordinal); // imported through Friday already
        Assert.Contains("decision: 1 order(s)", output, StringComparison.Ordinal);
        Assert.Contains("sized on the 5,000 SEK account cap, not the account's 45,000 SEK", output, StringComparison.Ordinal);
        Assert.True(output.Contains("Buy 7 ERIC B: Accepted (Filled, filled 7/7 @ 70.86)", StringComparison.Ordinal), output); // R6: 500 SEK, 10 % of the cap
        Assert.Contains("Reconciliation: clean", output, StringComparison.Ordinal);
        Assert.Contains("Report (partial day): 2026-09-28 INCOMPLETE: 1 sent, 1 accepted", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "reports", "2026-09-28.json")));

        RecordedRequest[] session = [.. _server.Requests.Skip(before)];
        Assert.NotEmpty(session);
        Assert.DoesNotContain(session, r => AvanzaOrderRoutes.All.Any(route => r.PathAndQuery.StartsWith(route.PathTemplate, StringComparison.Ordinal)));
        Assert.DoesNotContain(session, r => r.PathAndQuery.Contains("order-entry", StringComparison.OrdinalIgnoreCase));
        Assert.All(session.Where(r => r.Method != "GET"), r => Assert.Contains(r.PathAndQuery, new[] { AvanzaRoutes.UserCredentials.Path(), AvanzaRoutes.Totp.Path() }));

        using JsonDocument book = JsonDocument.Parse(File.ReadAllText(Path.Combine(State, "paper", "book.json")));
        Assert.Equal(7, book.RootElement.GetProperty("positions")[0].GetProperty("quantity").GetInt64());
        Assert.True(AuditLog.Verify(Audit).Valid);
        Assert.False(File.Exists(Path.Combine(State, "session.lock"))); // released
    }

    [Fact]
    public async Task ASavedStrategy_AndAnAllowlist_AreEnough_TheSessionImportsTheHistoryItNeeds()
    {
        File.WriteAllText(Path.Combine(Config, "universe.json"), """{ "format": "qa-universe/1", "instruments": [ { "orderbook_id": "5240", "ticker": "ERIC B", "name": "Ericsson B" } ] }""");
        Assert.Equal(0, Qa(TimeProvider.System, "paper", "strategy", "buy-and-hold", "--config-dir", Config, "--ledger", Path.Combine(_root, "ledger.jsonl")).Code);
        Assert.False(File.Exists(Store));

        (int code, string output, string error) = await RunPaper(seconds: 60, savedStrategy: true);

        Assert.True(code == 0, output + error);
        Assert.Contains("Paper session: buy-and-hold(entry=5) on ERIC B", output, StringComparison.Ordinal);
        Assert.Contains("History: ERIC B brought up to 2026-09-25 (2 new bar(s), 0 restated).", output, StringComparison.Ordinal);
        Assert.Contains("decision: 1 order(s)", output, StringComparison.Ordinal);
        Assert.DoesNotContain(_server.Requests, r => AvanzaOrderRoutes.All.Any(route => r.PathAndQuery.StartsWith(route.PathTemplate, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AFailedHistoryRefresh_IsAWarning_AndTheDecisionRefusesStaleHistory()
    {
        Assert.Equal(0, Qa(TimeProvider.System, "history", "import", "ERIC-B", "--from", "2026-09-24", "--to", "2026-09-24", "--store", Store,
            "--state-dir", State, "--login", "totp").Code);
        Assert.Equal(0, Qa(TimeProvider.System, "universe", "add", "ERIC-B", "--config-dir", Config, "--store", Store).Code);
        _server.On(AvanzaRoutes.PriceChart, _ => FakeAvanza.Status(System.Net.HttpStatusCode.OK, """{ "surprise": true }"""));

        (int code, string output, string error) = await RunPaper(seconds: 40);

        Assert.True(code == 0, output + error);
        Assert.Contains("WARNING: the history could not be brought up to date", output, StringComparison.Ordinal);
        Assert.Contains("decision failed: the history ends 2026-09-24, not on the last trading day 2026-09-25", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Accepted", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QaKill_DuringASession_HaltsIt_AndTheExitCodeSaysSo()
    {
        PrepareHistoryAndUniverse();
        bool written = false;
        (int code, string output, _) = await RunPaper(seconds: 40, onTick: now =>
        {
            if (!written && now >= Start.AddSeconds(5)) // before the 09:10 decision
            {
                written = true;
                Assert.Equal(0, Qa(TimeProvider.System, "kill", "--reason", "spy test", "--kill-file", KillFile, "--state-dir", State, "--audit-dir", Audit).Code);
            }
        });

        Assert.Equal(AvanzaCommands.ExitHalt, code);
        Assert.Contains("ALERT: KILL SWITCH", output, StringComparison.Ordinal);
        Assert.Contains("decision skipped: trading is halted", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Accepted", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnActiveKillSwitch_RefusesToStartASession()
    {
        PrepareHistoryAndUniverse();
        Assert.Equal(0, Qa(TimeProvider.System, "kill", "--reason", "left over", "--kill-file", KillFile, "--state-dir", State, "--audit-dir", Audit).Code);
        (int code, string output, _) = await RunPaper(seconds: 30);
        Assert.Equal(AvanzaCommands.ExitHalt, code);
        Assert.Contains("The kill switch is active (file KILL", output, StringComparison.Ordinal);
        Assert.DoesNotContain("decision", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(State, "session.lock")));
    }

    [Fact]
    public void Setup_IsCheckedBeforeAnyLogin()
    {
        (int code, _, string error) = Qa(_time, PaperArgs(10));
        Assert.Equal(1, code);
        Assert.Contains("allowlist is empty", error, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);

        Directory.CreateDirectory(Path.Combine(_root, "promotion"));
        File.WriteAllText(Path.Combine(_root, "promotion", "state.json"), """{ "maxAllowed": "Backtest", "records": [] }""");
        (code, _, error) = Qa(_time, PaperArgs(10));
        Assert.Equal(1, code);
        Assert.Contains("above the promotion state", error, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);
    }

    internal static string RepoRoot()
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
