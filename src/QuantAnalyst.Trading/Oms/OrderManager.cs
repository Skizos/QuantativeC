using QuantAnalyst.Core;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Oms;

/// <summary>Order states (ADR 0003 §6).</summary>
public enum OmsState
{
    New,
    Sent,
    Working,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected,

    /// <summary>The submit outcome is unknown; the instrument is blocked (R18) until reconciliation resolves it.</summary>
    Unknown,
}

/// <summary>One order as the OMS knows it.</summary>
public sealed class OmsOrder
{
    internal OmsOrder(Guid clientOrderId, AccountId account, OrderbookId orderbookId, string ticker, OrderSide side, long volume, decimal limitPrice, DateTimeOffset createdUtc)
    {
        ClientOrderId = clientOrderId;
        Account = account;
        OrderbookId = orderbookId;
        Ticker = ticker;
        Side = side;
        Volume = volume;
        LimitPrice = limitPrice;
        CreatedUtc = createdUtc;
        UpdatedUtc = createdUtc;
    }

    public Guid ClientOrderId { get; }

    public AccountId Account { get; }

    public OrderbookId OrderbookId { get; }

    public string Ticker { get; }

    public OrderSide Side { get; }

    public long Volume { get; internal set; }

    public decimal LimitPrice { get; internal set; }

    public OmsState State { get; internal set; } = OmsState.New;

    public OrderId? BrokerOrderId { get; internal set; }

    public long FilledVolume { get; internal set; }

    public decimal FilledValue { get; internal set; }

    public decimal Fees { get; internal set; }

    public string? Message { get; internal set; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset UpdatedUtc { get; internal set; }

    public long Remaining => Volume - FilledVolume;

    public decimal? AverageFillPrice => FilledVolume > 0 ? FilledValue / FilledVolume : null;

    /// <summary>Gets a value indicating whether the order may still trade (or might, when Unknown).</summary>
    public bool IsOpen => State is OmsState.New or OmsState.Sent or OmsState.Working or OmsState.PartiallyFilled or OmsState.Unknown;

    public bool IsTerminal => State is OmsState.Filled or OmsState.Cancelled or OmsState.Rejected;

    public OpenOrderView View() => new(ClientOrderId, OrderbookId, Side, Remaining, LimitPrice, State == OmsState.Unknown);
}

/// <summary>
/// The order state machine (ADR 0003 §6). Transitions are table-driven; an illegal one is a bug, so it raises the
/// <see cref="HaltReason.OmsInvariant"/> halt and throws. Every transition and fill is audited.
/// </summary>
public sealed class OrderManager(AuditLog audit, HaltController halts, TimeProvider time)
{
    private static readonly Dictionary<OmsState, OmsState[]> Allowed = new()
    {
        [OmsState.New] = [OmsState.Sent],
        [OmsState.Sent] = [OmsState.Working, OmsState.Rejected, OmsState.Unknown],
        [OmsState.Working] = [OmsState.PartiallyFilled, OmsState.Filled, OmsState.Cancelled, OmsState.Unknown],
        [OmsState.PartiallyFilled] = [OmsState.PartiallyFilled, OmsState.Filled, OmsState.Cancelled, OmsState.Unknown],

        // Reconciliation resolves Unknown to what the broker reports; "not placed" is Rejected.
        [OmsState.Unknown] = [OmsState.Working, OmsState.PartiallyFilled, OmsState.Filled, OmsState.Cancelled, OmsState.Rejected],
        [OmsState.Filled] = [],
        [OmsState.Cancelled] = [],
        [OmsState.Rejected] = [],
    };

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, OmsOrder> _orders = [];

    /// <summary>
    /// Raised after every change of an order (created, moved, filled), outside the book's lock, for observers such as
    /// the Windows app's charts. A handler's exception is swallowed: watching must never change the order flow.
    /// </summary>
    public event Action<OmsOrder>? Changed;

    public static bool IsAllowed(OmsState from, OmsState to) => Allowed[from].Contains(to);

    public IReadOnlyList<OmsOrder> All
    {
        get
        {
            lock (_lock)
            {
                return [.. _orders.Values.OrderBy(o => o.CreatedUtc)];
            }
        }
    }

    public IReadOnlyList<OmsOrder> Open => [.. All.Where(o => o.IsOpen)];

    public OmsOrder? Find(Guid clientOrderId)
    {
        lock (_lock)
        {
            return _orders.GetValueOrDefault(clientOrderId);
        }
    }

    public OmsOrder? FindByBrokerId(OrderId id)
    {
        lock (_lock)
        {
            return _orders.Values.FirstOrDefault(o => o.BrokerOrderId == id);
        }
    }

