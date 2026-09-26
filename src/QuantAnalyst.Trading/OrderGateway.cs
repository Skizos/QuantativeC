using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Pipeline;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading;

/// <summary>How far an intent got.</summary>
public enum SubmitStatus
{
    /// <summary>Not an order: unknown instrument, less than a lot, no valid tick, wrong currency.</summary>
    NotPrepared,

    /// <summary>At least one risk check failed; nothing was sent.</summary>
    RiskRejected,

    /// <summary>Sent; the broker accepted it (Working).</summary>
    Accepted,

    /// <summary>Sent; the broker refused it.</summary>
    BrokerRejected,

    /// <summary>Sent; the outcome is unknown. Never retried; the instrument is blocked until reconciled.</summary>
    Unknown,
}

public sealed record SubmitResult(SubmitStatus Status, OrderIntent Intent, PreparedOrder? Prepared, RiskReport? Risk, OmsOrder? Order, string Message);

/// <summary>Everything the gateway reads to build a risk context. The CLI composes it for Paper (Phase 6) or live (Phase 7).</summary>
public sealed record GatewayEnvironment
{
    public required TradingMode Mode { get; init; }

    public required IInstrumentCatalog Instruments { get; init; }

    public required IQuoteSource Quotes { get; init; }

    public required IAccountState Account { get; init; }

    public required MarketCalendar Calendar { get; init; }

    public required Universe Universe { get; init; }

    /// <summary>Gets the full ids of the allowed accounts (R1). Never logged.</summary>
    public required IReadOnlySet<string> AllowedAccountIds { get; init; }

    /// <summary>Gets courtage + FX fee for an order (the model's in simulated modes).</summary>
    public required Func<PreparedOrder, InstrumentSpec, decimal> Fees { get; init; }

    public required bool CourtageVerified { get; init; }
}

/// <summary>
/// The only way to trade (ADR 0002/0003): runs preparation (lots, tick), the risk engine (R1–R21), the mode gate, the
/// OMS and the order channel, auditing every step. It is the only creator of <see cref="ApprovedOrder"/> and the only
/// caller of <see cref="IBrokerOrderChannel"/> (an IL-scanning architecture test checks both). In Phase 6 it accepts
/// only simulated channels in Backtest or Paper mode.
/// </summary>
public sealed class OrderGateway : IDisposable
{
    private readonly ISimulatedOrderChannel _channel;
    private readonly GatewayEnvironment _env;
    private readonly PreTradeRiskEngine _risk;
    private readonly OrderManager _oms;
    private readonly HaltController _halts;
    private readonly AuditLog _audit;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Lock _lock = new();
    private readonly List<DateTimeOffset> _actions = [];
    private readonly Dictionary<OrderbookId, DateTimeOffset> _lastAction = [];
    private readonly List<RecentIntent> _recentIntents = [];
    private DateOnly _placementsDay;
    private int _placementsToday;
    private int _consecutiveBrokerRejects;
    private bool _disposed;

    public OrderGateway(IBrokerOrderChannel channel, GatewayEnvironment env, PreTradeRiskEngine risk, OrderManager oms, HaltController halts, AuditLog audit, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(env);
        if (env.Mode is TradingMode.Confirm or TradingMode.Auto)
        {
            throw new Modes.ModeNotAllowedException($"Mode {env.Mode} is not available in Phase 6: nothing is sent to a broker yet.");
        }

        if (channel is not ISimulatedOrderChannel simulated)
        {
            throw new Modes.ModeNotAllowedException($"The '{channel.Name}' channel is not simulated; {env.Mode} mode accepts only the Paper or Backtest channel.");
        }

        _channel = simulated;
        _env = env;
        _risk = risk ?? throw new ArgumentNullException(nameof(risk));
        _oms = oms ?? throw new ArgumentNullException(nameof(oms));
        _halts = halts ?? throw new ArgumentNullException(nameof(halts));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        simulated.Filled += OnFill;
        simulated.Ended += OnEnded;
        audit.Append("gateway-start", new { mode = env.Mode.ToString(), channel = channel.Name, universe = env.Universe.Entries.Count });
    }

    /// <summary>Raised on every broker rejection with the number of consecutive ones (the kill switch fires at 3).</summary>
    public event Action<int, string>? BrokerRejected;

    /// <summary>Raised when a submit or cancel outcome is unknown.</summary>
    public event Action<OmsOrder>? OutcomeUnknown;

    public TradingMode Mode => _env.Mode;

    public OrderManager Oms => _oms;

