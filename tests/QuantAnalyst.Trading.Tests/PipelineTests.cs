using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Pipeline;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

public sealed class OrderPreparationTests
{
    // Two bands with different ticks, like a Nasdaq Stockholm table around 100 SEK.
    internal static readonly TickSizeTable Ticks = new([new TickSizeBand(0m, 99.99m, 0.01m), new TickSizeBand(100m, 999.9m, 0.1m)]);

    internal static InstrumentSpec Spec(long lot = 1, string currency = "SEK") =>
        new(RiskEngineTests.Eric, "ERIC B", "Ericsson B", currency, lot, Ticks, TickTableVerified: false);

    private static OrderIntent Intent(OrderSide side, long qty, decimal? limit) =>
        new(RiskEngineTests.Eric, "ERIC B", side, qty, limit, "test", limit, RiskEngineTests.Now, "test");

    [Theory]
    [InlineData(25, 10, 20)]
    [InlineData(20, 10, 20)]
    [InlineData(9, 10, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(0, 1, 0)]
    [InlineData(-5, 1, 0)]
    public void Normalize_KeepsWholeLots_AndNeverRoundsUp(long quantity, long lot, long expected) =>
        Assert.Equal(expected, OrderPreparation.Normalize(quantity, lot));

    [Fact]
    public void Normalize_RefusesALotBelowOne() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderPreparation.Normalize(10, 0));

    [Theory]
    [InlineData(OrderSide.Buy, 100.37, 100.3, "100.37 rounded down to 100.3")]
    [InlineData(OrderSide.Sell, 100.31, 100.4, "100.31 rounded up to 100.4")]
    [InlineData(OrderSide.Buy, 99.999, 99.99, "99.999 rounded down to 99.99")]
    [InlineData(OrderSide.Sell, 99.991, 100.0, "99.991 rounded up to 100.00")] // crosses into the 0.1 band
    [InlineData(OrderSide.Buy, 100.3, 100.3, "on tick")]
    public void Prepare_RoundsThePassiveWay(OrderSide side, double limit, double expected, string note)
    {
        PreparedOrder p = OrderPreparation.Prepare(Intent(side, 10, (decimal)limit), Spec());
        Assert.Equal((decimal)expected, p.LimitPrice);
        Assert.Equal((decimal)limit, p.RoundedFrom);
        Assert.True(Ticks.IsOnTick(p.LimitPrice!.Value));
        Assert.Equal(note, OrderPreparation.RoundingNote(p));
    }

    [Fact]
    public void Prepare_TurnsTheQuantityIntoWholeLots()
    {
        PreparedOrder p = OrderPreparation.Prepare(Intent(OrderSide.Buy, 37, 100m), Spec(lot: 10));
        Assert.Equal(30, p.Volume);
        Assert.Equal(3_000m, p.Value);
    }

    [Fact]
    public void Prepare_LeavesAMarketOrderWithoutALimit_ForR3ToReject()
    {
        PreparedOrder p = OrderPreparation.Prepare(Intent(OrderSide.Buy, 10, null), Spec());
        Assert.Null(p.LimitPrice);
        Assert.Equal(0m, p.Value);
        Assert.False(new PreTradeRiskEngine(RiskLimits.AdrDefaults).Evaluate(p, RiskEngineTests.Baseline())["R3"].Passed);
    }

