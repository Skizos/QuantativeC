using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Reconciliation;
using QuantAnalyst.Trading.Risk;
using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Trading.Tests;

/// <summary>
/// ADR 0005 / plan 16 step 6: one Paper session across Stockholm and New York. Each market decides and ends its day
/// orders on its own clock; the day is booked and reported once, at the last close.
/// </summary>
public sealed class MultiMarketSessionTests : IDisposable
{
    private static readonly OrderbookId Eric = RiskEngineTests.Eric;
    private static readonly OrderbookId Aapl = ForeignTradingTests.Aapl;

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 7, 9, 58, TimeSpan.Zero)); // 09:09:58 Stockholm
    private readonly SettableQuotes _quotes = new();
    private readonly StringWriter _output = new();
    private readonly AuditLog _audit;
    private readonly HaltController _halts;
    private readonly OrderManager _oms;
    private readonly PaperOrderChannel _channel;
    private readonly OrderGateway _gateway;
    private readonly KillSwitch _kill;
    private readonly PaperSession _session;
    private readonly List<string> _decided = [];
    private readonly List<DateOnly> _reports = [];

    public MultiMarketSessionTests()
    {
        _audit = new AuditLog(_dir.File("audit"), _time);
        _halts = new HaltController(_audit, _time);
        _oms = new OrderManager(_audit, _halts, _time);
        var fx = new FxTable(new Dictionary<string, decimal> { ["USD"] = 10m });
        PaperBook book = PaperBook.InMemory(100_000m, "test-mini", _quotes, _time, fx);
        var instruments = new InstrumentCatalog([OrderPreparationTests.Spec(), ForeignTradingTests.AppleSpec]);
        var costs = PaperOrderChannelTests.Mini with
        {
            Foreign = new Dictionary<string, Analytics.Backtesting.ForeignCourtage>(StringComparer.Ordinal) { ["USD"] = new(1m, 0.0025m) },
        };
        _channel = new PaperOrderChannel(book, costs, _quotes, instruments, _time, fx);
        MarketCalendar stockholm = OrderGatewayTests.Calendar();
        var xsto = new TradingSchedule(stockholm, RiskLimits.AdrDefaults, new TimeOnly(9, 10));
        var xnys = new TradingSchedule(ForeignTradingTests.NewYork, RiskLimits.AdrDefaults, new TimeOnly(9, 10), stockholm);
        _gateway = new OrderGateway(_channel, new GatewayEnvironment
        {
            Mode = TradingMode.Paper,
            Instruments = instruments,
            Quotes = _quotes,
            Account = book,
            Calendar = stockholm,
            Universe = new Universe([new UniverseEntry(Eric, "ERIC B", "Ericsson B"), new UniverseEntry(Aapl, "AAPL", "Apple Inc")]),
            AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { PaperConfig.AccountId },
            Fees = (p, s) => _channel.EstimateFees(p.Value, s.Currency),
            CourtageVerified = false,
            Fx = fx,
            Schedules = new Dictionary<string, TradingSchedule>(StringComparer.Ordinal) { ["SEK"] = xsto, ["USD"] = xnys },
        }, new PreTradeRiskEngine(RiskLimits.AdrDefaults), _oms, _halts, _audit, _time);
        _kill = new KillSwitch(_gateway, _halts, _audit, _time, _dir.File("KILL"), _dir.File("state"), book, RiskLimits.AdrDefaults, watch: false);
        _session = new PaperSession(_gateway, _channel, book, _kill, new Reconciler(_oms, _halts, _audit, _time, book.Account), _halts,
            [
                new SessionMarket(xsto, ct => Decide("XSTO", Eric, "ERIC B", 100.2m, 1), s => s.Currency == "SEK"),
                new SessionMarket(xnys, ct => Decide("XNYS", Aapl, "AAPL", 250.00m, 1), s => s.Currency == "USD"),
            ],
            _audit, _time, _output, d =>
            {
                _reports.Add(d);
                return "reported";
            });
    }

    /// <summary>What each market's decision returns: resting buys below the ask.</summary>
    private int Orders { get; set; } = 1;

    public void Dispose()
    {
        _kill.Dispose();
        _gateway.Dispose();
        _dir.Dispose();
    }

    private Task<PlanResult> Decide(string mic, OrderbookId id, string ticker, decimal limit, long qty)
    {
        _decided.Add(mic);
        DateTimeOffset now = _time.GetUtcNow();
        OrderIntent[] buys = [.. Enumerable.Range(0, Orders).Select(i => new OrderIntent(id, ticker, OrderSide.Buy, qty, limit - (0.01m * i), "test", limit, now, "test"))];
        return Task.FromResult(new PlanResult(buys, [$"{ticker}: {buys.Length} resting buy(s)"]));
    }

    private async Task StepFor(TimeSpan span)
    {
        DateTimeOffset until = _time.GetUtcNow() + span;
        while (_time.GetUtcNow() < until)
        {
            DateTimeOffset now = _time.GetUtcNow();
            _quotes.Set(Eric, now, 100.4m, 500, 100.6m, 700, 100.5m, 10_000);
            _quotes.Set(Aapl, now, 250.40m, 500, 250.60m, 700, 250.50m, 10_000);
            await _session.StepAsync(CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    private async Task At(int hour, int minute, int second, TimeSpan span, int day = 28, int month = 9)
    {
        _time.SetUtcNow(new DateTimeOffset(2026, month, day, hour, minute, second, TimeSpan.Zero));
        await StepFor(span);
    }

    private string[] AuditKinds() =>
        [.. Directory.GetFiles(_dir.File("audit")).Order().SelectMany(AuditLog.Read).Select(r => r.GetProperty("kind").GetString()!)];

    private OmsOrder Order(OrderbookId id) => _oms.All.Single(o => o.OrderbookId == id);

    [Fact]
    public async Task EachMarket_DecidesAndClosesOnItsOwnClock_AndTheDayIsReportedOnceAtTheLastClose()
    {
        await StepFor(TimeSpan.FromSeconds(3)); // 09:10 Stockholm
        Assert.Equal(["XSTO"], _decided);
        Assert.Equal(OmsState.Working, Order(Eric).State);

        await At(13, 39, 58, TimeSpan.FromSeconds(3)); // 09:40 New York = 15:40 Stockholm
        Assert.Equal(["XSTO", "XNYS"], _decided);
        Assert.Equal(OmsState.Working, Order(Aapl).State);
        string output = _output.ToString();
        Assert.Contains("09:10:00 XSTO decision: 1 order(s).", output, StringComparison.Ordinal);
        Assert.Contains("15:40:00 XNYS decision: 1 order(s).", output, StringComparison.Ordinal);
        Assert.Contains("  AAPL: 1 resting buy(s)", output, StringComparison.Ordinal);

        await At(15, 30, 0, TimeSpan.FromSeconds(2)); // 17:30 Stockholm: its close
        Assert.Equal((OmsState.Cancelled, OmsState.Working), (Order(Eric).State, Order(Aapl).State));
        Assert.Contains("17:30:00 XSTO close: 1 order(s) expired.", _output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("end-of-day", AuditKinds());
        Assert.Empty(_reports);

        await At(20, 0, 0, TimeSpan.FromSeconds(2)); // 16:00 New York = 22:00 Stockholm: the last close
        Assert.Equal(OmsState.Cancelled, Order(Aapl).State);
        Assert.Contains("22:00:00 XNYS close: 1 order(s) expired. Value 100,000.00 SEK", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal([new DateOnly(2026, 9, 28)], _reports);
        Assert.Equal(1, AuditKinds().Count(k => k == "market-close"));
        Assert.Equal(1, AuditKinds().Count(k => k == "end-of-day"));
        Assert.Equal(["XSTO", "XNYS"], _decided); // once a day each

        JsonElement[] decisions = [.. Directory.GetFiles(_dir.File("audit")).SelectMany(AuditLog.Read).Where(r => r.GetProperty("kind").GetString() == "decision")];
        Assert.Equal(["XSTO", "XNYS"], decisions.Select(d => d.GetProperty("data").GetProperty("market").GetString()));
        Assert.True(AuditLog.Verify(_dir.File("audit")).Valid);
    }

    [Fact]
    public async Task OnAUsHoliday_StockholmsCloseEndsTheDay()
    {
        await At(8, 9, 58, TimeSpan.FromSeconds(3), day: 26, month: 11); // Thanksgiving; 09:10 Stockholm (CET)
        Assert.Equal(["XSTO"], _decided);

        await At(14, 39, 58, TimeSpan.FromSeconds(3), day: 26, month: 11); // 09:40 New York: no session
        await At(16, 30, 0, TimeSpan.FromSeconds(2), day: 26, month: 11); // 17:30 Stockholm
        Assert.Equal(["XSTO"], _decided);
        Assert.Contains("17:30:00 XSTO close: 1 order(s) expired. Value", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal([new DateOnly(2026, 11, 26)], _reports);
        Assert.DoesNotContain("market-close", AuditKinds());
    }

    [Fact]
    public async Task StartedLate_BothMarketsDecide_AndStockholmsLeftoverOrdersDontHoldUpNewYorks()
    {
        Orders = 3;
        await At(15, 19, 55, TimeSpan.FromSeconds(2)); // 17:19:55 Stockholm: its window closes at 17:20
        Assert.Equal(["XSTO", "XNYS"], _decided);
        Assert.Equal(1, _oms.All.Count(o => o.OrderbookId == Eric)); // the first; the pace holds the rest

        await StepFor(PaperSession.PaceBetweenOrders); // 17:20:10: Stockholm's window is closed, New York's open
        Assert.Equal(1, _oms.All.Count(o => o.OrderbookId == Eric));
        Assert.Equal(1, _oms.All.Count(o => o.OrderbookId == Aapl));

        await At(15, 30, 0, TimeSpan.FromSeconds(1)); // Stockholm's close drops its two leftovers
        await At(15, 40, 0, TimeSpan.FromSeconds(30));
        Assert.Equal(1, _oms.All.Count(o => o.OrderbookId == Eric));
        Assert.Equal(3, _oms.All.Count(o => o.OrderbookId == Aapl));
    }

    [Fact]
    public async Task StoppedBeforeTheLastClose_WritesAPartialReport()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 9, 28, 15, 29, 55, TimeSpan.Zero));
        Task<PaperSessionSummary> run = _session.RunAsync(new DateTimeOffset(2026, 9, 28, 15, 30, 10, TimeSpan.Zero), CancellationToken.None);
        for (int i = 0; i < 40 && !run.IsCompleted; i++)
        {
            _quotes.Set(Eric, _time.GetUtcNow(), 100.4m, 500, 100.6m, 700, 100.5m, 10_000);
            _quotes.Set(Aapl, _time.GetUtcNow(), 250.40m, 500, 250.60m, 700, 250.50m, 10_000);
            await Task.Delay(5, TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await run;
        Assert.Contains("XSTO close: 0 order(s) expired.", _output.ToString(), StringComparison.Ordinal); // decided too late to trade
        Assert.Equal([new DateOnly(2026, 9, 28)], _reports);
        Assert.DoesNotContain("end-of-day", AuditKinds());
    }
}