    /// <summary>Creates an order in state New. Its client id is audited before anything is sent.</summary>
    internal OmsOrder Create(Guid clientOrderId, AccountId account, OrderbookId orderbookId, string ticker, OrderSide side, long volume, decimal limitPrice)
    {
        var order = new OmsOrder(clientOrderId, account, orderbookId, ticker, side, volume, limitPrice, time.GetUtcNow());
        lock (_lock)
        {
            if (!_orders.TryAdd(clientOrderId, order))
            {
                throw Invariant($"client order id {clientOrderId} is used twice");
            }
        }

        audit.Append("oms-new", new { clientOrderId, orderbookId = orderbookId.Value, ticker, side = side.ToString(), volume, limitPrice });
        Notify(order);
        return order;
    }

    /// <summary>Moves an order to <paramref name="to"/>; an illegal transition halts trading and throws.</summary>
    public void Transition(Guid clientOrderId, OmsState to, string why, OrderId? brokerOrderId = null)
    {
        OmsOrder order = Get(clientOrderId);
        OmsState from;
        lock (_lock)
        {
            from = order.State;
            if (!IsAllowed(from, to))
            {
                throw Invariant($"illegal transition {from} -> {to} for {clientOrderId} ({why})");
            }

            order.State = to;
            order.Message = why;
            order.BrokerOrderId ??= brokerOrderId;
            order.UpdatedUtc = time.GetUtcNow();
        }

        audit.Append("oms-state", new { clientOrderId, from = from.ToString(), to = to.ToString(), why, brokerOrderId = brokerOrderId?.Value });
        Notify(order);
    }

    /// <summary>
    /// Moves an order to <paramref name="to"/> only if it is in one of <paramref name="from"/>, atomically. Returns false
    /// (and changes nothing) otherwise, for paths that race with fills (a reply and a fill arriving together).
    /// </summary>
    public bool TryTransition(Guid clientOrderId, OmsState to, string why, OrderId? brokerOrderId, params OmsState[] from)
    {
        OmsOrder order = Get(clientOrderId);
        OmsState was;
        lock (_lock)
        {
            was = order.State;
            if (!from.Contains(was))
            {
                return false;
            }

            if (!IsAllowed(was, to))
            {
                throw Invariant($"illegal transition {was} -> {to} for {clientOrderId} ({why})");
            }

            order.State = to;
            order.Message = why;
            order.BrokerOrderId ??= brokerOrderId;
            order.UpdatedUtc = time.GetUtcNow();
        }

        audit.Append("oms-state", new { clientOrderId, from = was.ToString(), to = to.ToString(), why, brokerOrderId = brokerOrderId?.Value });
        Notify(order);
        return true;
    }

    /// <summary>Applies a (partial) fill. Overfilling or filling a closed order halts trading.</summary>
    public void ApplyFill(Guid clientOrderId, long volume, decimal price, decimal fees, string source) =>
        ApplyFillValue(clientOrderId, volume, volume * price, fees, source);

    /// <summary>
    /// Applies a fill given its total value (several deals at different prices, as reconciliation finds them), so the
    /// order's filled value stays exact instead of going through a rounded average price.
    /// </summary>
    public void ApplyFillValue(Guid clientOrderId, long volume, decimal value, decimal fees, string source)
    {
        decimal price = volume > 0 ? value / volume : 0m;
        OmsOrder order = Get(clientOrderId);
        OmsState from, to;
        lock (_lock)
        {
            from = order.State;
            if (volume <= 0 || price <= 0)
            {
                throw Invariant($"fill of {volume} @ {price} for {clientOrderId} is not positive");
            }

            if (order.FilledVolume + volume > order.Volume)
            {
                throw Invariant($"fill of {volume} would overfill {clientOrderId} ({order.FilledVolume}/{order.Volume})");
            }

            to = order.FilledVolume + volume == order.Volume ? OmsState.Filled : OmsState.PartiallyFilled;
            if (!IsAllowed(from, to))
            {
                throw Invariant($"fill in state {from} for {clientOrderId}");
            }

            order.FilledVolume += volume;
            order.FilledValue += value;
            order.Fees += fees;
            order.State = to;
            order.UpdatedUtc = time.GetUtcNow();
        }

        audit.Append("oms-fill", new { clientOrderId, volume, price = decimal.Round(price, 6), value, fees, source, from = from.ToString(), to = to.ToString(), filled = order.FilledVolume, of = order.Volume });
        Notify(order);
    }

    private void Notify(OmsOrder order)
    {
        try
        {
            Changed?.Invoke(order);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An observer's failure is its own; the order flow goes on exactly as it would without one.
        }
    }

    private OmsOrder Get(Guid clientOrderId) =>
        Find(clientOrderId) ?? throw Invariant($"unknown client order id {clientOrderId}");

    private InvalidOperationException Invariant(string message)
    {
        halts.Raise(HaltReason.OmsInvariant, message);
        return new InvalidOperationException($"OMS invariant broken: {message}. Trading is halted.");
    }
}