    public static TheoryData<string, long, decimal?, string, string> Refusals => new()
    {
        { "SEK", 5, 100m, "SEK lot 10", "less than one lot of 10" },
        { "SEK", 10, 0m, "SEK", "must be positive" },
        { "SEK", 10, -1m, "SEK", "must be positive" },
        { "SEK", 10, 5_000m, "SEK", "no valid tick for limit 5000" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Prepare_RefusesWhatCannotBeAnOrder(string currency, long qty, decimal? limit, string spec, string expected)
    {
        InstrumentSpec s = Spec(lot: spec.EndsWith("lot 10", StringComparison.Ordinal) ? 10 : 1, currency: currency);
        OrderPreparationException ex = Assert.Throws<OrderPreparationException>(() => OrderPreparation.Prepare(Intent(OrderSide.Buy, qty, limit), s));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_RefusesASpecOfAnotherInstrument() =>
        Assert.Throws<ArgumentException>(() => OrderPreparation.Prepare(Intent(OrderSide.Buy, 10, 100m), Spec() with { OrderbookId = RiskEngineTests.Other }));
}

public sealed class PromotionStateTests
{
    private static void Write(TempDir dir, string file, string maxAllowed, int records = 0) =>
        File.WriteAllText(dir.File(file), $$"""{ "maxAllowed": "{{maxAllowed}}", "records": [{{string.Join(",", Enumerable.Repeat("{}", records))}}] }""");

    [Fact]
    public void NoFile_MeansPaper()
    {
        using var dir = new TempDir();
        PromotionState s = PromotionState.Load(dir.Path);
        Assert.Equal(TradingMode.Paper, s.MaxAllowed);
        Assert.Equal(0, s.Records);
        Assert.Contains("none", s.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLocalState_WinsOverTheTemplate()
    {
        using var dir = new TempDir();
        Write(dir, PromotionState.TemplateFile, "Backtest");
        Assert.Equal(TradingMode.Backtest, PromotionState.Load(dir.Path).MaxAllowed);

        Write(dir, PromotionState.StateFile, "Confirm", records: 2);
        PromotionState s = PromotionState.Load(dir.Path);
        Assert.Equal(TradingMode.Confirm, s.MaxAllowed);
        Assert.Equal(2, s.Records);
        Assert.EndsWith(PromotionState.StateFile, s.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommittedTemplate_AllowsPaperOnly_AndTheRealStateIsIgnoredByGit()
    {
        string root = TradingConfigTests.RepoRoot();
        using var copy = new TempDir(); // the template alone, whatever the owner's local state.json says
        File.Copy(Path.Combine(root, "promotion", PromotionState.TemplateFile), copy.File(PromotionState.TemplateFile));
        PromotionState s = PromotionState.Load(copy.Path);
        Assert.Equal(TradingMode.Paper, s.MaxAllowed);
        Assert.Equal(0, s.Records);
        Assert.Contains("promotion/state.json", File.ReadAllLines(Path.Combine(root, ".gitignore")));
    }

    [Theory]
    [InlineData("""{ "maxAllowed": "Live" }""", "is not Backtest, Paper, Confirm or Auto")]
    [InlineData("""{ "maxAllowed": "paper" }""", "is not Backtest, Paper, Confirm or Auto")]
    [InlineData("""{ "maxAllowed": "7" }""", "is not Backtest, Paper, Confirm or Auto")]
    [InlineData("""{ "records": [] }""", "not a valid promotion state")]
    [InlineData("""{ "maxAllowed": """, "not a valid promotion state")]
    public void ABadFile_FailsLoudly(string json, string expected)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File(PromotionState.StateFile), json);
        TradingConfigException ex = Assert.Throws<TradingConfigException>(() => PromotionState.Load(dir.Path));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TradingMode.Paper, TradingMode.Backtest, true, null)]
    [InlineData(TradingMode.Paper, TradingMode.Paper, true, null)]
    [InlineData(TradingMode.Backtest, TradingMode.Paper, false, "above the promotion state")]
    [InlineData(TradingMode.Paper, TradingMode.Confirm, false, "above the promotion state")]
    [InlineData(TradingMode.Confirm, TradingMode.Confirm, true, null)]
    [InlineData(TradingMode.Auto, TradingMode.Confirm, true, null)]
    [InlineData(TradingMode.Confirm, TradingMode.Auto, false, "above the promotion state")]
    [InlineData(TradingMode.Auto, TradingMode.Auto, false, "Phase 8")]
    public void Effective_IsTheRequestedMode_OrAnError_NeverADowngrade(TradingMode max, TradingMode requested, bool allowed, string? error)
    {
        var s = new PromotionState(max, 0, "test");
        if (allowed)
        {
            Assert.Equal(requested, s.Effective(requested));
            return;
        }

        ModeNotAllowedException ex = Assert.Throws<ModeNotAllowedException>(() => s.Effective(requested));
        Assert.Contains(error!, ex.Message, StringComparison.Ordinal);
    }
}

public sealed class OrderManagerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now);
    private readonly AuditLog _audit;
    private readonly HaltController _halts;
    private readonly OrderManager _oms;

    public OrderManagerTests()
    {
        _audit = new AuditLog(_dir.Path, _time);
        _halts = new HaltController(_audit, _time);
        _oms = new OrderManager(_audit, _halts, _time);
    }

    public void Dispose() => _dir.Dispose();

    // ADR 0003 §6, spelled out independently of the implementation's table.
    private static readonly HashSet<(OmsState, OmsState)> Legal =
    [
        (OmsState.New, OmsState.Sent),
        (OmsState.Sent, OmsState.Working), (OmsState.Sent, OmsState.Rejected), (OmsState.Sent, OmsState.Unknown),
        (OmsState.Working, OmsState.PartiallyFilled), (OmsState.Working, OmsState.Filled), (OmsState.Working, OmsState.Cancelled), (OmsState.Working, OmsState.Unknown),
        (OmsState.PartiallyFilled, OmsState.PartiallyFilled), (OmsState.PartiallyFilled, OmsState.Filled), (OmsState.PartiallyFilled, OmsState.Cancelled), (OmsState.PartiallyFilled, OmsState.Unknown),
        (OmsState.Unknown, OmsState.Working), (OmsState.Unknown, OmsState.PartiallyFilled), (OmsState.Unknown, OmsState.Filled), (OmsState.Unknown, OmsState.Cancelled), (OmsState.Unknown, OmsState.Rejected),
    ];

    [Fact]
    public void TheTransitionTable_IsExactlyTheAdrOne()
    {
        foreach (OmsState from in Enum.GetValues<OmsState>())
        {
            foreach (OmsState to in Enum.GetValues<OmsState>())
            {
                Assert.True(Legal.Contains((from, to)) == OrderManager.IsAllowed(from, to), $"{from} -> {to}");
            }
        }

        // Terminal states are final, and nothing goes back to New.
        Assert.All([OmsState.Filled, OmsState.Cancelled, OmsState.Rejected], s => Assert.DoesNotContain(Legal, t => t.Item1 == s));
        Assert.DoesNotContain(Legal, t => t.Item2 == OmsState.New);
    }

    [Fact]
    public void EveryChange_IsTold_AndAnObserverThatThrows_ChangesNothing()
    {
        var seen = new List<(OmsState State, long Filled)>();
        _oms.Changed += o => seen.Add((o.State, o.FilledVolume));
        _oms.Changed += _ => throw new InvalidOperationException("a broken observer");

        OmsOrder o = Working(volume: 10);
        _oms.ApplyFill(o.ClientOrderId, 4, 100m, 0m, "test");
        _oms.ApplyFill(o.ClientOrderId, 6, 101m, 0m, "test");

        Assert.Equal([(OmsState.New, 0L), (OmsState.Sent, 0L), (OmsState.Working, 0L), (OmsState.PartiallyFilled, 4L), (OmsState.Filled, 10L)], seen);
        Assert.Equal((OmsState.Filled, 10L), (o.State, o.FilledVolume)); // the throwing handler stopped nothing
        Assert.False(_halts.IsHalted);
        Assert.True(_oms.TryTransition(NewOrder().ClientOrderId, OmsState.Sent, "test", null, OmsState.New));
        Assert.Equal(OmsState.Sent, seen[^1].State);
    }

    private OmsOrder NewOrder(long volume = 10) =>
        _oms.Create(Guid.NewGuid(), new AccountId("PAPER"), RiskEngineTests.Eric, "ERIC B", OrderSide.Buy, volume, 100m);

    private OmsOrder Working(long volume = 10)
    {
        OmsOrder o = NewOrder(volume);
        _oms.Transition(o.ClientOrderId, OmsState.Sent, "test");
        _oms.Transition(o.ClientOrderId, OmsState.Working, "test", new OrderId("B1-" + o.ClientOrderId));
        return o;
    }

    [Fact]
    public void AnIllegalTransition_HaltsTrading_Throws_AndLeavesTheOrderAsItWas()
    {
        OmsOrder o = NewOrder();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => _oms.Transition(o.ClientOrderId, OmsState.Filled, "skip ahead"));
        Assert.Contains("New -> Filled", ex.Message, StringComparison.Ordinal);
        Assert.Equal(OmsState.New, o.State);
        Assert.True(_halts.IsActive(HaltReason.OmsInvariant));
    }

    [Fact]
    public void Fills_Accumulate_ToPartialThenFilled_WithAverageAndFees()
    {
        OmsOrder o = Working(10);
        _oms.ApplyFill(o.ClientOrderId, 4, 100m, 1m, "test");
        Assert.Equal(OmsState.PartiallyFilled, o.State);
        Assert.Equal(6, o.Remaining);
        _oms.ApplyFill(o.ClientOrderId, 6, 99.5m, 1.5m, "test");
        Assert.Equal(OmsState.Filled, o.State);
        Assert.Equal(0, o.Remaining);
        Assert.Equal(99.7m, o.AverageFillPrice); // (400 + 597) / 10
        Assert.Equal(2.5m, o.Fees);
        Assert.True(o.IsTerminal);
        Assert.False(o.IsOpen);
        Assert.Empty(_oms.Open);
        Assert.False(_halts.IsHalted);
    }

    [Theory]
    [InlineData(11, 100, "would overfill")]
    [InlineData(0, 100, "is not positive")]
    [InlineData(5, 0, "is not positive")]
    public void ABadFill_HaltsTrading_AndChangesNothing(long volume, int price, string expected)
    {
        OmsOrder o = Working(10);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => _oms.ApplyFill(o.ClientOrderId, volume, price, 0m, "test"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(OmsState.Working, o.State);
        Assert.Equal(0, o.FilledVolume);
        Assert.True(_halts.IsActive(HaltReason.OmsInvariant));
    }

    [Fact]
    public void AFillBeforeTheBrokerAccepted_OrAfterTheEnd_HaltsTrading()
    {
        OmsOrder sent = NewOrder();
        _oms.Transition(sent.ClientOrderId, OmsState.Sent, "test");
        Assert.Throws<InvalidOperationException>(() => _oms.ApplyFill(sent.ClientOrderId, 1, 100m, 0m, "test"));

        OmsOrder cancelled = Working();
        _oms.Transition(cancelled.ClientOrderId, OmsState.Cancelled, "test");
        Assert.Throws<InvalidOperationException>(() => _oms.ApplyFill(cancelled.ClientOrderId, 1, 100m, 0m, "test"));
        Assert.True(_halts.IsActive(HaltReason.OmsInvariant));
    }

    [Fact]
    public void AnUnknownOrder_CanBeResolvedByAFill_AndBlocksItsInstrumentMeanwhile()
    {
        OmsOrder o = Working(10);
        _oms.Transition(o.ClientOrderId, OmsState.Unknown, "cancel reply lost");
        Assert.True(o.IsOpen);
        Assert.True(o.View().IsUnknown);
        _oms.ApplyFill(o.ClientOrderId, 10, 100m, 0m, "reconciliation");
        Assert.Equal(OmsState.Filled, o.State);
        Assert.False(_halts.IsHalted);
    }

    [Fact]
    public void AClientIdUsedTwice_OrNeverCreated_HaltsTrading()
    {
        OmsOrder o = NewOrder();
        Assert.Throws<InvalidOperationException>(() => _oms.Create(o.ClientOrderId, o.Account, o.OrderbookId, o.Ticker, o.Side, 1, 1m));
        Assert.Throws<InvalidOperationException>(() => _oms.Transition(Guid.NewGuid(), OmsState.Sent, "test"));
        Assert.True(_halts.IsActive(HaltReason.OmsInvariant));
    }

    [Fact]
    public void EveryStepIsAudited_InAValidChain()
    {
        OmsOrder o = Working(10);
        _oms.ApplyFill(o.ClientOrderId, 10, 100m, 0m, "test");
        string file = Directory.GetFiles(_dir.Path).Single();
        Assert.Equal(["oms-new", "oms-state", "oms-state", "oms-fill"], AuditLog.Read(file).Select(r => r.GetProperty("kind").GetString()));
        Assert.True(AuditLog.Verify(_dir.Path).Valid);
        Assert.Same(o, _oms.FindByBrokerId(o.BrokerOrderId!.Value));
    }
}
