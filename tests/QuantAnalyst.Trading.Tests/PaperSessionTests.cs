using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Analytics.Backtesting;
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

public sealed class DailyPlannerTests
{
    private static readonly OrderbookId Eric = RiskEngineTests.Eric;
    private static readonly OrderbookId Test = new("1001");
    private static readonly PreTradeRiskEngine Risk = new(RiskLimits.AdrDefaults);
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now);
    private readonly SettableQuotes _quotes = new();

    private static InstrumentSpec[] Specs => [OrderPreparationTests.Spec(), OrderPreparationTests.Spec() with { OrderbookId = Test, Ticker = "TEST B" }];

    private static AccountSnapshot Account(decimal value = 100_000m, long eric = 0, decimal ericValue = 0m) =>
        new(new AccountId(PaperConfig.AccountId), value, value, new Dictionary<OrderbookId, long> { [Eric] = eric }, new Dictionary<OrderbookId, decimal> { [Eric] = ericValue }, value);

    private PlanResult Plan(double[] targets, AccountSnapshot? account = null, ExecutionOptions? execution = null) =>
        DailyPlanner.Plan(targets, Specs, account ?? Account(), _quotes, Risk, execution ?? new ExecutionOptions(), "test", _time.GetUtcNow());

    private void Price(OrderbookId id, decimal bid, decimal ask) => _quotes.Set(id, _time.GetUtcNow(), bid, 1_000, ask, 1_000, (bid + ask) / 2, 1_000);

    [Fact]
    public void ATarget_BecomesWholeShares_AtALimitTowardTheMarket()
    {
        Price(Eric, 99.9m, 100.1m);
        PlanResult p = Plan([0.05, double.NaN]); // 5 % of 99,000 investable = 49 shares at 100
        OrderIntent i = Assert.Single(p.Intents);
        Assert.Equal((OrderSide.Buy, 49L), (i.Side, i.Quantity));
        Assert.Equal(100.5m, i.LimitPrice); // +50 bps from the 100.0 reference; OrderPreparation rounds it
        Assert.Equal(100.0m, i.DecisionPrice);
        Assert.Contains(p.Notes, n => n.Contains("TEST B: hold", StringComparison.Ordinal));
    }

    [Fact]
    public void BigTargets_AreClippedToWhatR6AndR7Allow()
    {
        Price(Eric, 99.9m, 100.1m);
        OrderIntent r6 = Assert.Single(Plan([1.0, double.NaN]).Intents);
        Assert.Equal(99, r6.Quantity); // 10,000 SEK per order at a 100.5 limit
        Assert.Contains("clipped from 990 by R6", r6.Reason, StringComparison.Ordinal);

        OrderIntent r7 = Assert.Single(Plan([1.0, double.NaN], Account(eric: 150, ericValue: 15_000m)).Intents);
        Assert.Equal(49, r7.Quantity); // 20,000 - 15,000 = 5,000 SEK of room
        Assert.Contains("R7", r7.Reason, StringComparison.Ordinal);

        PlanResult full = Plan([1.0, double.NaN], Account(eric: 200, ericValue: 20_000m));
        Assert.Empty(full.Intents);
        Assert.Contains(full.Notes, n => n.Contains("leaves no room", StringComparison.Ordinal));
    }

    [Fact]
    public void AZeroTarget_SellsThePosition_AndSellsNeverExceedIt()
    {
        Price(Eric, 99.9m, 100.1m);
        OrderIntent sell = Assert.Single(Plan([0.0, double.NaN], Account(eric: 50, ericValue: 5_000m)).Intents);
        Assert.Equal((OrderSide.Sell, 50L, 99.5m), (sell.Side, sell.Quantity, sell.LimitPrice));
    }

    [Fact]
    public void SmallRebalances_StayInsideTheBand_AndNoPriceMeansNoOrder()
    {
        Price(Eric, 99.9m, 100.1m);
        PlanResult band = Plan([0.05, double.NaN], Account(eric: 47, ericValue: 4_700m)); // wants 49, has 47
        Assert.Empty(band.Intents);
        Assert.Contains(band.Notes, n => n.Contains("no-trade band", StringComparison.Ordinal));

        PlanResult noPrice = Plan([double.NaN, 0.05]);
        Assert.Empty(noPrice.Intents);
        Assert.Contains(noPrice.Notes, n => n.Contains("TEST B: skipped, no fresh live price", StringComparison.Ordinal));
    }

    [Fact]
    public void TargetsMustMatchTheInstruments() =>
        Assert.Throws<ArgumentException>(() => Plan([0.1]));
}

public sealed class StrategyReplayTests
{
    [Fact]
    public void TheDecisionIsTheOneAtTheLastBar_AfterReplayingEveryBar()
    {
        MarketPanel panel = SyntheticMarket.Generate(new SyntheticMarketOptions { Instruments = 2, Periods = 30, Seed = 7 });
        Assert.Equal([0.5, 0.5], StrategyReplay.DecideAtLastBar(panel.Truncate(3), new BuyAndHold(5)));
        Assert.All(StrategyReplay.DecideAtLastBar(panel, new BuyAndHold(5)), w => Assert.True(double.IsNaN(w)));

        // A stateful strategy must see every bar in order; replaying makes that true.
        double[] ma = StrategyReplay.DecideAtLastBar(panel, new MovingAverageCross(3, 10));
        Assert.All(ma, w => Assert.True(w is 0.0 or 0.5));
    }

    [Fact]
    public void BadWeights_AreRefused()
    {
        MarketPanel panel = SyntheticMarket.Generate(new SyntheticMarketOptions { Instruments = 2, Periods = 5, Seed = 7 });
        Assert.Throws<StrategyException>(() => StrategyReplay.DecideAtLastBar(panel, new Fixed(0.7, 0.7)));
        Assert.Throws<StrategyException>(() => StrategyReplay.DecideAtLastBar(panel, new Fixed(-0.1, 0.1)));
    }

