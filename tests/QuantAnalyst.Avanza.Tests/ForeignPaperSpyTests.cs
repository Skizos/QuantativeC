using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Trading.Audit;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// ADR 0005 / plan 16 step 6: <c>qa paper run</c> with a Swedish and a US share, against the fake Avanza server on a fake
/// clock from Monday 2026-09-28 15:39:40 Stockholm (09:39:40 New York). The FX fixing is read at the start, Stockholm
/// decides at once (a late start, its window still open), New York at 09:40 its time, and nothing goes to an order route.
/// </summary>
public sealed class ForeignPaperSpyTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 13, 39, 40, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-foreign-spy", Guid.NewGuid().ToString("N"));
    private readonly FakeAvanza _server = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly FxCliTests.FakeFx _fx = new();

    public ForeignPaperSpyTests()
    {
        Directory.CreateDirectory(Config);
        string repo = Path.Combine(PaperSpyTests.RepoRoot(), "config");
        foreach (string f in new[]
                 {
                     "costs.avanza-start.json", "costs.avanza-mini.json", "market-calendar.XSTO.2026.json", "market-calendar.XSTO.2027.json", "market-calendar.XNYS.2026.json",
                     "market-calendar.XNYS.2027.json",
                 })
        {
            File.Copy(Path.Combine(repo, f), Path.Combine(Config, f));
        }

        // The repository's limits with a 45,000 SEK account cap, so one order (10 %) can hold a few US shares.
        File.WriteAllText(Path.Combine(Config, "risk-limits.json"),
            File.ReadAllText(Path.Combine(repo, "risk-limits.json")).Replace("\"max_account_value_sek\": 5000", "\"max_account_value_sek\": 45000", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(Config, "paper.json"), """{ "format": "qa-paper/1", "costs": "avanza-start", "cash": 45000, "decision_time": "09:10" }""");
        File.WriteAllText(Path.Combine(Config, "universe.json"), """{ "format": "qa-universe/1", "instruments": [] }""");

        _server.Always(AvanzaRoutes.Orderbook, r =>
        {
            string id = r.RequestUri!.AbsolutePath.Split('/')[^1];
            return FakeAvanza.Json(id == "4478" ? FxCliTests.UsOrderbook(id) : FxCliTests.Body("033-orderbook"));
        });
        _server.Always(AvanzaRoutes.PriceChart, _ => FakeAvanza.Json(FxCliTests.Body("035-price-chart")));
        _server.Always(AvanzaRoutes.MarketData, _ => FakeAvanza.Json(FxCliTests.Body("034-marketdata")));
    }

    private string Config => Path.Combine(_root, "config");

    private string Store => Path.Combine(_root, "q.duckdb");

    private string State => Path.Combine(_root, "state");

    private string Audit => Path.Combine(_root, "audit");

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
        {
            Time = time,
            FxRates = () => _fx,
        };
        var output = new StringWriter();
        var error = new StringWriter();
        int code = QaCli.Run(args, output, error, services);
        return (code, output.ToString(), error.ToString());
    }

    private void PrepareHistoryAndUniverse()
    {
        Assert.Equal(0, Qa(TimeProvider.System, "history", "import", "ERIC-B", "--from", "2026-09-24", "--to", "2026-09-25", "--store", Store,
            "--state-dir", State, "--login", "totp").Code);
        Assert.Equal(0, Qa(TimeProvider.System, "history", "import", "--id", "4478", "--from", "2026-09-24", "--to", "2026-09-25", "--store", Store,
            "--state-dir", State, "--login", "totp").Code);
        Assert.Equal(0, Qa(TimeProvider.System, "universe", "add", "ERIC-B", "--config-dir", Config, "--store", Store).Code);
        Assert.Equal(0, Qa(TimeProvider.System, "universe", "add", "AAPL", "--config-dir", Config, "--store", Store).Code);
        _fx.Calls.Clear();
    }

    [Fact]
    public void Status_ShowsEachMarketsDecision_AndTheEndAfterNewYorksClose()
    {
        PrepareHistoryAndUniverse();
        Assert.Equal(0, Qa(TimeProvider.System, "paper", "strategy", "buy-and-hold", "--config-dir", Config, "--ledger", Path.Combine(_root, "ledger.jsonl")).Code);

        string FirstStep(DateTimeOffset now)
        {
            (int code, string output, string error) = Qa(new FakeTimeProvider(now), "status", "--config-dir", Config, "--store", Store, "--state-dir", State, "--audit-dir", Audit,
                "--kill-file", Path.Combine(_root, "KILL"), "--promotion-dir", Path.Combine(_root, "promotion"), "--ledger", Path.Combine(_root, "ledger.jsonl"));
            Assert.True(code == 0, error);
            Assert.Contains("warn  Calendar XNYS", output, StringComparison.Ordinal);
            string steps = output[output.IndexOf("Next steps:", StringComparison.Ordinal)..];
            return steps.Split('\n')[1].Trim();
        }

        Assert.Equal(
            "1. Start today's session before 09:10: qa paper run. It decides per market (XSTO at 09:10, XNYS at 15:40), trades, and ends after the 22:00 close; keep its window open.",
            FirstStep(new DateTimeOffset(2026, 9, 28, 6, 30, 0, TimeSpan.Zero)));
        Assert.Equal(
            "1. Today's session can still start: qa paper run. It decides per market (XNYS at once) and ends after the 22:00 close; keep its window open.",
            FirstStep(new DateTimeOffset(2026, 9, 28, 16, 0, 0, TimeSpan.Zero))); // 18:00 Stockholm: only New York is still open
        Assert.Equal(
            "1. Next session: Tuesday 2026-09-29. Start it that morning before 09:10: qa paper run",
            FirstStep(new DateTimeOffset(2026, 9, 28, 20, 30, 0, TimeSpan.Zero)));
    }

    /// <summary>
    /// Runs the CLI on a worker while this thread moves the fake clock and keeps the depth streams alive (a fresh stream
    /// for every connect, so a reconnect after a clock jump finds one). Without <paramref name="seconds"/> the session
    /// runs to its default stop (the last close + 2 minutes).
    /// </summary>
    /// <param name="usStream">False when the US share is expected to be skipped (no stream is opened for it).</param>
    private async Task<(int Code, string Output, string Error)> RunPaper(double? seconds, Action<DateTimeOffset>? onTick = null, bool usStream = true)
    {
        var streams = new Streams();
        _server.Always(AvanzaRoutes.OrderDepthStream, streams.Open);
        string[] duration = seconds is { } d ? ["--duration", d.ToString(System.Globalization.CultureInfo.InvariantCulture)] : [];
        string[] args =
        [
            "paper", "run", "--strategy", "buy-and-hold", .. duration, "--config-dir", Config, "--store", Store, "--state-dir", State, "--audit-dir", Audit,
            "--kill-file", Path.Combine(_root, "KILL"), "--promotion-dir", Path.Combine(_root, "promotion"), "--reports-dir", Path.Combine(_root, "reports"),
            "--login", "totp",
        ];
        Task<(int, string, string)> run = Task.Run(() => Qa(_time, args), TestContext.Current.CancellationToken);

        // Login and setup need no clock; hold the fake clock until the session streams (or stops).
        Task streaming = usStream ? Task.WhenAll(streams.Stockholm.Task, streams.Us.Task) : streams.Stockholm.Task;
        await Task.WhenAny(streaming, run).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        int step = 0;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (!run.IsCompleted && DateTime.UtcNow < deadline)
        {
            if (step++ % 8 == 0)
            {
                await streams.Depth(step);
            }

            await Task.Delay(15, TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromMilliseconds(500));
            onTick?.Invoke(_time.GetUtcNow());
        }

        Assert.True(run.IsCompleted, "qa paper run did not finish");
        return await run;
    }

    /// <summary>
    /// The depth streams the session opened: the latest per orderbook, each given the recorded poll's 94.96 / 94.98, so
    /// whichever quote is newer, the price is the same (R5 compares the limit with it).
    /// </summary>
    private sealed class Streams
    {
        private readonly Dictionary<string, SseConnection> _latest = new(StringComparer.Ordinal);

        public TaskCompletionSource Stockholm { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Us { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpResponseMessage Open(HttpRequestMessage request)
        {
            string id = request.RequestUri!.AbsolutePath.Split('/')[^1];
            var connection = new SseConnection();
            lock (_latest)
            {
                _latest[id] = connection;
            }

            (id == "4478" ? Us : Stockholm).TrySetResult();
            return connection.Response;
        }

        public async Task Depth(int step)
        {
            KeyValuePair<string, SseConnection>[] open;
            lock (_latest)
            {
                open = [.. _latest];
            }

            foreach ((string id, SseConnection connection) in open)
            {
                await connection.Depth(id, 94.96m, 94.98m, $"{id}-{step}");
            }
        }
    }

    [Fact]
    public async Task AMixedList_ReadsTheFixing_DecidesPerMarket_AndBuysTheUsShareInKronor_WithoutAnOrderRoute()
    {
        PrepareHistoryAndUniverse();
        int before = _server.Requests.Count;

        (int code, string output, string error) = await RunPaper(seconds: 60);

        Assert.True(code == 0, output + error);
        Assert.Contains("FX: USD 10.2700 SEK (Riksbank fixing 2026-09-28) for AAPL.", output, StringComparison.Ordinal);
        Assert.Equal("USD", Assert.Single(_fx.Calls).Currency);
        Assert.Contains("XSTO (ERIC B): decides 09:10 Stockholm, closes 17:30 Stockholm.", output, StringComparison.Ordinal);
        Assert.Contains("XNYS (AAPL): decides 09:40 New York (15:40 Stockholm), closes 22:00 Stockholm.", output, StringComparison.Ordinal);
        Assert.Matches(@"15:39:4\d XSTO decision: 1 order\(s\)\.", output); // started late, its window still open
        Assert.Contains("15:40:00 XNYS decision: 1 order(s).", output, StringComparison.Ordinal);
        Assert.Matches(@"Buy \d+ ERIC B: Accepted \(Filled, filled (\d+)/\1 @ 94\.98\)", output);
        System.Text.RegularExpressions.Match aapl = System.Text.RegularExpressions.Regex.Match(output, @"Buy (\d+) AAPL: Accepted \(Filled, filled \1/\1 @ ([\d.]+)\)");
        Assert.True(aapl.Success, output);
        Assert.Contains("Reconciliation: clean", output, StringComparison.Ordinal);

        // Plan 17 A2: the session's bid/ask samples are kept; before Stockholm's close no bars are collected.
        Assert.Matches(@"Spreads: \d+ bid/ask sample\(s\) stored for intraday research\.", output);
        Assert.DoesNotContain("Intraday:", output, StringComparison.Ordinal);

        // The paper book holds AAPL in dollars, its cost in kronor at 10.27 plus the courtage (0.25 %, at least 1 USD;
        // Start has no FX fee).
        var c = System.Globalization.CultureInfo.InvariantCulture;
        long shares = long.Parse(aapl.Groups[1].Value, c);
        decimal price = decimal.Parse(aapl.Groups[2].Value, c);
        using JsonDocument book = JsonDocument.Parse(File.ReadAllText(Path.Combine(State, "paper", "book.json")));
        JsonElement position = book.RootElement.GetProperty("positions").EnumerateArray().Single(p => p.GetProperty("ticker").GetString() == "AAPL");
        Assert.Equal("USD", position.GetProperty("currency").GetString());
        decimal value = decimal.Round(shares * price * 10.27m, 2);
        decimal courtage = decimal.Round(Math.Max(1m, 0.0025m * shares * price) * 10.27m, 2);
        Assert.Equal(value + courtage, position.GetProperty("cost_basis").GetDecimal());

        RecordedRequest[] session = [.. _server.Requests.Skip(before)];
        Assert.DoesNotContain(session, r => AvanzaOrderRoutes.All.Any(route => r.PathAndQuery.StartsWith(route.PathTemplate, StringComparison.Ordinal)));
        Assert.All(session.Where(r => r.Method != "GET"), r => Assert.Contains(r.PathAndQuery, new[] { AvanzaRoutes.UserCredentials.Path(), AvanzaRoutes.Totp.Path() }));
        Assert.True(AuditLog.Verify(Audit).Valid);
    }

    [Fact]
    public async Task AStaleFixing_SkipsTheUsShare_AndStockholmStillTrades()
    {
        _fx.Until = new DateOnly(2026, 9, 22);
        PrepareHistoryAndUniverse();

        (int code, string output, string error) = await RunPaper(seconds: 30, usStream: false);

        Assert.True(code == 0, output + error);
        Assert.Contains("WARNING: no USD/SEK fixing from the last 4 days is stored, so AAPL is skipped today (ADR 0005).", output, StringComparison.Ordinal);
        Assert.Contains("decision: 1 order(s).", output, StringComparison.Ordinal);
        Assert.DoesNotContain("XNYS decision", output, StringComparison.Ordinal);
        Assert.DoesNotContain("XNYS (AAPL)", output, StringComparison.Ordinal); // one market left: the lines read as before
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(output, @"Buy \d+ ERIC B: Accepted"), output);
        Assert.DoesNotContain("AAPL: Accepted", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutADuration_TheSessionRunsToTwoMinutesAfterNewYorksClose_AndReportsTheDayOnce()
    {
        PrepareHistoryAndUniverse();
        bool jumped = false;

        (int code, string output, string error) = await RunPaper(seconds: null, now =>
        {
            if (!jumped && now >= Start.AddSeconds(75))
            {
                jumped = true;
                _time.SetUtcNow(new DateTimeOffset(2026, 9, 28, 19, 59, 50, TimeSpan.Zero)); // 21:59:50 Stockholm
            }
        });

        Assert.True(code == 0, output + error);
        Assert.Contains("Running until 2026-09-28 22:02 (Stockholm).", output, StringComparison.Ordinal);
        Assert.Contains("XSTO close: 0 order(s) expired.", output, StringComparison.Ordinal);
        Assert.Matches(@"22:00:0\d XNYS close: 0 order\(s\) expired\. Value", output);
        Assert.Contains("Report: 2026-09-28", output, StringComparison.Ordinal);
        Assert.DoesNotContain("partial day", output, StringComparison.Ordinal);

        // Plan 17 A2: after the close today's intraday bars are collected, Stockholm shares only. This fake chart
        // answers daily bars whatever is asked, so the import refuses them, and the day is not affected.
        Assert.Contains("AAPL: skipped, intraday research is Stockholm only (ADR 0006).", output, StringComparison.Ordinal);
        Assert.Contains("ERIC B 1-minute: FAILED (Avanza answered with Day bars for Today, not Minute; nothing was stored.", output, StringComparison.Ordinal);
        Assert.Contains("2 import(s) failed", output, StringComparison.Ordinal);
    }
}