    /// <summary>Runs one intent through the whole pipeline. Orders are processed one at a time.</summary>
    public async Task<SubmitResult> SubmitAsync(OrderIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SubmitCoreAsync(intent, ct).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>
    /// Cancels one working order. Cancels reduce risk, so they skip the pre-trade checks (and are allowed while halted),
    /// but they are audited and counted as order actions.
    /// </summary>
    public async Task<OrderSubmitResult> CancelAsync(Guid clientOrderId, string reason, CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CancelCoreAsync(clientOrderId, reason, ct).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>Cancels every open order that has a broker id (the kill switch path). Returns how many are still open.</summary>
    public async Task<int> CancelAllAsync(string reason, CancellationToken ct)
    {
        foreach (OmsOrder order in _oms.Open.Where(o => o.State is OmsState.Working or OmsState.PartiallyFilled))
        {
            await CancelAsync(order.ClientOrderId, reason, ct).ConfigureAwait(false);
        }

        return _oms.Open.Count;
    }

    private async Task<SubmitResult> SubmitCoreAsync(OrderIntent intent, CancellationToken ct)
    {
        _audit.Append("intent", new
        {
            orderbookId = intent.OrderbookId.Value,
            intent.Ticker,
            side = intent.Side.ToString(),
            intent.Quantity,
            intent.LimitPrice,
            type = intent.Type.ToString(),
            intent.Condition,
            intent.Reason,
            intent.DecisionPrice,
            intent.DecisionTimeUtc,
            intent.StrategyId,
        });

        InstrumentSpec? spec = _env.Instruments.Find(intent.OrderbookId);
        PreparedOrder prepared;
        try
        {
            prepared = spec is null
                ? throw new OrderPreparationException($"orderbook {intent.OrderbookId} ({intent.Ticker}) has no instrument data (tick table, lot size)")
                : OrderPreparation.Prepare(intent, spec);
        }
        catch (OrderPreparationException ex)
        {
            _audit.Append("not-prepared", new { orderbookId = intent.OrderbookId.Value, reason = ex.Message });
            return new SubmitResult(SubmitStatus.NotPrepared, intent, null, null, null, ex.Message);
        }

        _audit.Append("prepared", new { prepared.Volume, prepared.LimitPrice, rounding = OrderPreparation.RoundingNote(prepared) });
        RiskContext ctx = await BuildContextAsync(prepared, spec, ct).ConfigureAwait(false);
        RiskReport report = _risk.Evaluate(prepared, ctx);
        _audit.Append("risk", new
        {
            passed = report.Passed,
            checks = report.Checks.Select(c => new { c.Id, c.Name, c.Passed, c.Observed, c.Limit, message = c.Passed ? null : c.Message }),
        });
        if (!report.Passed)
        {
            string why = string.Join("; ", report.Failures.Select(f => $"{f.Id} {f.Message}"));
            return new SubmitResult(SubmitStatus.RiskRejected, intent, prepared, report, null, why);
        }

        // Mode gate: Backtest and Paper pass to the simulated channel (the constructor refused anything else).
        _audit.Append("gate", new { mode = _env.Mode.ToString(), channel = _channel.Name, decision = "simulate" });

        DateTimeOffset now = _time.GetUtcNow();
        DateOnly today = StockholmDate(now);
        Guid clientOrderId = Guid.CreateVersion7(now);
        OmsOrder order = _oms.Create(clientOrderId, ctx.Account, prepared.OrderbookId, intent.Ticker, prepared.Side, prepared.Volume, prepared.LimitPrice!.Value);
        var approved = new ApprovedOrder(clientOrderId, ctx.Account, prepared.OrderbookId, prepared.Side, prepared.Volume, prepared.LimitPrice.Value, today);
        _audit.Append("submit", new { clientOrderId, channel = _channel.Name, account = ctx.Account.Masked, validUntil = today });
        _oms.Transition(clientOrderId, OmsState.Sent, "sent to " + _channel.Name);
        RecordAction(prepared, now, placement: true);

        OrderSubmitResult result;
        try
        {
            result = await _channel.PlaceAsync(approved, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever happened, the order may exist: Unknown, never retried (ADR 0003 §6).
            result = OrderSubmitResult.Unknown($"{ex.GetType().Name}: {ex.Message}");
        }

        return Complete(intent, prepared, report, order, result);
    }

    private SubmitResult Complete(OrderIntent intent, PreparedOrder prepared, RiskReport report, OmsOrder order, OrderSubmitResult result)
    {
        _audit.Append("submit-result", new { order.ClientOrderId, outcome = result.Outcome.ToString(), brokerOrderId = result.BrokerOrderId?.Value, result.Message, fault = result.Fault.ToString() });
        RaiseFault(result, "place");
        switch (result.Outcome)
        {
            case SubmitOutcome.Accepted when result.BrokerOrderId is { } id:
                _consecutiveBrokerRejects = 0;

                // A fill may already have moved it on (see OnFill).
                _oms.TryTransition(order.ClientOrderId, OmsState.Working, "accepted", id, OmsState.Sent);

                return new SubmitResult(SubmitStatus.Accepted, intent, prepared, report, order, result.Message);

            case SubmitOutcome.Rejected:
                _oms.Transition(order.ClientOrderId, OmsState.Rejected, "broker: " + result.Message);
                int streak = ++_consecutiveBrokerRejects;
                BrokerRejected?.Invoke(streak, result.Message);
                return new SubmitResult(SubmitStatus.BrokerRejected, intent, prepared, report, order, result.Message);

            case SubmitOutcome.Unknown when order.State != OmsState.Sent:
                // Fills arrived before the submit returned, so the order exists; the fills are the better evidence.
                _audit.Append("submit-outcome-superseded", new { order.ClientOrderId, state = order.State.ToString(), result.Message });
                return new SubmitResult(SubmitStatus.Accepted, intent, prepared, report, order, "filled; submit reply lost: " + result.Message);

            default:
                // Unknown, or "accepted" without an order id (unusable: we could never cancel it).
                string why = result.Outcome == SubmitOutcome.Accepted ? "accepted without an order id" : result.Message;
                _oms.Transition(order.ClientOrderId, OmsState.Unknown, why);
                OutcomeUnknown?.Invoke(order);
                return new SubmitResult(SubmitStatus.Unknown, intent, prepared, report, order, why);
        }
    }

    private async Task<OrderSubmitResult> CancelCoreAsync(Guid clientOrderId, string reason, CancellationToken ct)
    {
        OmsOrder? order = _oms.Find(clientOrderId);
        if (order is not { State: OmsState.Working or OmsState.PartiallyFilled, BrokerOrderId: { } brokerId })
        {
            string why = order is null ? "no such order" : $"order is {order.State}{(order.BrokerOrderId is null ? " without a broker id" : string.Empty)}";
            _audit.Append("cancel-skipped", new { clientOrderId, why });
            return OrderSubmitResult.Rejected("not cancellable: " + why);
        }

        var cancel = new ApprovedCancel(clientOrderId, order.Account, brokerId);
        _audit.Append("cancel", new { clientOrderId, brokerOrderId = brokerId.Value, reason });
        RecordAction(order.OrderbookId, _time.GetUtcNow());
        OrderSubmitResult result;
        try
        {
            result = await _channel.CancelAsync(cancel, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = OrderSubmitResult.Unknown($"{ex.GetType().Name}: {ex.Message}");
        }

        _audit.Append("cancel-result", new { clientOrderId, outcome = result.Outcome.ToString(), result.Message, fault = result.Fault.ToString() });
        RaiseFault(result, "cancel");
        switch (result.Outcome)
        {
            case SubmitOutcome.Accepted:
                // A fill that raced the cancel may have completed it; then it stays Filled.
                _oms.TryTransition(clientOrderId, OmsState.Cancelled, "cancelled: " + reason, null, OmsState.Working, OmsState.PartiallyFilled);
                break;
            case SubmitOutcome.Unknown:
                if (_oms.TryTransition(clientOrderId, OmsState.Unknown, "cancel outcome unknown: " + result.Message, null, OmsState.Working, OmsState.PartiallyFilled))
                {
                    OutcomeUnknown?.Invoke(order);
                }

                break;
            default:
                // Refused (e.g. already filled): the order stays as it is until the next fill or reconciliation.
                break;
        }

        return result;
    }

    // Drift and a gone endpoint fire the kill switch (it listens for these halts); an expired session halts the order flow.
    private void RaiseFault(OrderSubmitResult result, string action)
    {
        HaltReason? reason = result.Fault switch
        {
            BrokerFault.SchemaDrift => HaltReason.SchemaDrift,
            BrokerFault.EndpointGone => HaltReason.EndpointGone,
            BrokerFault.SessionExpired => HaltReason.Session,
            _ => null,
        };
        if (reason is { } r)
        {
            _halts.Raise(r, $"{_channel.Name} {action}: {result.Message}");
        }
    }

    private async Task<RiskContext> BuildContextAsync(PreparedOrder order, InstrumentSpec spec, CancellationToken ct)
    {
        AccountSnapshot account = await _env.Account.GetAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();
        DateOnly today = StockholmDate(now);
        TradingDay? day = null;
        bool calendarVerified = false;
        try
        {
            day = _env.Calendar.Classify(today);
            calendarVerified = _env.Calendar.GetYear(today.Year).VerifiedOn is not null;
        }
        catch (ArgumentOutOfRangeException)
        {
            // Year not in the calendar: R16 fails with "date not in the calendar".
        }

        lock (_lock)
        {
            if (_placementsDay != today)
            {
                _placementsDay = today;
                _placementsToday = 0;
            }

            _actions.RemoveAll(t => now - t > TimeSpan.FromMinutes(5));
            _recentIntents.RemoveAll(i => now - i.AtUtc > TimeSpan.FromMinutes(10));
            return new RiskContext
            {
                Mode = _env.Mode,
                NowUtc = now,
                Account = account.Account,
                AllowedAccountIds = _env.AllowedAccountIds,
                Universe = _env.Universe,
                AccountValue = account.AccountValue,
                AvailableCash = account.AvailableCash,
                Positions = account.Positions,
                PositionValues = account.PositionValues,
                OpenOrders = [.. _oms.Open.Select(o => o.View())],
                Quote = _env.Quotes.Latest(order.OrderbookId),
                OrdersPlacedToday = _placementsToday,
                RecentActionsUtc = [.. _actions],
                LastActionOnInstrumentUtc = _lastAction.TryGetValue(order.OrderbookId, out DateTimeOffset last) ? last : null,
                RecentIntents = [.. _recentIntents],
                Today = day,
                Halts = _halts.Active,
                StartOfDayValue = account.StartOfDayValue,
                EstimatedFees = _env.Fees(order, spec),
                Verified = new VerifiedConstants(_env.CourtageVerified, calendarVerified, spec.TickTableVerified),
                Preflight = null,
            };
        }
    }

    private void RecordAction(PreparedOrder order, DateTimeOffset now, bool placement)
    {
        RecordAction(order.OrderbookId, now);
        lock (_lock)
        {
            if (placement)
            {
                _placementsToday++;
            }

            // R14 compares with intents that were sent, so a rejected intent does not block its own valid retry.
            _recentIntents.Add(new RecentIntent(order.OrderbookId, order.Side, order.Volume, order.LimitPrice, now));
        }
    }

    private void RecordAction(OrderbookId id, DateTimeOffset now)
    {
        lock (_lock)
        {
            _actions.Add(now);
            _lastAction[id] = now;
        }
    }

    private void OnFill(SimulatedFill fill)
    {
        OmsOrder? order = _oms.Find(fill.ClientOrderId);
        if (order is null)
        {
            _halts.Raise(HaltReason.Reconciliation, $"fill for unknown client order {fill.ClientOrderId}");
            return;
        }

        try
        {
            // A fill can arrive before PlaceAsync returns (a marketable order); it proves the order was accepted.
            _oms.TryTransition(order.ClientOrderId, OmsState.Working, "fill arrived before the submit reply", fill.BrokerOrderId, OmsState.Sent);

            _oms.ApplyFill(fill.ClientOrderId, fill.Volume, fill.Price, fill.Courtage + fill.FxFee, fill.How.Length == 0 ? _channel.Name : $"{_channel.Name}: {fill.How}");
        }
        catch (InvalidOperationException ex)
        {
            // The OMS has raised the OmsInvariant halt and audited it; the channel's thread must not die for it.
            _audit.Append("fill-refused", new { fill.ClientOrderId, fill.Volume, fill.Price, reason = ex.Message });
        }
    }

    private void OnEnded(OrderId brokerOrderId, string reason)
    {
        OmsOrder? order = _oms.FindByBrokerId(brokerOrderId);
        if (order is not null)
        {
            _oms.TryTransition(order.ClientOrderId, OmsState.Cancelled, reason, brokerOrderId, OmsState.Working, OmsState.PartiallyFilled);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Filled -= OnFill;
        _channel.Ended -= OnEnded;
        _serial.Dispose();
    }

    /// <summary>The Stockholm calendar date of a UTC instant (day orders, daily counters, audit files).</summary>
    public static DateOnly StockholmDate(DateTimeOffset utc) => DateOnly.FromDateTime(MarketTime.ToStockholm(utc).DateTime);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"OrderGateway({_env.Mode}, {_channel.Name})");
}