    private sealed class Fixed(double a, double b) : IStrategy
    {
        public void Decide(BarWindow window, Span<double> targets)
        {
            targets[0] = a;
            targets[1] = b;
        }
    }
}

public sealed class PaperSessionTests : IDisposable
{
    private static readonly OrderbookId Eric = RiskEngineTests.Eric;
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 7, 9, 58, TimeSpan.Zero)); // 09:09:58 Stockholm
    private readonly SettableQuotes _quotes = new();
    private readonly StringWriter _output = new();
    private readonly AuditLog _audit;
    private readonly HaltController _halts;
    private readonly OrderManager _oms;
    private readonly PaperBook _book;
    private readonly PaperOrderChannel _channel;
    private readonly OrderGateway _gateway;
    private readonly KillSwitch _kill;
    private readonly PaperSession _session;
    private int _decisions;

    public PaperSessionTests()
    {
        _audit = new AuditLog(_dir.File("audit"), _time);
        _halts = new HaltController(_audit, _time);
        _oms = new OrderManager(_audit, _halts, _time);
        _book = PaperBook.InMemory(100_000m, "test-mini", _quotes, _time);
        var instruments = new InstrumentCatalog([OrderPreparationTests.Spec()]);
        _channel = new PaperOrderChannel(_book, PaperOrderChannelTests.Mini, _quotes, instruments, _time);
        MarketCalendar calendar = OrderGatewayTests.Calendar();
        _gateway = new OrderGateway(_channel, new GatewayEnvironment
        {
            Mode = TradingMode.Paper,
            Instruments = instruments,
            Quotes = _quotes,
            Account = _book,
            Calendar = calendar,
            Universe = new Universe([new UniverseEntry(Eric, "ERIC B", "Ericsson B")]),
            AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { PaperConfig.AccountId },
            Fees = (p, s) => _channel.EstimateFees(p.Value, s.Currency),
            CourtageVerified = false,
        }, new PreTradeRiskEngine(RiskLimits.AdrDefaults), _oms, _halts, _audit, _time);
        _kill = new KillSwitch(_gateway, _halts, _audit, _time, _dir.File("KILL"), _dir.File("state"), _book, 0.02m, watch: false);
        var schedule = new TradingSchedule(calendar, RiskLimits.AdrDefaults, new TimeOnly(9, 10));
        var reconciler = new Reconciler(_oms, _halts, _audit, _time, _book.Account);
        _session = new PaperSession(_gateway, _channel, _book, _kill, reconciler, _halts, schedule, _audit, _time, Decide, _output);
    }

    public void Dispose()
    {
        _kill.Dispose();
        _gateway.Dispose();
        _dir.Dispose();
    }

    private Task<PlanResult> Decide(CancellationToken ct)
    {
        _decisions++;
        DateTimeOffset now = _time.GetUtcNow();
        OrderIntent Buy(long qty, decimal limit) => new(Eric, "ERIC B", OrderSide.Buy, qty, limit, "test", limit, now, "test");
        return Task.FromResult(new PlanResult([Buy(10, 100.2m), Buy(5, 100.1m)], ["two resting buys"]));
    }

    private async Task StepFor(TimeSpan span)
    {
        DateTimeOffset until = _time.GetUtcNow() + span;
        while (_time.GetUtcNow() < until)
        {
            _quotes.Set(Eric, _time.GetUtcNow(), 100.4m, 500, 100.6m, 700, 100.5m, 10_000);
            await _session.StepAsync(CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task ItDecidesOnceAtTheDecisionTime_PacesTheOrders_AndEndsThemAtTheClose()
    {
        await StepFor(TimeSpan.FromSeconds(2));
        Assert.Equal(0, _decisions); // 09:09:59
        await StepFor(TimeSpan.FromSeconds(2));
        Assert.Equal(1, _decisions);
        Assert.Single(_oms.All); // the second waits for the pace

        await StepFor(PaperSession.PaceBetweenOrders);
        Assert.Equal(2, _oms.All.Count);
        await StepFor(TimeSpan.FromMinutes(5));
        Assert.Equal(1, _decisions); // once a day

        _time.SetUtcNow(new DateTimeOffset(2026, 9, 28, 15, 30, 0, TimeSpan.Zero)); // the close
        await StepFor(TimeSpan.FromSeconds(1));
        Assert.All(_oms.All, o => Assert.Equal(OmsState.Cancelled, o.State));
        Assert.Contains("close: 2 order(s) expired", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHaltedSession_SkipsTheDecision()
    {
        _halts.Raise(HaltReason.StaleData, "stream down");
        await StepFor(TimeSpan.FromSeconds(5));
        Assert.Equal(0, _decisions);
        Assert.Contains("decision skipped: trading is halted (StaleData)", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_CancelsEverythingThroughTheGateway_AndReconciles()
    {
        Task<PaperSessionSummary> run = _session.RunAsync(_time.GetUtcNow().AddMinutes(1), CancellationToken.None);
        for (int i = 0; i < 70 && !run.IsCompleted; i++)
        {
            _quotes.Set(Eric, _time.GetUtcNow(), 100.4m, 500, 100.6m, 700, 100.5m, 10_000);
            await Task.Delay(5, TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        PaperSessionSummary summary = await run;
        Assert.Equal(2, summary.Submitted);
        Assert.True(summary.ReconciliationClean);
        Assert.All(_oms.All, o => Assert.Equal(OmsState.Cancelled, o.State));
        Assert.Equal(0, _channel.RestingCount);
        Assert.True(AuditLog.Verify(_dir.File("audit")).Valid);
    }
}
