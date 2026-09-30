using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

/// <summary>
/// ADR 0003 §4: one pass and one fail test per check. The baseline passes all 21 checks with room to spare, and each
/// fail case changes one thing so that exactly that check fails (a stronger statement than "it fails somewhere").
/// </summary>
public sealed class RiskEngineTests
{
    internal static readonly OrderbookId Eric = new("5240");
    internal static readonly OrderbookId Other = new("9999");

    // Monday 2026-09-28 10:00 Stockholm (CEST, UTC+2).
    internal static readonly DateTimeOffset Now = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);

    private static readonly PreTradeRiskEngine Engine = new(RiskLimits.AdrDefaults);

    internal static PreparedOrder Buy(long volume = 10, decimal limit = 100.00m, string condition = "NORMAL", IntentOrderType type = IntentOrderType.Limit) =>
        Order(OrderSide.Buy, volume, limit, condition, type);

    internal static PreparedOrder Sell(long volume = 10, decimal limit = 100.00m) => Order(OrderSide.Sell, volume, limit);

    internal static PreparedOrder Order(OrderSide side, long volume, decimal? limit, string condition = "NORMAL", IntentOrderType type = IntentOrderType.Limit) =>
        new(new OrderIntent(Eric, "ERIC B", side, volume, limit, "test", 100m, Now, "test", type, condition), volume, limit, limit);

    internal static Quote FreshQuote(DateTimeOffset now, bool stale = false) => new(
        Eric, 100.4m, 500, 100.6m, 700, 100.5m, now.AddSeconds(-2), 1_000_000, [], QuoteSource.Stream, now.AddSeconds(-1), now.AddSeconds(-3),
        now.AddSeconds(-1), now, stale, stale ? "depth stream disconnected" : null);

    internal static RiskContext Baseline(TradingMode mode = TradingMode.Paper) => new()
    {
        Mode = mode,
        NowUtc = Now,
        Account = new AccountId("12345678"),
        AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { "12345678" },
        Universe = new Universe([new UniverseEntry(Eric, "ERIC B", "Ericsson B")]),
        AccountValue = 100_000m,
        AvailableCash = 50_000m,
        Positions = new Dictionary<OrderbookId, long> { [Eric] = 100 },
        PositionValues = new Dictionary<OrderbookId, decimal> { [Eric] = 10_000m },
        OpenOrders = [],
        Quote = FreshQuote(Now),
        OrdersPlacedToday = 0,
        RecentActionsUtc = [],
        LastActionOnInstrumentUtc = null,
        RecentIntents = [],
        Today = new TradingDay(new DateOnly(2026, 9, 28), TradingDayKind.Full, new TimeOnly(9, 0), new TimeOnly(17, 30), null),
        Halts = [],
        StartOfDayValue = 100_000m,
        EstimatedFees = 39m,
        Verified = new VerifiedConstants(false, false, false),
        Preflight = null,
    };

    [Fact]
    public void Baseline_PassesAllTwentyOneChecks()
    {
        RiskReport report = Engine.Evaluate(Buy(), Baseline());
        Assert.Equal(PreTradeRiskEngine.CheckCount, report.Checks.Count);
        Assert.Equal(Enumerable.Range(1, 21).Select(i => $"R{i}"), report.Checks.Select(c => c.Id));
        Assert.True(report.Passed, string.Join("; ", report.Failures.Select(f => $"{f.Id}: {f.Message} ({f.Observed})")));
        Assert.True(Engine.Evaluate(Sell(), Baseline()).Passed);
    }

    public static TheoryData<string, string> FailCases => new()
    {
        { "R1", "account not allowed" },
        { "R2", "instrument not in universe" },
        { "R3", "fill-or-kill condition" },
        { "R4", "sell more than held" },
        { "R5", "limit 10 % from reference" },
        { "R6", "order above 10 % of account" },
        { "R7", "position above 20 %" },
        { "R8", "gross exposure above 100 %" },
        { "R9", "cash below value + fees" },
        { "R10", "21st order today" },
        { "R11", "6th action this minute" },
        { "R12", "2 s after the last action" },
        { "R13", "opposite order working" },
        { "R14", "identical intent 30 s ago" },
        { "R15", "stale quote" },
        { "R16", "17:25, after the window" },
        { "R17", "halted" },
        { "R18", "unknown order in the instrument" },
        { "R19", "down 3 % today" },
        { "R20", "live with unverified constants" },
        { "R21", "live with a failed preflight" },
    };

    [Theory]
    [MemberData(nameof(FailCases))]
    public void EachCheck_FailsAlone_WhenItsConditionIsBroken(string id, string description)
    {
        (PreparedOrder order, RiskContext ctx) = Broken(id);
        RiskReport report = Engine.Evaluate(order, ctx);

        Assert.False(report.Passed, description);
        Assert.Equal([id], report.Failures.Select(f => f.Id));
        RiskCheckResult failed = report[id];
        Assert.NotEqual("ok", failed.Message);
        Assert.False(string.IsNullOrWhiteSpace(failed.Observed));
        Assert.False(string.IsNullOrWhiteSpace(failed.Limit));
    }

    [Theory]
    [MemberData(nameof(FailCases))]
    public void EachCheck_PassesAtItsBoundary(string id, string description)
    {
        // The limit itself is allowed (<= / >=), so these must pass.
        _ = description;
        (PreparedOrder order, RiskContext ctx) = AtBoundary(id);
        RiskReport report = Engine.Evaluate(order, ctx);
        Assert.True(report[id].Passed, $"{id}: {report[id].Message} ({report[id].Observed} vs {report[id].Limit})");
        Assert.True(report.Passed, string.Join("; ", report.Failures.Select(f => $"{f.Id}: {f.Message}")));
    }

    // ---- The account cap (ADR 0003 §4, Changes 2026-09-27): a 100,000 SEK account sized on 5,000 SEK -------------

    private static readonly PreTradeRiskEngine CappedEngine = new(RiskLimits.AdrDefaults with { MaxAccountValueSek = 5_000m });

    [Fact]
    public void R2_LetsAnExitingShareBeSold_NeverBought()
    {
        // Plan 21: off the list while held; R4 still caps the sell at the position (100).
        RiskContext exiting = Baseline() with { Universe = new Universe([], [new UniverseEntry(Eric, "ERIC B", "Ericsson B")]) };
        Assert.True(Engine.Evaluate(Sell(100), exiting)["R2"].Passed);
        Assert.False(Engine.Evaluate(Sell(101), exiting)["R4"].Passed);
        RiskCheckResult buy = Engine.Evaluate(Buy(), exiting)["R2"];
        Assert.False(buy.Passed);
        Assert.Contains("may only be sold", buy.Message, StringComparison.Ordinal);
        Assert.False(Engine.Evaluate(Sell(), Baseline() with { Universe = Universe.Empty })["R2"].Passed); // not listed at all
    }

    private static RiskContext SmallBook(decimal ericValue = 400m, decimal otherValue = 0m) => Baseline() with
    {
        Positions = new Dictionary<OrderbookId, long> { [Eric] = (long)(ericValue / 100m) },
        PositionValues = new Dictionary<OrderbookId, decimal> { [Eric] = ericValue, [Other] = otherValue },
    };

    [Theory]
    [InlineData(5, true)] // 500 SEK: 10 % of the 5,000 SEK cap, not of the 100,000 SEK account
    [InlineData(6, false)]
    public void R6_IsSizedOnTheAccountCap(long volume, bool passes)
    {
        RiskCheckResult r6 = CappedEngine.Evaluate(Buy(volume), SmallBook())["R6"];
        Assert.Equal(passes, r6.Passed);
        Assert.Equal("500.00 SEK = min(25,000.00 SEK, 10 % of 5,000.00 SEK) (sized on the 5,000.00 SEK account cap, not the account's 100,000.00 SEK)", r6.Limit);
    }

    [Theory]
    [InlineData(6, true)] // 400 held + 600 = 1,000 SEK: 20 % of the cap
    [InlineData(7, false)]
    public void R7_IsSizedOnTheAccountCap(long volume, bool passes)
    {
        RiskCheckResult r7 = CappedEngine.Evaluate(Buy(volume), SmallBook())["R7"];
        Assert.Equal(passes, r7.Passed);
        Assert.StartsWith("1,000.00 SEK (sized on the 5,000.00 SEK account cap", r7.Limit, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3, true)] // 4,700 in other shares + 300 = 5,000 SEK: 100 % of the cap
    [InlineData(4, false)]
    public void R8_IsSizedOnTheAccountCap_CountingEveryHolding(long volume, bool passes)
    {
        RiskCheckResult r8 = CappedEngine.Evaluate(Buy(volume), SmallBook(ericValue: 0m, otherValue: 4_700m))["R8"];
        Assert.Equal(passes, r8.Passed);
        Assert.StartsWith("5,000.00 SEK (sized on the 5,000.00 SEK account cap", r8.Limit, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(99_901, true)] // 99 SEK lost
    [InlineData(99_900, false)] // 100 SEK: 2 % of the cap, although the account lost only 0.1 %
    public void R19_IsSizedOnTheAccountCap(int value, bool passes)
    {
        RiskCheckResult r19 = CappedEngine.Evaluate(Buy(1), SmallBook() with { AccountValue = value })["R19"];
        Assert.Equal(passes, r19.Passed);
        Assert.Equal("a loss below 100.00 SEK = 2 % of 5,000.00 SEK, the account cap", r19.Limit);
        Assert.EndsWith(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"today ({value - 100_000:N2} SEK)"), r19.Observed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3_921, true)] // 79 SEK lost
    [InlineData(3_920, false)] // exactly -2 %
    public void BelowTheCap_TheAccountItselfIsTheSize(int value, bool passes)
    {
        RiskContext small = SmallBook() with { AccountValue = 4_000m, StartOfDayValue = 4_000m };
        RiskReport report = CappedEngine.Evaluate(Buy(4), small with { AccountValue = value });
        Assert.Equal(passes, report["R19"].Passed);
        Assert.Equal("> -2 %", report["R19"].Limit);
        Assert.Equal("400.00 SEK = min(25,000.00 SEK, 10 % of 4,000.00 SEK)", CappedEngine.Evaluate(Buy(4), small)["R6"].Limit);
        Assert.DoesNotContain(report.Checks, c => c.Limit.Contains("account cap", StringComparison.Ordinal));
    }

    [Fact]
    public void AllChecksAreReported_EvenWhenSeveralFail()
    {
        RiskContext ctx = Baseline() with { Universe = Universe.Empty, Quote = null, Halts = [new HaltState(HaltReason.KillSwitch, "test", Now)] };
        RiskReport report = Engine.Evaluate(Buy(), ctx);
        Assert.Equal(21, report.Checks.Count);
        Assert.Equal(["R2", "R5", "R15", "R17"], report.Failures.Select(f => f.Id));
    }

    [Fact]
    public void ReferencePrice_IsTheFreshLastTrade_ElseTheMid_ElseNone()
    {
        Assert.Equal(100.5m, Engine.ReferencePrice(FreshQuote(Now), Now));
        Quote oldLast = FreshQuote(Now) with { TimeOfLastUtc = Now.AddMinutes(-5) };
        Assert.Equal(100.5m, Engine.ReferencePrice(oldLast, Now)); // mid of 100.4 / 100.6
        Assert.Null(Engine.ReferencePrice(oldLast with { Bid = null }, Now));
        Assert.Null(Engine.ReferencePrice(null, Now));
    }

    [Fact]
    public void MarketOrdersAndClosedDays_Fail()
    {
        Assert.False(Engine.Evaluate(Order(OrderSide.Buy, 10, null, type: IntentOrderType.Market), Baseline())["R3"].Passed);
        RiskContext holiday = Baseline() with { Today = new TradingDay(new DateOnly(2026, 12, 24), TradingDayKind.Closed, null, null, "Christmas Eve") };
        Assert.False(Engine.Evaluate(Buy(), holiday)["R16"].Passed);
        Assert.False(Engine.Evaluate(Buy(), Baseline() with { Today = null })["R16"].Passed);
        RiskContext halfDay = Baseline() with
        {
            NowUtc = Now.AddHours(3), // 13:00 Stockholm
            Quote = FreshQuote(Now.AddHours(3)),
            Today = new TradingDay(new DateOnly(2026, 9, 28), TradingDayKind.Half, new TimeOnly(9, 0), new TimeOnly(13, 0), "half day"),
        };
        Assert.False(Engine.Evaluate(Buy(), halfDay)["R16"].Passed); // half days close the window at 12:50
        Assert.False(Engine.Evaluate(Buy(), Baseline() with { StartOfDayValue = 0 })["R19"].Passed);
        Assert.False(Engine.Evaluate(Buy(), Baseline(TradingMode.Auto) with { Verified = new VerifiedConstants(true, true, true) })["R21"].Passed); // live without preflight
    }

    private static (PreparedOrder, RiskContext) Broken(string id)
    {
        RiskContext b = Baseline();
        return id switch
        {
            "R1" => (Buy(), b with { AllowedAccountIds = new HashSet<string> { "87654321" } }),
            "R2" => (Buy(), b with { Universe = Universe.Empty }),
            "R3" => (Buy(condition: "FILL_OR_KILL"), b),
            "R4" => (Sell(volume: 10), b with { Positions = new Dictionary<OrderbookId, long> { [Eric] = 5 } }), // R6 also caps large sells
            "R5" => (Buy(limit: 110.60m), b),
            "R6" => (Buy(volume: 101), b with { Positions = new Dictionary<OrderbookId, long>(), PositionValues = new Dictionary<OrderbookId, decimal>() }),
            "R7" => (Buy(), b with { PositionValues = new Dictionary<OrderbookId, decimal> { [Eric] = 19_500m } }),
            "R8" => (Buy(), b with { PositionValues = new Dictionary<OrderbookId, decimal> { [Eric] = 10_000m, [Other] = 89_500m } }),
            "R9" => (Buy(), b with { AvailableCash = 1_000m }),
            "R10" => (Buy(), b with { OrdersPlacedToday = 20 }),
            "R11" => (Buy(), b with { RecentActionsUtc = [.. Enumerable.Range(1, 5).Select(i => Now.AddSeconds(-5 * i))] }),
            "R12" => (Buy(), b with { LastActionOnInstrumentUtc = Now.AddSeconds(-2) }),
            "R13" => (Buy(), b with { OpenOrders = [new OpenOrderView(Guid.NewGuid(), Eric, OrderSide.Sell, 5, 101m, false)] }),
            "R14" => (Buy(), b with { RecentIntents = [new RecentIntent(Eric, OrderSide.Buy, 10, 100.00m, Now.AddSeconds(-30))] }),
            "R15" => (Buy(), b with { Quote = FreshQuote(Now, stale: true) }),
            "R16" => (Buy(), b with { NowUtc = Now.AddHours(7).AddMinutes(25), Quote = FreshQuote(Now.AddHours(7).AddMinutes(25)) }),
            "R17" => (Buy(), b with { Halts = [new HaltState(HaltReason.StaleData, "stream down", Now)] }),
            "R18" => (Buy(), b with { OpenOrders = [new OpenOrderView(Guid.NewGuid(), Eric, OrderSide.Buy, 1, 100m, true)] }),
            "R19" => (Buy(), b with { AccountValue = 97_000m }),
            "R20" => (Buy(), Baseline(TradingMode.Confirm) with { Preflight = new BrokerPreflight(true, []) }),
            "R21" => (Buy(), Baseline(TradingMode.Confirm) with { Verified = new VerifiedConstants(true, true, true), Preflight = new BrokerPreflight(false, ["priceRampingWarning"]) }),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }

    private static (PreparedOrder, RiskContext) AtBoundary(string id)
    {
        RiskContext b = Baseline();
        return id switch
        {
            "R1" => (Buy(), b),
            "R2" => (Buy(), b),
            "R3" => (Buy(), b),
            "R4" => (Sell(volume: 100), b), // exactly the position
            "R5" => (Buy(limit: 102.51m), b), // 2.00 % from 100.5 (rounded limit)
            "R6" => (Buy(volume: 100), b with { Positions = new Dictionary<OrderbookId, long>(), PositionValues = new Dictionary<OrderbookId, decimal>() }), // exactly 10,000
            "R7" => (Buy(), b with { PositionValues = new Dictionary<OrderbookId, decimal> { [Eric] = 19_000m } }), // exactly 20,000
            "R8" => (Buy(), b with { PositionValues = new Dictionary<OrderbookId, decimal> { [Eric] = 10_000m, [Other] = 89_000m } }), // exactly 100,000
            "R9" => (Buy(), b with { AvailableCash = 1_039m }),
            "R10" => (Buy(), b with { OrdersPlacedToday = 19 }),
            "R11" => (Buy(), b with { RecentActionsUtc = [.. Enumerable.Range(1, 4).Select(i => Now.AddSeconds(-5 * i)).Append(Now.AddSeconds(-61))] }),
            "R12" => (Buy(), b with { LastActionOnInstrumentUtc = Now.AddSeconds(-5) }),
            "R13" => (Buy(), b with { OpenOrders = [new OpenOrderView(Guid.NewGuid(), Other, OrderSide.Sell, 5, 101m, false)] }), // other instrument
            "R14" => (Buy(), b with { RecentIntents = [new RecentIntent(Eric, OrderSide.Buy, 10, 100.00m, Now.AddSeconds(-61))] }), // outside the window
            "R15" => (Buy(), b with { Quote = FreshQuote(Now) with { AsOfUtc = Now.AddSeconds(-10) } }),
            "R16" => (Buy(), b with { NowUtc = Now.AddHours(-1).AddMinutes(5), Quote = FreshQuote(Now.AddHours(-1).AddMinutes(5)) }), // 09:05
            "R17" => (Buy(), b),
            "R18" => (Buy(), b with { OpenOrders = [new OpenOrderView(Guid.NewGuid(), Other, OrderSide.Buy, 1, 100m, true)] }), // unknown elsewhere
            "R19" => (Buy(), b with { AccountValue = 98_100m }), // -1.9 %
            "R20" => (Buy(), Baseline(TradingMode.Confirm) with { Verified = new VerifiedConstants(true, true, true), Preflight = new BrokerPreflight(true, []) }),
            "R21" => (Buy(), Baseline(TradingMode.Confirm) with { Verified = new VerifiedConstants(true, true, true), Preflight = new BrokerPreflight(true, []) }),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }
}
