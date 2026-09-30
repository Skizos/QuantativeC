using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Observation;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Reports;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// The Paper spy (plan 06 gate): a whole <c>qa paper run</c> against the fake Avanza server, on a fake clock through
/// Monday 2026-09-28's decision time, with polled live prices (Paper uses no depth stream since 2026-09-30) so orders
/// really are placed and filled on paper. The
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
        foreach (string f in new[] { "risk-limits.json", "costs.avanza-start.json", "costs.avanza-mini.json", "market-calendar.XSTO.2026.json", "market-calendar.XSTO.2027.json" })
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

    /// <summary>Runs the CLI on a worker while this thread moves the fake clock (the session polls ERIC B's prices: 70.84 / 70.86).</summary>
    /// <param name="setupCalls">
    /// Setup makes more calls than the test's rate-limit burst (plan 22: ten names): the clock then creeps forward by 50 ms
    /// steps until the first poll, so the token bucket refills; the session starts a few seconds after 09:09:40.
    /// </param>
    private async Task<(int Code, string Output, string Error)> RunPaper(double seconds, Action<DateTimeOffset>? onTick = null, bool savedStrategy = false, bool setupCalls = false)
    {
        int before = _server.Requests.Count;
        Task<(int, string, string)> run = Task.Run(() => Qa(_time, PaperArgs(seconds, savedStrategy)), TestContext.Current.CancellationToken);

        // Login and setup need no clock; hold the fake clock until the session polls its first price (or stops), so it
        // starts at 09:09:40 however slow the machine is.
        Task first = Task.WhenAny(FirstPoll(before), run);
        while (setupCalls && !first.IsCompleted)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromMilliseconds(50));
        }

        await first.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!run.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15, TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromMilliseconds(500));
            onTick?.Invoke(_time.GetUtcNow());
        }

        Assert.True(run.IsCompleted, "qa paper run did not finish");
        return await run;
    }

    private async Task FirstPoll(int after)
    {
        while (!_server.Requests.Skip(after).Any(r => r.PathAndQuery.StartsWith(AvanzaRoutes.MarketData.Path("5240"), StringComparison.Ordinal)))
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
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
        Assert.Contains("Live prices: polled every 5 s (Paper does not use Avanza's order-book stream, which Avanza refuses).", output, StringComparison.Ordinal);
        Assert.Equal(0, _server.CountFor(AvanzaRoutes.OrderDepthStream)); // owner's decision 2026-09-30
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
    public async Task AShareTakenOffTheListWhileHeld_IsSoldByTheNextSession()
    {
        // Plan 21: the book holds 7 ERIC B; the owner takes ERIC B off the list; the session sells it (R2: sells only).
        PrepareHistoryAndUniverse();
        string paper = Path.Combine(State, "paper");
        Directory.CreateDirectory(paper);
        File.WriteAllText(Path.Combine(paper, "book.json"), """
            { "format": "qa-paper-book/1", "account": "PAPER", "costs": "avanza-start", "starting_cash": 45000, "cash": 44504, "fees_paid": 1,
              "realized_pnl": 0, "start_of_day_value": 0, "saved_utc": "2026-09-25T15:30:00Z",
              "positions": [ { "orderbook_id": "5240", "ticker": "ERIC B", "quantity": 7, "cost_basis": 496, "last_fill_price": 70.86 } ] }
            """);
        (int code, string removed, string error) = Qa(TimeProvider.System, "universe", "remove", "ERIC-B", "--config-dir", Config, "--state-dir", State);
        Assert.True(code == 0, error);
        Assert.Contains("moves to the exiting list", removed, StringComparison.Ordinal);

        (code, string output, error) = await RunPaper(seconds: 60);

        Assert.True(code == 0, output + error);
        Assert.Contains("ERIC B: off the list, still held; this session sells it (sells only).", output, StringComparison.Ordinal);
        Assert.Contains("ERIC B: off the list (exiting), target zero", output, StringComparison.Ordinal);
        Assert.True(output.Contains("Sell 7 ERIC B: Accepted (Filled, filled 7/7 @ 70.84)", StringComparison.Ordinal), output); // at the bid
        using JsonDocument book = JsonDocument.Parse(File.ReadAllText(Path.Combine(paper, "book.json")));
        Assert.Equal(0, book.RootElement.GetProperty("positions").GetArrayLength());
        Assert.Contains("sold; take it off with qa universe remove ERIC-B",
            Qa(TimeProvider.System, "status", "--config-dir", Config, "--store", Store, "--state-dir", State, "--audit-dir", Audit, "--kill-file", KillFile,
                "--promotion-dir", Path.Combine(_root, "promotion")).Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADividend_IsCreditedAtTheSessionStart_AndTheDayReportShowsIt()
    {
        // Plan 21: the book holds 7 ERIC B since Friday's session; Avanza's details show an ex-date today.
        PrepareHistoryAndUniverse();
        WriteBook("""{ "orderbook_id": "5240", "ticker": "ERIC B", "quantity": 7, "cost_basis": 496, "last_fill_price": 70.86 }""", corporateThrough: "2026-09-25");
        _server.Always(AvanzaRoutes.StockDetails, _ => FakeAvanza.Json(Fixtures.Mutate("stock-details-5240.json", n => n["dividends"]!["events"]![0]!["exDate"] = "2026-09-28")));

        (int code, string output, string error) = await RunPaper(seconds: 60);

        Assert.True(code == 0, output + error);
        Assert.Contains("ERIC B: dividend 1.45 SEK × 7 (ex-date 2026-09-28) = 10.15 SEK credited (gross; paid 2026-10-27).", output, StringComparison.Ordinal);
        using (JsonDocument book = JsonDocument.Parse(File.ReadAllText(Path.Combine(State, "paper", "book.json"))))
        {
            Assert.Equal(10.15m, book.RootElement.GetProperty("dividends_received").GetDecimal());
            Assert.Equal("2026-09-28", book.RootElement.GetProperty("corporate_through").GetString());
        }

        EodCorporateAction dividend = Assert.Single(EodReport.Build(Audit, new DateOnly(2026, 9, 28), TimeProvider.System).CorporateActions);
        Assert.Equal(("dividend", 10.15m), (dividend.Kind, dividend.CashSek));
        Assert.Contains(_server.Requests, r => r.PathAndQuery == AvanzaRoutes.StockDetails.Path("5240"));
    }

    [Fact]
    public async Task ASharePricedAtHalfItsLastClose_IsHeldBack_UntilTheOwnerSplitsIt()
    {
        // Plan 21: Friday's close was 141.72 and today ERIC B trades at 70.86, with no split in Avanza's share count.
        PrepareHistoryAndUniverse();
        WriteBook("""{ "orderbook_id": "5240", "ticker": "ERIC B", "quantity": 7, "cost_basis": 992, "last_fill_price": 141.7, "last_mark": 141.72 }""", corporateThrough: "2026-09-25");

        (int code, string output, string error) = await RunPaper(seconds: 60);

        Assert.True(code == 0, output + error);
        Assert.True(output.Contains("ERIC B: HELD BACK, its price 70.86 against its last close 141.72 looks like a 2:1 split, and no split is known: not traded today, "
            + "valued at its last close. If it split, run 'qa paper split ERIC-B 2:1'; if the move is real, 'qa paper accept-price ERIC-B'.", StringComparison.Ordinal), output);
        Assert.Contains("Paper account: value 45,496.04 SEK (start of day 45,496.04)", output, StringComparison.Ordinal); // 7 × 141.72: no loss
        Assert.Contains("ERIC B: hold (no target today)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ERIC B: Accepted", output, StringComparison.Ordinal);

        (code, string split, error) = Qa(TimeProvider.System, "paper", "split", "ERIC-B", "2:1", "--state-dir", State, "--audit-dir", Audit);
        Assert.True(code == 0, error);
        Assert.Equal("ERIC B: split 2:1 applied; the position goes from 7 to 14.", split.Trim());
        using JsonDocument book = JsonDocument.Parse(File.ReadAllText(Path.Combine(State, "paper", "book.json")));
        JsonElement position = book.RootElement.GetProperty("positions")[0];
        Assert.Equal((14, 70.86m, 992m), (position.GetProperty("quantity").GetInt64(), position.GetProperty("last_mark").GetDecimal(), position.GetProperty("cost_basis").GetDecimal()));
        Assert.Equal(2m, book.RootElement.GetProperty("splits_by_hand")[0].GetProperty("ratio").GetDecimal());
        string status = Qa(TimeProvider.System, "paper", "status", "--state-dir", State).Output;
        Assert.Contains("ERIC B (5240): 14, cost 992.00 SEK, last fill 70.85, last close 70.86", status, StringComparison.Ordinal);
        Assert.Contains("5240: split 2:1 by hand on ", status, StringComparison.Ordinal);
    }

    [Fact]
    public void PaperSplitAndAcceptPrice_NeedAHeldShare_AndAreAudited()
    {
        Assert.Contains("No paper book yet", Qa(TimeProvider.System, "paper", "accept-price", "ERIC-B", "--state-dir", State, "--audit-dir", Audit).Error, StringComparison.Ordinal);
        WriteBook("""{ "orderbook_id": "5240", "ticker": "ERIC B", "quantity": 7, "cost_basis": 496, "last_fill_price": 70.86, "last_mark": 141.72 }""", corporateThrough: null);

        Assert.Contains("The paper book holds no VOLV B", Qa(TimeProvider.System, "paper", "split", "VOLV-B", "2:1", "--state-dir", State, "--audit-dir", Audit).Error, StringComparison.Ordinal);
        Assert.Contains("not a split ratio", Qa(TimeProvider.System, "paper", "split", "ERIC-B", "3:2", "--state-dir", State, "--audit-dir", Audit).Error, StringComparison.Ordinal);

        (int code, string output, string error) = Qa(TimeProvider.System, "paper", "accept-price", "ERIC-B", "--state-dir", State, "--audit-dir", Audit);
        Assert.True(code == 0, error);
        Assert.Equal("ERIC B: its move is taken as real; the next session values and trades it at its live price.", output.Trim());
        Assert.Contains("has no close mark to compare with", Qa(TimeProvider.System, "paper", "accept-price", "ERIC-B", "--state-dir", State, "--audit-dir", Audit).Output, StringComparison.Ordinal);
        using JsonDocument book = JsonDocument.Parse(File.ReadAllText(Path.Combine(State, "paper", "book.json")));
        Assert.False(book.RootElement.GetProperty("positions")[0].TryGetProperty("last_mark", out _));
        Assert.True(AuditLog.Verify(Audit).Valid);
    }

    /// <summary>A Paper book as Friday's session left it, holding <paramref name="position"/>.</summary>
    private void WriteBook(string position, string? corporateThrough)
    {
        string paper = Path.Combine(State, "paper");
        Directory.CreateDirectory(paper);
        string through = corporateThrough is null ? string.Empty : $""", "corporate_through": "{corporateThrough}" """;
        File.WriteAllText(Path.Combine(paper, "book.json"), $$"""
            { "format": "qa-paper-book/1", "account": "PAPER", "costs": "avanza-start", "starting_cash": 45000, "cash": 44504, "fees_paid": 1,
              "realized_pnl": 0, "start_of_day_value": 0, "saved_utc": "2026-09-25T15:30:00Z"{{through}},
              "positions": [ {{position}} ] }
            """);
    }

    [Fact]
    public async Task AListedShareThatTradesOnlyInAuctions_IsLeftOut_TheOthersStillTrade()
    {
        // Plan 22: SMALL (First North) was measured as trading in auctions after it joined the list. Before, one such
        // share stopped the decision for every share (plan 18).
        PrepareHistoryAndUniverse();
        string ticks = Data.Store.InstrumentRecord.CanonicalTickTable(new Core.Instruments.TickSizeTable([new Core.Instruments.TickSizeBand(0m, 99_999m, 0.01m)]));
        using (var store = Data.Store.HistoryStore.Open(Store))
        {
            var known = new DateTimeOffset(2026, 9, 25, 18, 0, 0, TimeSpan.Zero);
            store.UpsertInstrument(new Data.Store.InstrumentRecord(new Core.OrderbookId("9999"), null, "SMALL", "Small AB", "SEK", "FNSE", "STOCK",
                Data.Store.TradingModel.PeriodicAuction, 1m, ticks, new DateOnly(2026, 9, 1)), "test", "test", known);
            store.UpsertDailyBars(new Core.OrderbookId("9999"),
                [new Core.Market.DailyBar(new DateOnly(2026, 9, 24), 10m, 10m, 10m, 10m, 100), new Core.Market.DailyBar(new DateOnly(2026, 9, 25), 10m, 10m, 10m, 10m, 100)],
                Data.History.AvanzaChartImporter.AvanzaPriceChart, "test", known);
        }

        File.WriteAllText(Path.Combine(Config, "universe.json"), """
            { "format": "qa-universe/1", "instruments": [ { "orderbook_id": "5240", "ticker": "ERIC B", "name": "Ericsson B" }, { "orderbook_id": "9999", "ticker": "SMALL", "name": "Small AB" } ] }
            """);
        _server.Always(AvanzaRoutes.Orderbook, r => FakeAvanza.Json(r.RequestUri!.AbsolutePath.EndsWith("/9999", StringComparison.Ordinal)
            ? Fixtures.Mutate("orderbook-5240.json", n =>
            {
                n["id"] = "9999";
                n["marketPlace"] = "FNSE";
                n["tickerSymbol"] = "SMALL";
                n["name"] = "Small AB";
            })
            : Fixtures.Bytes("orderbook-5240.json")));
        _server.Always(AvanzaRoutes.MarketData, _ => FakeAvanza.Json(Fixtures.Bytes("marketdata-5240.json")));

        (int code, string output, string error) = await RunPaper(seconds: 60);

        Assert.True(code == 0, output + error);
        Assert.True(output.Contains("SMALL: not traded, it does not trade continuously (plan 22); take it off the list with 'qa universe remove SMALL'", StringComparison.Ordinal), output);
        Assert.Contains("SMALL: hold (no target today)", output, StringComparison.Ordinal);
        Assert.Contains("decision: 1 order(s)", output, StringComparison.Ordinal);
        Assert.Contains("ERIC B: Accepted", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TenNames_AreTraded_EachPolledAboutEverySevenSeconds()
    {
        // Plan 22: ERIC B and nine more Stockholm shares (the same prices under other ids).
        PrepareHistoryAndUniverse();
        string ticks = Data.Store.InstrumentRecord.CanonicalTickTable(new Core.Instruments.TickSizeTable([new Core.Instruments.TickSizeBand(0m, 99_999m, 0.01m)]));
        string[] ids = [.. Enumerable.Range(9001, 9).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        using (var store = Data.Store.HistoryStore.Open(Store))
        {
            var known = new DateTimeOffset(2026, 9, 25, 18, 0, 0, TimeSpan.Zero);
            foreach (string id in ids)
            {
                store.UpsertInstrument(new Data.Store.InstrumentRecord(new Core.OrderbookId(id), null, $"S{id}", $"Share {id}", "SEK", "XSTO", "STOCK",
                    Data.Store.TradingModel.Continuous, 1m, ticks, new DateOnly(2026, 9, 1)), "test", "test", known);
                store.UpsertDailyBars(new Core.OrderbookId(id),
                    [new Core.Market.DailyBar(new DateOnly(2026, 9, 24), 70m, 71m, 69m, 70.4m, 1000), new Core.Market.DailyBar(new DateOnly(2026, 9, 25), 70.4m, 71m, 70m, 70.9m, 1000)],
                    Data.History.AvanzaChartImporter.AvanzaPriceChart, "test", known);
            }
        }

        Trading.Risk.Universe list = Trading.Risk.Universe.Load(Path.Combine(Config, "universe.json"));
        foreach (string id in ids)
        {
            list = list.With(new Trading.Risk.UniverseEntry(new Core.OrderbookId(id), $"S{id}", $"Share {id}"));
        }

        list.Save(Path.Combine(Config, "universe.json"));
        _server.Always(AvanzaRoutes.Orderbook, r => FakeAvanza.Json(r.RequestUri!.AbsolutePath.Split('/')[^1] is var id && id != "5240"
            ? Fixtures.Mutate("orderbook-5240.json", n =>
            {
                n["id"] = id;
                n["tickerSymbol"] = $"S{id}";
                n["name"] = $"Share {id}";
            })
            : Fixtures.Bytes("orderbook-5240.json")));
        _server.Always(AvanzaRoutes.MarketData, _ => FakeAvanza.Json(Fixtures.Bytes("marketdata-5240.json")));
        int before = _server.Requests.Count;

        (int code, string output, string error) = await RunPaper(seconds: 60, setupCalls: true);

        Assert.True(code == 0, output + error);
        Assert.True(output.Contains("decision: 10 order(s).", StringComparison.Ordinal), output);
        int[] polls = [.. ids.Append("5240").Select(id => _server.Requests.Skip(before).Count(r => r.PathAndQuery == AvanzaRoutes.MarketData.Path(id)))];
        Assert.All(polls, n => Assert.InRange(n, 7, 11)); // about 60 s / 7 s, not 60 s / 5 s = 13
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
