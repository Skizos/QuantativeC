using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

/// <summary>A scriptable simulated channel: records every call and answers what the test says.</summary>
internal sealed class FakeSimulatedChannel : ISimulatedOrderChannel
{
    private int _next;
    private int _inFlight;

    public event Action<SimulatedFill>? Filled;

    public event Action<OrderId, string>? Ended;

    public string Name => "fake-sim";

    public List<ApprovedOrder> Placed { get; } = [];

    public List<ApprovedCancel> Cancelled { get; } = [];

    public Func<ApprovedOrder, OrderSubmitResult>? OnPlace { get; set; }

    public Func<ApprovedCancel, OrderSubmitResult>? OnCancel { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public int MaxConcurrent { get; private set; }

    public async Task<OrderSubmitResult> PlaceAsync(ApprovedOrder order, CancellationToken ct)
    {
        int now = Interlocked.Increment(ref _inFlight);
        MaxConcurrent = Math.Max(MaxConcurrent, now);
        try
        {
            Placed.Add(order);
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }

            return OnPlace?.Invoke(order) ?? OrderSubmitResult.Accepted(new OrderId($"SIM-{++_next}"));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public Task<OrderSubmitResult> ModifyAsync(ApprovedModify modify, CancellationToken ct) =>
        Task.FromResult(OrderSubmitResult.Rejected("modify is not simulated"));

    public Task<OrderSubmitResult> CancelAsync(ApprovedCancel cancel, CancellationToken ct)
    {
        Cancelled.Add(cancel);
        return Task.FromResult(OnCancel?.Invoke(cancel) ?? OrderSubmitResult.Accepted(cancel.BrokerOrderId, "cancelled"));
    }

    public void Fill(Guid clientOrderId, OrderId brokerId, long volume, decimal price, decimal courtage = 0m) =>
        Filled?.Invoke(new SimulatedFill(clientOrderId, brokerId, volume, price, courtage, 0m, DateTimeOffset.UnixEpoch));

    public void End(OrderId brokerId, string reason) => Ended?.Invoke(brokerId, reason);

    public bool HasSubscribers => Filled is not null || Ended is not null;
}

/// <summary>A real broker channel stand-in: not simulated, so the Phase 6 gateway must refuse it.</summary>
internal sealed class FakeLiveChannel : IBrokerOrderChannel
{
    public string Name => "fake-live";

    public Task<OrderSubmitResult> PlaceAsync(ApprovedOrder order, CancellationToken ct) => throw new InvalidOperationException("must never be called");

    public Task<OrderSubmitResult> ModifyAsync(ApprovedModify modify, CancellationToken ct) => throw new InvalidOperationException("must never be called");

    public Task<OrderSubmitResult> CancelAsync(ApprovedCancel cancel, CancellationToken ct) => throw new InvalidOperationException("must never be called");
}

internal sealed class FixedAccount(AccountSnapshot snapshot) : IAccountState
{
    public AccountSnapshot Snapshot { get; set; } = snapshot;

    public Task<AccountSnapshot> GetAsync(CancellationToken ct) => Task.FromResult(Snapshot);
}

internal sealed class FreshQuotes(TimeProvider time) : IQuoteSource
{
    public Quote? Latest(OrderbookId id) => RiskEngineTests.FreshQuote(time.GetUtcNow()) with { OrderbookId = id };
}

public sealed class OrderGatewayTests : IDisposable
{
    private static readonly AccountId Paper = new("PAPER");
    private static readonly OrderbookId Test = new("1001"); // fake id, not a real orderbook

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now); // Monday 10:00 Stockholm
    private readonly FakeSimulatedChannel _channel = new();
    private readonly AuditLog _audit;
    private readonly HaltController _halts;
    private readonly OrderManager _oms;
    private readonly OrderGateway _gateway;

    public OrderGatewayTests()
    {
        _audit = new AuditLog(_dir.Path, _time);
        _halts = new HaltController(_audit, _time);
        _oms = new OrderManager(_audit, _halts, _time);
        _gateway = NewGateway(_channel, TradingMode.Paper);
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _dir.Dispose();
    }

    internal static MarketCalendar Calendar(DateOnly? verifiedOn = null) => new([
        new CalendarYear("XSTO", 2026, new TimeOnly(9, 0), new TimeOnly(17, 30), new TimeOnly(9, 0), new TimeOnly(13, 0), [], [], "test", verifiedOn),
    ]);

    private GatewayEnvironment Env(TradingMode mode) => new()
    {
        Mode = mode,
        Instruments = new InstrumentCatalog([
            OrderPreparationTests.Spec(),
            OrderPreparationTests.Spec() with { OrderbookId = Test, Ticker = "TEST B", Name = "Test B" },
        ]),
        Quotes = new FreshQuotes(_time),
        Account = new FixedAccount(new AccountSnapshot(
            Paper, 100_000m, 50_000m, new Dictionary<OrderbookId, long> { [RiskEngineTests.Eric] = 100 }, new Dictionary<OrderbookId, decimal> { [RiskEngineTests.Eric] = 10_000m }, 100_000m)),
        Calendar = Calendar(),
        Universe = new Universe([new UniverseEntry(RiskEngineTests.Eric, "ERIC B", "Ericsson B"), new UniverseEntry(Test, "TEST B", "Test B")]),
        AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { Paper.Value },
        Fees = (_, _) => 0m,
        CourtageVerified = true,
    };

    private OrderGateway NewGateway(IBrokerOrderChannel channel, TradingMode mode) =>
        new(channel, Env(mode), new PreTradeRiskEngine(RiskLimits.AdrDefaults), _oms, _halts, _audit, _time);

    private static OrderIntent Intent(OrderSide side = OrderSide.Buy, long qty = 10, decimal? limit = 100.37m, OrderbookId? id = null) =>
        new(id ?? RiskEngineTests.Eric, id == Test ? "TEST B" : "ERIC B", side, qty, limit, "test", limit, RiskEngineTests.Now, "test");

    private Task<SubmitResult> Submit(OrderIntent? intent = null) => _gateway.SubmitAsync(intent ?? Intent(), CancellationToken.None);

    private string[] AuditKinds() =>
        [.. Directory.GetFiles(_dir.Path).Order().SelectMany(AuditLog.Read).Select(r => r.GetProperty("kind").GetString()!)];

    [Theory]
    [InlineData(TradingMode.Confirm, "needs the order card with its typed confirmation and Avanza's pre-trade checks")]
    [InlineData(TradingMode.Auto, "not available before Phase 8")]
    public void Confirm_NeedsItsCardAndPreflight_AndAutoCannotStart(TradingMode mode, string expected)
    {
        ModeNotAllowedException ex = Assert.Throws<ModeNotAllowedException>(() => NewGateway(new FakeSimulatedChannel(), mode));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealBrokerChannel_IsRefused_EvenInPaper()
    {
        ModeNotAllowedException ex = Assert.Throws<ModeNotAllowedException>(() => NewGateway(new FakeLiveChannel(), TradingMode.Paper));
        Assert.Contains("not simulated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGoodIntent_IsRounded_Checked_Sent_AndWorking_WithEveryStepAudited()
    {
        SubmitResult r = await Submit();

        Assert.Equal(SubmitStatus.Accepted, r.Status);
        Assert.True(r.Risk!.Passed);
        ApprovedOrder sent = Assert.Single(_channel.Placed);
        Assert.Equal(100.3m, sent.LimitPrice); // rounded down (buy) before the risk check
        Assert.Equal(10, sent.Volume);
        Assert.Equal(Paper, sent.Account);
        Assert.Equal(new DateOnly(2026, 9, 28), sent.ValidUntil); // a day order, Stockholm date
        Assert.Equal(OmsState.Working, r.Order!.State);
        Assert.Equal(sent.ClientOrderId, r.Order.ClientOrderId);
        Assert.Equal(new OrderId("SIM-1"), r.Order.BrokerOrderId);

        Assert.Equal(
            ["gateway-start", "intent", "prepared", "risk", "gate", "oms-new", "submit", "oms-state", "submit-result", "oms-state"],
            AuditKinds());
        Assert.True(AuditLog.Verify(_dir.Path).Valid);
    }

    [Fact]
    public async Task ARiskFailure_SendsNothing_AndCreatesNoOrder()
    {
        SubmitResult r = await Submit(Intent(qty: 1_000)); // 100,300 SEK: R6, R7, R8, R9 ...

        Assert.Equal(SubmitStatus.RiskRejected, r.Status);
        Assert.Contains("R6", r.Message, StringComparison.Ordinal);
        Assert.Empty(_channel.Placed);
        Assert.Empty(_oms.All);
        Assert.DoesNotContain("submit", AuditKinds());
    }

    [Fact]
    public async Task AnInstrumentWithoutData_IsNotAnOrder()
    {
        SubmitResult r = await Submit(Intent(id: RiskEngineTests.Other));
        Assert.Equal(SubmitStatus.NotPrepared, r.Status);
        Assert.Contains("no instrument data", r.Message, StringComparison.Ordinal);
        Assert.Empty(_channel.Placed);
        Assert.Contains("not-prepared", AuditKinds());
    }

    [Fact]
    public async Task AMarketOrder_IsRejectedByR3_NotSentWithoutALimit()
    {
        SubmitResult r = await Submit(Intent(limit: null));
        Assert.Equal(SubmitStatus.RiskRejected, r.Status);
        Assert.Equal(["R3", "R5"], r.Risk!.Failures.Select(f => f.Id)); // no limit: no collar either
        Assert.Empty(_channel.Placed);
    }

    [Fact]
    public async Task BrokerRejections_AreCounted_AndAnAcceptResetsTheStreak()
    {
        var streaks = new List<int>();
        _gateway.BrokerRejected += (n, _) => streaks.Add(n);
        _channel.OnPlace = _ => OrderSubmitResult.Rejected("insufficient funds");

        for (int i = 0; i < 2; i++)
        {
            SubmitResult r = await Submit();
            Assert.Equal(SubmitStatus.BrokerRejected, r.Status);
            Assert.Equal(OmsState.Rejected, r.Order!.State);
            _time.Advance(TimeSpan.FromSeconds(61)); // past R12 and the R14 duplicate window
        }

        _channel.OnPlace = null;
        Assert.Equal(SubmitStatus.Accepted, (await Submit()).Status);
        _time.Advance(TimeSpan.FromSeconds(61));
        _channel.OnPlace = _ => OrderSubmitResult.Rejected("again");
        await Submit(Intent(OrderSide.Buy, 5)); // R13 would stop a sell against the working buy
        Assert.Equal([1, 2, 1], streaks);
    }

    [Fact]
    public async Task AnUnknownOutcome_IsNeverRetried_AndBlocksTheInstrument()
    {
        var unknown = new List<OmsOrder>();
        _gateway.OutcomeUnknown += unknown.Add;
        _channel.OnPlace = _ => throw new TimeoutException("no reply");

        SubmitResult first = await Submit();
        Assert.Equal(SubmitStatus.Unknown, first.Status);
        Assert.Equal(OmsState.Unknown, first.Order!.State);
        Assert.Contains("TimeoutException", first.Message, StringComparison.Ordinal);
        Assert.Single(_channel.Placed); // exactly one attempt
        Assert.Single(unknown);

        _time.Advance(TimeSpan.FromMinutes(5));
        _channel.OnPlace = null;
        SubmitResult second = await Submit(Intent(qty: 5));
        Assert.Equal(SubmitStatus.RiskRejected, second.Status);
        Assert.Equal(["R18"], second.Risk!.Failures.Select(f => f.Id));
        Assert.Single(_channel.Placed);

        // Other instruments still trade.
        Assert.Equal(SubmitStatus.Accepted, (await Submit(Intent(id: Test))).Status);
    }

    [Fact]
    public async Task AcceptedWithoutAnOrderId_IsTreatedAsUnknown()
    {
        _channel.OnPlace = _ => new OrderSubmitResult(SubmitOutcome.Accepted, null, "ok?");
        SubmitResult r = await Submit();
        Assert.Equal(SubmitStatus.Unknown, r.Status);
        Assert.Equal(OmsState.Unknown, r.Order!.State);
        Assert.Contains("without an order id", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fills_FromTheChannel_MoveTheOrderToPartialThenFilled()
    {
        SubmitResult r = await Submit();
        ApprovedOrder sent = _channel.Placed.Single();
        _channel.Fill(sent.ClientOrderId, r.Order!.BrokerOrderId!.Value, 4, 100.3m, courtage: 1m);
        Assert.Equal(OmsState.PartiallyFilled, r.Order.State);
        _channel.Fill(sent.ClientOrderId, r.Order.BrokerOrderId.Value, 6, 100.2m, courtage: 1m);
        Assert.Equal(OmsState.Filled, r.Order.State);
        Assert.Equal(2m, r.Order.Fees);
        Assert.False(_halts.IsHalted);
    }

    [Fact]
    public async Task AFillBeforeThePlaceReturns_IsAcceptedAsProofTheOrderExists()
    {
        _channel.OnPlace = o =>
        {
            _channel.Fill(o.ClientOrderId, new OrderId("SIM-X"), o.Volume, o.LimitPrice);
            return OrderSubmitResult.Accepted(new OrderId("SIM-X"));
        };
        SubmitResult r = await Submit();
        Assert.Equal(SubmitStatus.Accepted, r.Status);
        Assert.Equal(OmsState.Filled, r.Order!.State);
        Assert.Equal(new OrderId("SIM-X"), r.Order.BrokerOrderId);

        _time.Advance(TimeSpan.FromMinutes(2));
        _channel.OnPlace = o =>
        {
            _channel.Fill(o.ClientOrderId, new OrderId("SIM-Y"), o.Volume, o.LimitPrice);
            throw new TimeoutException("reply lost after the fill");
        };
        SubmitResult lost = await Submit(Intent(qty: 5));
        Assert.Equal(SubmitStatus.Accepted, lost.Status);
        Assert.Equal(OmsState.Filled, lost.Order!.State);
        Assert.Contains("submit-outcome-superseded", AuditKinds());
        Assert.False(_halts.IsHalted);
    }

    [Fact]
    public async Task ABadFill_HaltsTrading_WithoutThrowingIntoTheChannel()
    {
        SubmitResult r = await Submit();
        _channel.Fill(r.Order!.ClientOrderId, r.Order.BrokerOrderId!.Value, 11, 100.3m); // overfill
        Assert.True(_halts.IsActive(HaltReason.OmsInvariant));
        Assert.Contains("fill-refused", AuditKinds());
        Assert.Equal(0, r.Order.FilledVolume);

        _time.Advance(TimeSpan.FromMinutes(2));
        SubmitResult blocked = await Submit(Intent(id: Test));
        Assert.Equal(SubmitStatus.RiskRejected, blocked.Status);
        Assert.Contains("R17", blocked.Risk!.Failures.Select(f => f.Id));
    }

    [Fact]
    public async Task AFillForAnOrderNobodySent_HaltsForReconciliation()
    {
        _ = await Submit();
        _channel.Fill(Guid.NewGuid(), new OrderId("SIM-?"), 1, 100m);
        Assert.True(_halts.IsActive(HaltReason.Reconciliation));
    }

    [Fact]
    public async Task AnOrderTheChannelEnds_IsCancelled()
    {
        SubmitResult r = await Submit();
        _channel.End(r.Order!.BrokerOrderId!.Value, "day order expired at the close");
        Assert.Equal(OmsState.Cancelled, r.Order.State);
        Assert.Equal("day order expired at the close", r.Order.Message);
    }

    [Fact]
    public async Task Cancel_ReachesTheChannel_OnlyForWorkingOrders_AndWorksWhileHalted()
    {
        SubmitResult r = await Submit();
        _halts.Raise(HaltReason.KillSwitch, "test");

        OrderSubmitResult cancelled = await _gateway.CancelAsync(r.Order!.ClientOrderId, "kill switch", CancellationToken.None);
        Assert.Equal(SubmitOutcome.Accepted, cancelled.Outcome);
        Assert.Equal(OmsState.Cancelled, r.Order.State);
        Assert.Single(_channel.Cancelled);

        OrderSubmitResult again = await _gateway.CancelAsync(r.Order.ClientOrderId, "twice", CancellationToken.None);
        Assert.Equal(SubmitOutcome.Rejected, again.Outcome);
        Assert.Contains("is Cancelled", again.Message, StringComparison.Ordinal);
        OrderSubmitResult nobody = await _gateway.CancelAsync(Guid.NewGuid(), "no such", CancellationToken.None);
        Assert.Contains("no such order", nobody.Message, StringComparison.Ordinal);
        Assert.Single(_channel.Cancelled);
    }

    [Fact]
    public async Task ACancelWithAnUnknownOutcome_MarksTheOrderUnknown()
    {
        SubmitResult r = await Submit();
        _channel.OnCancel = _ => throw new HttpRequestException("reset");
        OrderSubmitResult c = await _gateway.CancelAsync(r.Order!.ClientOrderId, "test", CancellationToken.None);
        Assert.Equal(SubmitOutcome.Unknown, c.Outcome);
        Assert.Equal(OmsState.Unknown, r.Order.State);
    }

    [Fact]
    public async Task ARefusedCancel_LeavesTheOrderWorking()
    {
        SubmitResult r = await Submit();
        _channel.OnCancel = _ => OrderSubmitResult.Rejected("already filled");
        await _gateway.CancelAsync(r.Order!.ClientOrderId, "test", CancellationToken.None);
        Assert.Equal(OmsState.Working, r.Order.State);
    }

    [Fact]
    public async Task CancelAll_CancelsEveryWorkingOrder()
    {
        await Submit();
        await Submit(Intent(id: Test));
        Assert.Equal(2, _oms.Open.Count);
        Assert.Equal(0, await _gateway.CancelAllAsync("kill switch", CancellationToken.None));
        Assert.Equal(2, _channel.Cancelled.Count);
        Assert.All(_oms.All, o => Assert.Equal(OmsState.Cancelled, o.State));
    }

    [Fact]
    public async Task Submits_AreProcessedOneAtATime()
    {
        _channel.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SubmitResult> first = Submit();
        Task<SubmitResult> second = Submit(Intent(id: Test));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(_channel.Placed); // the second waits for the first to finish
        Assert.False(second.IsCompleted);

        _channel.Gate.SetResult();
        SubmitResult[] results = await Task.WhenAll(first, second);
        Assert.All(results, r => Assert.Equal(SubmitStatus.Accepted, r.Status));
        Assert.Equal(1, _channel.MaxConcurrent);
    }

    [Fact]
    public async Task RateAndDuplicateChecks_SeeWhatTheGatewaySent()
    {
        Assert.Equal(SubmitStatus.Accepted, (await Submit()).Status);

        _time.Advance(TimeSpan.FromSeconds(2));
        SubmitResult tooSoon = await Submit(Intent(qty: 5));
        Assert.Equal(["R12"], tooSoon.Risk!.Failures.Select(f => f.Id));

        _time.Advance(TimeSpan.FromSeconds(10));
        SubmitResult duplicate = await Submit();
        Assert.Equal(["R14"], duplicate.Risk!.Failures.Select(f => f.Id));
    }

    [Fact]
    public async Task OutsideTheTradingWindow_NothingIsSent()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 9, 28, 15, 25, 0, TimeSpan.Zero)); // 17:25 Stockholm
        SubmitResult r = await Submit();
        Assert.Equal(["R16"], r.Risk!.Failures.Select(f => f.Id));

        _time.SetUtcNow(new DateTimeOffset(2027, 1, 4, 9, 0, 0, TimeSpan.Zero)); // 2027 is not in this calendar
        SubmitResult noCalendar = await Submit(Intent(qty: 5));
        Assert.Contains("R16", noCalendar.Risk!.Failures.Select(f => f.Id));
        Assert.Empty(_channel.Placed);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromTheChannel()
    {
        var channel = new FakeSimulatedChannel();
        var gateway = NewGateway(channel, TradingMode.Paper);
        Assert.True(channel.HasSubscribers);
        gateway.Dispose();
        gateway.Dispose(); // idempotent
        Assert.False(channel.HasSubscribers);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => gateway.SubmitAsync(Intent(), CancellationToken.None));
    }
}
