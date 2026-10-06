using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Risk;
using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Trading.Tests;

/// <summary>
/// ADR 0005 / plan 16 step 5: US and Canadian shares on paper. Money (limits, cash, fees, positions) is SEK at the
/// day's rate; prices stay in the share's currency; R16 judges an order on its own market's clock and calendar.
/// </summary>
public sealed class ForeignTradingTests : IDisposable
{
    internal static readonly OrderbookId Aapl = new("4478");
    private static readonly OrderbookId Eric = RiskEngineTests.Eric;

    // 09:45 New York (EDT) = 15:45 Stockholm (CEST) on Monday 2026-09-28: both markets are in their windows.
    private static readonly DateTimeOffset BothOpen = new(2026, 9, 28, 13, 45, 0, TimeSpan.Zero);

    private static readonly FxTable Fx = new(new Dictionary<string, decimal> { ["USD"] = 10m });

    // Mini-like: 0.25 % min 1 at home and abroad, FX fee 0.25 %.
    private static readonly CostModel Costs = PaperOrderChannelTests.Mini with
    {
        Foreign = new Dictionary<string, ForeignCourtage>(StringComparer.Ordinal) { ["USD"] = new(1m, 0.0025m) },
    };

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now); // 10:00 Stockholm, 04:00 New York
    private readonly SettableQuotes _quotes = new();
    private readonly AuditLog _audit;
    private readonly HaltController _halts;
    private readonly OrderManager _oms;
    private readonly InstrumentCatalog _instruments;
    private readonly PaperBook _book;
    private readonly PaperOrderChannel _channel;
    private OrderGateway? _gateway;

    public ForeignTradingTests()
    {
        _audit = new AuditLog(Path.Combine(_dir.Path, "audit"), _time);
        _halts = new HaltController(_audit, _time);
        _oms = new OrderManager(_audit, _halts, _time);
        _instruments = new InstrumentCatalog([AppleSpec, OrderPreparationTests.Spec()]);
        _book = PaperBook.OpenOrCreate(Path.Combine(_dir.Path, "paper"), new PaperConfig(Costs.Name, 100_000m, new TimeOnly(9, 10)), _quotes, _time, out _, Fx);
        _channel = new PaperOrderChannel(_book, Costs, _quotes, _instruments, _time, Fx);
    }

    /// <summary>Apple in USD, cent ticks.</summary>
    internal static InstrumentSpec AppleSpec { get; } =
        new(Aapl, "AAPL", "Apple Inc", "USD", 1, new TickSizeTable([new TickSizeBand(0m, 99_999m, 0.01m)]), TickTableVerified: false);

    /// <summary>A draft New York year: 09:30–16:00, Thanksgiving closed, the day after closing at 13:00.</summary>
    internal static MarketCalendar NewYork { get; } = new([
        new CalendarYear("XNYS", 2026, new TimeOnly(9, 30), new TimeOnly(16, 0), new TimeOnly(9, 30), new TimeOnly(13, 0),
            [new CalendarEntry(new DateOnly(2026, 11, 26), "Thanksgiving Day")],
            [new CalendarEntry(new DateOnly(2026, 11, 27), "Day after Thanksgiving", new TimeOnly(13, 0))],
            "test", null) { TimeZoneId = Markets.UnitedStates.TimeZoneId },
    ]);

    private static TradingSchedule UsSchedule => new(NewYork, RiskLimits.AdrDefaults, new TimeOnly(9, 10), OrderGatewayTests.Calendar());

    private OrderGateway Gateway => _gateway ??= NewGateway();

    public void Dispose()
    {
        _gateway?.Dispose();
        _dir.Dispose();
    }

    private OrderGateway NewGateway(Func<GatewayEnvironment, GatewayEnvironment>? change = null)
    {
        var env = new GatewayEnvironment
        {
            Mode = TradingMode.Paper,
            Instruments = _instruments,
            Quotes = _quotes,
            Account = _book,
            Calendar = OrderGatewayTests.Calendar(),
            Universe = new Universe([new UniverseEntry(Eric, "ERIC B", "Ericsson B"), new UniverseEntry(Aapl, "AAPL", "Apple Inc")]),
            AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { PaperConfig.AccountId },
            Fees = (p, spec) => _channel.EstimateFees(p.Value, spec.Currency),
            CourtageVerified = false,
            Fx = Fx,
            Schedules = new Dictionary<string, TradingSchedule>(StringComparer.Ordinal)
            {
                ["SEK"] = new(OrderGatewayTests.Calendar(), RiskLimits.AdrDefaults, new TimeOnly(9, 10)),
                ["USD"] = UsSchedule,
            },
        };
        return new OrderGateway(_channel, change is null ? env : change(env), new PreTradeRiskEngine(RiskLimits.AdrDefaults), _oms, _halts, _audit, _time);
    }

    private void At(DateTimeOffset utc)
    {
        _time.SetUtcNow(utc);
        AppleMarket(250.50m, 10_000);
        _quotes.Set(Eric, utc, 100.4m, 500, 100.6m, 700, 100.5m, 10_000);
    }

    private void AppleMarket(decimal last, decimal total, decimal bid = 250.40m, decimal ask = 250.60m) =>
        _quotes.Set(Aapl, _time.GetUtcNow(), bid, 500, ask, 700, last, total);

    private Task<SubmitResult> Submit(OrderbookId id, OrderSide side, long qty, decimal limit, OrderGateway? gateway = null) =>
        (gateway ?? Gateway).SubmitAsync(
            new OrderIntent(id, id == Aapl ? "AAPL" : "ERIC B", side, qty, limit, "test", limit, _time.GetUtcNow(), "test"), TestContext.Current.CancellationToken);

    [Fact]
    public async Task AUsBuy_FillsInDollars_AndIsBookedInKronor()
    {
        At(BothOpen);
        SubmitResult r = await Submit(Aapl, OrderSide.Buy, 3, 250.60m); // marketable: the ask

        Assert.True(r.Status == SubmitStatus.Accepted, r.Message);
        Assert.Equal(OmsState.Filled, r.Order!.State);
        Assert.Equal(250.60m, r.Order.AverageFillPrice); // USD

        // 751.80 USD at 10 = 7,518 SEK; courtage 0.25 % = 1.8795 USD = 18.80 SEK; FX fee 0.25 % = 18.80 SEK.
        Assert.Equal(37.60m, r.Order.Fees);
        Assert.Equal(100_000m - 7_518m - 37.60m, _book.Cash);
        PaperPosition held = Assert.Single(_book.Positions);
        Assert.Equal((3L, 7_555.60m, 250.60m, "USD"), (held.Quantity, held.CostBasis, held.LastFillPrice, held.Currency));

        AccountSnapshot account = await _book.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3 * 250.50m * 10m, account.PositionValues[Aapl]); // marked at the last trade, in SEK
        Assert.Equal(_book.Cash + 7_515m, account.AccountValue);

        RiskCheckResult r6 = r.Risk!.Checks.Single(c => c.Id == "R6");
        Assert.Equal("7,518.00 SEK (751.80 at 10.0000 SEK per unit)", r6.Observed);
        RiskCheckResult r16 = r.Risk.Checks.Single(c => c.Id == "R16");
        Assert.Equal(("09:45:00 New York (Full day, XNYS)", "09:35–15:50 New York"), (r16.Observed, r16.Limit));
        Assert.True(AuditLog.Verify(Path.Combine(_dir.Path, "audit")).Valid);
    }

    [Fact]
    public async Task ASell_ReturnsKronor_AndTheBookKeepsTheCurrency_AcrossARestart()
    {
        At(BothOpen);
        await Submit(Aapl, OrderSide.Buy, 3, 250.60m);
        At(BothOpen.AddMinutes(10));
        AppleMarket(260.50m, 11_000, bid: 260.40m, ask: 260.60m);

        PaperBook reopened = PaperBook.OpenOrCreate(Path.Combine(_dir.Path, "paper"), new PaperConfig(Costs.Name, 100_000m, new TimeOnly(9, 10)), _quotes, _time, out _, Fx);
        Assert.Equal("USD", Assert.Single(reopened.Positions).Currency);

        SubmitResult sold = await Submit(Aapl, OrderSide.Sell, 3, 260.40m); // the bid
        Assert.True(sold.Status == SubmitStatus.Accepted, sold.Message);

        // 781.20 USD = 7,812 SEK, less 19.53 + 19.53 SEK in fees.
        Assert.Equal(100_000m - 7_518m - 37.60m + 7_812m - 39.06m, _book.Cash);
        Assert.Empty(_book.Positions);
        string fills = File.ReadAllText(Path.Combine(_dir.Path, "paper", "fills.jsonl"));
        Assert.Contains("\"currency\":\"USD\",\"sek_per_unit\":10", fills, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASwedishBook_IsWrittenAsBefore_WithoutACurrency()
    {
        At(BothOpen);
        SubmitResult r = await Submit(Eric, OrderSide.Buy, 10, 100.6m);
        Assert.True(r.Status == SubmitStatus.Accepted, r.Message);
        Assert.DoesNotContain("currency", File.ReadAllText(Path.Combine(_dir.Path, "paper", PaperBook.FileName)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("currency", File.ReadAllText(Path.Combine(_dir.Path, "paper", "fills.jsonl")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1,006.00 SEK", r.Risk!.Checks.Single(c => c.Id == "R6").Observed);
    }

    [Fact]
    public async Task R6_JudgesTheValueInKronor()
    {
        At(BothOpen);
        SubmitResult r = await Submit(Aapl, OrderSide.Buy, 4, 250.60m); // 1,002.40 USD = 10,024 SEK > 10 % of 100,000

        Assert.Equal(SubmitStatus.RiskRejected, r.Status);
        Assert.Equal(["R6"], r.Risk!.Failures.Select(f => f.Id));
        Assert.Equal(0m, _book.Reserved);
    }

    [Fact]
    public async Task R16_JudgesAUsOrderOnNewYorksClock()
    {
        At(RiskEngineTests.Now); // 10:00 Stockholm, 04:00 New York
        SubmitResult us = await Submit(Aapl, OrderSide.Buy, 3, 250.60m);
        SubmitResult swedish = await Submit(Eric, OrderSide.Buy, 10, 100.6m);

        Assert.Equal(["R16"], us.Risk!.Failures.Select(f => f.Id));
        RiskCheckResult r16 = us.Risk.Failures.First();
        Assert.Equal(("04:00:00 New York (Full day, XNYS)", "09:35–15:50 New York"), (r16.Observed, r16.Limit));
        Assert.True(swedish.Status == SubmitStatus.Accepted, swedish.Message);
        Assert.StartsWith("10:00:00", swedish.Risk!.Checks.Single(c => c.Id == "R16").Observed, StringComparison.Ordinal); // Stockholm, as before
    }

    [Fact]
    public async Task R16_FailsAUsOrderOnAUsHoliday_AndWithoutAUsSchedule()
    {
        At(new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero)); // Thanksgiving, 10:00 New York
        SubmitResult holiday = await Submit(Aapl, OrderSide.Buy, 3, 250.60m);
        Assert.Equal(["R16"], holiday.Risk!.Failures.Select(f => f.Id));
        Assert.Equal("the XNYS market is closed today", holiday.Risk.Failures.First().Message);
        Assert.Equal("XNYS: Closed (Thanksgiving Day)", holiday.Risk.Failures.First().Observed);

        using OrderGateway noSchedule = NewGateway(e => e with { Schedules = null });
        At(new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.Zero));
        SubmitResult r = await Submit(Aapl, OrderSide.Buy, 3, 250.60m, noSchedule);
        Assert.Equal(["R16"], r.Risk!.Failures.Select(f => f.Id));
        Assert.Equal("no XNYS calendar for this date", r.Risk.Failures.First().Observed);
    }

    [Fact]
    public async Task AUsShare_WithoutARate_IsNotPrepared()
    {
        At(BothOpen);
        using OrderGateway noRate = NewGateway(e => e with { Fx = FxTable.SekOnly });
        SubmitResult r = await Submit(Aapl, OrderSide.Buy, 3, 250.60m, noRate);
        Assert.Equal(SubmitStatus.NotPrepared, r.Status);
        Assert.Equal("AAPL: no USD/SEK rate is known, so the order's value in SEK can't be judged.", r.Message);
    }

    [Fact]
    public async Task AShareInAnotherCurrency_IsNotPrepared()
    {
        At(BothOpen);
        var euro = new InstrumentCatalog([AppleSpec with { Currency = "EUR" }]);
        using OrderGateway gateway = NewGateway(e => e with { Instruments = euro });
        SubmitResult r = await Submit(Aapl, OrderSide.Buy, 3, 250.60m, gateway);
        Assert.Equal(SubmitStatus.NotPrepared, r.Status);
        Assert.Equal("AAPL trades in EUR; the program trades shares in SEK, USD, CAD (ADR 0005).", r.Message);
    }

    [Fact]
    public async Task ARestingUsBuy_ReservesKronor_AndEndsAtItsOwnMarketsClose()
    {
        At(BothOpen);
        SubmitResult us = await Submit(Aapl, OrderSide.Buy, 3, 250.00m); // under the ask: rests
        SubmitResult swedish = await Submit(Eric, OrderSide.Buy, 10, 100.3m);
        Assert.Equal((OmsState.Working, OmsState.Working), (us.Order!.State, swedish.Order!.State));

        // 750 USD = 7,500 SEK + courtage 1.875 USD (18.75 SEK) + FX fee 18.75 SEK; ERIC 1,003 + 2.51 courtage.
        Assert.Equal(7_537.50m + 1_005.51m, _book.Reserved);

        Assert.Equal(1, _channel.EndOfDay("XNYS day orders expired at the close", s => s.Currency == "USD"));
        Assert.Equal((OmsState.Cancelled, OmsState.Working), (us.Order.State, swedish.Order.State));
        Assert.Equal(1_005.51m, _book.Reserved);

        Assert.Equal(1, _channel.EndOfDay("XSTO day orders expired at the close"));
        Assert.Equal(0m, _book.Reserved);
    }

    [Fact]
    public void ThePlanner_SizesAUsShareInKronor_AndKeepsItsLimitInDollars()
    {
        _time.SetUtcNow(BothOpen);
        AppleMarket(250.50m, 10_000);
        var account = new AccountSnapshot(new AccountId(PaperConfig.AccountId), 100_000m, 100_000m, new Dictionary<OrderbookId, long>(), new Dictionary<OrderbookId, decimal>(), 100_000m);
        var risk = new PreTradeRiskEngine(RiskLimits.AdrDefaults);

        PlanResult plan = DailyPlanner.Plan([0.2], [AppleSpec], account, [], _quotes, risk, new ExecutionOptions(), "test", BothOpen, Fx);

        // Target 20 % of 99,000 SEK at 2,505 SEK a share = 7; one order may be 10,000 SEK = 3 shares at the 251.75 USD limit.
        OrderIntent buy = Assert.Single(plan.Intents);
        Assert.Equal((OrderSide.Buy, 3L, 250.50m * 1.005m), (buy.Side, buy.Quantity, buy.LimitPrice));

        PlanResult noRate = DailyPlanner.Plan([0.2], [AppleSpec], account, [], _quotes, risk, new ExecutionOptions(), "test", BothOpen);
        Assert.Empty(noRate.Intents);
        Assert.Contains("AAPL: skipped, no USD/SEK rate", noRate.Notes);
    }

    [Fact]
    public void ThePlanner_CountsAWorkingUsBuyInKronor()
    {
        _time.SetUtcNow(BothOpen);
        AppleMarket(250.50m, 10_000);
        var account = new AccountSnapshot(new AccountId(PaperConfig.AccountId), 100_000m, 100_000m, new Dictionary<OrderbookId, long>(), new Dictionary<OrderbookId, decimal>(), 100_000m);
        OpenOrderView working = new(Guid.CreateVersion7(), Aapl, OrderSide.Buy, 7, 250m, false, FxToSek: 10m); // 17,500 SEK of the 20,000 R7 room

        PlanResult plan = DailyPlanner.Plan([0.2], [AppleSpec], account, [working], _quotes, new PreTradeRiskEngine(RiskLimits.AdrDefaults), new ExecutionOptions(), "test", BothOpen, Fx);

        // At face value (7 × 250 = 1,750) the working buy would leave room for 3 more shares; in SEK it leaves none.
        Assert.Equal(17_500m, working.RemainingValueSek);
        Assert.Empty(plan.Intents);
        Assert.Contains("AAPL: Buy 7 wanted, but R7 position leaves no room", plan.Notes);
    }

    [Fact]
    public void TheUsSchedule_KeepsStockholmsDistancesFromTheSession_InNewYorkTime()
    {
        SessionPlan day = UsSchedule.Plan(new DateOnly(2026, 9, 28))!;
        Assert.Equal(
            [At(13, 30), At(13, 35), At(13, 40), At(19, 50), At(20, 0)],
            [day.OpenUtc, day.WindowOpenUtc, day.DecisionUtc, day.WindowCloseUtc, day.CloseUtc]);

        // The day after Thanksgiving closes at 13:00 (EST): the window closes at 12:50 New York = 17:50 UTC.
        SessionPlan half = UsSchedule.Plan(new DateOnly(2026, 11, 27))!;
        Assert.Equal((new DateTimeOffset(2026, 11, 27, 17, 50, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero)), (half.WindowCloseUtc, half.CloseUtc));
        Assert.Null(UsSchedule.Plan(new DateOnly(2026, 11, 26)));

        // 9 March: New York is on summer time, Stockholm not yet: the decision is 09:40 New York = 14:40 Stockholm.
        SessionPlan march = UsSchedule.Plan(new DateOnly(2026, 3, 9))!;
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 13, 40, 0, TimeSpan.Zero), march.DecisionUtc);
        Assert.Equal(new TimeOnly(14, 40), TimeOnly.FromDateTime(MarketTime.ToStockholm(march.DecisionUtc).DateTime));

        static DateTimeOffset At(int h, int m) => new(2026, 9, 28, h, m, 0, TimeSpan.Zero);
    }

    [Fact]
    public void TheUsSchedule_PhasesAndNextDecision_FollowNewYorksDate()
    {
        TradingSchedule us = UsSchedule;
        Assert.Equal("XNYS", us.Mic);
        Assert.Equal(SessionPhase.BeforeWindow, us.PhaseAt(RiskEngineTests.Now)); // 04:00 New York
        Assert.Equal(SessionPhase.Window, us.PhaseAt(BothOpen));
        Assert.Equal(SessionPhase.Closed, us.PhaseAt(new DateTimeOffset(2026, 9, 28, 20, 30, 0, TimeSpan.Zero)));

        // 22:30 Stockholm on Monday is 16:30 Monday in New York: the next decision is Tuesday 09:40 New York.
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 13, 40, 0, TimeSpan.Zero), us.NextDecision(new DateTimeOffset(2026, 9, 28, 20, 30, 0, TimeSpan.Zero)).DecisionUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 14, 40, 0, TimeSpan.Zero), us.NextDecision(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero)).DecisionUtc);
    }

    [Fact]
    public void TheStockholmSchedule_IsUnchanged()
    {
        SessionPlan day = new TradingSchedule(OrderGatewayTests.Calendar(), RiskLimits.AdrDefaults, new TimeOnly(9, 10)).Plan(new DateOnly(2026, 9, 28))!;
        Assert.Equal(
            [new DateTimeOffset(2026, 9, 28, 7, 5, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 28, 7, 10, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 28, 15, 20, 0, TimeSpan.Zero)],
            [day.WindowOpenUtc, day.DecisionUtc, day.WindowCloseUtc]);
    }

    [Fact]
    public void TheFxTable_KnowsSekAlways_AndRefusesANonPositiveRate()
    {
        Assert.Equal(1m, FxTable.SekOnly.SekPerUnit("SEK"));
        Assert.Null(FxTable.SekOnly.SekPerUnit("USD"));
        Assert.Equal(10m, Fx.SekPerUnit("USD"));
        Assert.Null(Fx.SekPerUnit("CAD"));
        Assert.Equal("USD 10.0000 SEK", Fx.ToString());
        Assert.Equal("SEK only", FxTable.SekOnly.ToString());
        Assert.Throws<ArgumentException>(() => new FxTable(new Dictionary<string, decimal> { ["CAD"] = 0m }));
    }
}
