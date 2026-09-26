namespace QuantAnalyst.Core.Broker;

/// <summary>
/// The order port (ADR 0002: reads and orders are separate ports). Implementations: Paper and Backtest (simulated) and
/// Avanza (fixture-tested only until Phase 7). Only <c>QuantAnalyst.Trading.OrderGateway</c> may call it; an
/// architecture test scans the IL of every QuantAnalyst assembly for other callers.
/// </summary>
public interface IBrokerOrderChannel
{
    /// <summary>Gets a short name for audit records, e.g. "paper" or "avanza".</summary>
    string Name { get; }

    /// <summary>Places a day limit order. Never retried by the caller: a timeout or unreadable answer is Unknown.</summary>
    Task<OrderSubmitResult> PlaceAsync(ApprovedOrder order, CancellationToken ct);

    /// <summary>Changes price and/or volume of a working order.</summary>
    Task<OrderSubmitResult> ModifyAsync(ApprovedModify modify, CancellationToken ct);

    /// <summary>Cancels a working order.</summary>
    Task<OrderSubmitResult> CancelAsync(ApprovedCancel cancel, CancellationToken ct);
}

/// <summary>What the broker said about a place, modify or cancel request.</summary>
public enum SubmitOutcome
{
    /// <summary>The broker accepted the request (Avanza <c>orderRequestStatus: SUCCESS</c>).</summary>
    Accepted,

    /// <summary>The broker refused it (Avanza <c>orderRequestStatus: ERROR</c>); the message says why.</summary>
    Rejected,

    /// <summary>
    /// We do not know: timeout, transport error, 5xx, 404 or an unreadable answer. The order may or may not exist, so it
    /// is never retried and blocks its instrument until reconciliation resolves it (ADR 0003 §6).
    /// </summary>
    Unknown,
}

/// <param name="BrokerOrderId">The broker's order id when accepted (and sometimes when rejected).</param>
/// <param name="Message">The broker's message or our reason for Unknown. Contains no secrets.</param>
public sealed record OrderSubmitResult(SubmitOutcome Outcome, OrderId? BrokerOrderId, string Message)
{
    public static OrderSubmitResult Accepted(OrderId id, string message = "") => new(SubmitOutcome.Accepted, id, message);

    public static OrderSubmitResult Rejected(string message, OrderId? id = null) => new(SubmitOutcome.Rejected, id, message);

    public static OrderSubmitResult Unknown(string reason) => new(SubmitOutcome.Unknown, null, reason);
}

/// <summary>
/// A limit order that passed every pre-trade check and the mode gate. The constructor is internal: only
/// <c>QuantAnalyst.Trading</c> (and within it only <c>OrderGateway</c>, checked by an architecture test) creates one.
/// </summary>
public sealed record ApprovedOrder
{
    internal ApprovedOrder(Guid clientOrderId, AccountId account, OrderbookId orderbookId, OrderSide side, long volume, decimal limitPrice, DateOnly validUntil)
    {
        if (volume <= 0 || limitPrice <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "An approved order needs a positive volume and limit price.");
        }

        ClientOrderId = clientOrderId;
        Account = account;
        OrderbookId = orderbookId;
        Side = side;
        Volume = volume;
        LimitPrice = limitPrice;
        ValidUntil = validUntil;
    }

    /// <summary>Gets our key for the order (UUIDv7), persisted before the request is sent.</summary>
    public Guid ClientOrderId { get; }

    public AccountId Account { get; }

    public OrderbookId OrderbookId { get; }

    public OrderSide Side { get; }

    public long Volume { get; }

    /// <summary>Gets the limit, already rounded to the instrument's tick (buy down, sell up).</summary>
    public decimal LimitPrice { get; }

    /// <summary>Gets the last day the order is valid (a day order: the trading day it is placed).</summary>
    public DateOnly ValidUntil { get; }
}

/// <summary>An approved change to a working order (price and/or volume). Internal constructor, as <see cref="ApprovedOrder"/>.</summary>
public sealed record ApprovedModify
{
    internal ApprovedModify(Guid clientOrderId, AccountId account, OrderId brokerOrderId, decimal limitPrice, long volume, DateOnly validUntil)
    {
        ClientOrderId = clientOrderId;
        Account = account;
        BrokerOrderId = brokerOrderId;
        LimitPrice = limitPrice;
        Volume = volume;
        ValidUntil = validUntil;
    }

    public Guid ClientOrderId { get; }

    public AccountId Account { get; }

    public OrderId BrokerOrderId { get; }

    public decimal LimitPrice { get; }

    public long Volume { get; }

    public DateOnly ValidUntil { get; }
}

/// <summary>An approved cancel of a working order. Internal constructor, as <see cref="ApprovedOrder"/>.</summary>
public sealed record ApprovedCancel
{
    internal ApprovedCancel(Guid clientOrderId, AccountId account, OrderId brokerOrderId)
    {
        ClientOrderId = clientOrderId;
        Account = account;
        BrokerOrderId = brokerOrderId;
    }

    public Guid ClientOrderId { get; }

    public AccountId Account { get; }

    public OrderId BrokerOrderId { get; }
}
