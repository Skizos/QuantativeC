namespace QuantAnalyst.Core.Orders;

/// <summary>A working order as reported by the broker. <see cref="State"/> is the broker's raw state name.</summary>
public sealed record BrokerOrder(
    OrderId Id,
    AccountId Account,
    OrderbookId OrderbookId,
    string InstrumentName,
    OrderSide Side,
    decimal Price,
    decimal Volume,
    decimal OriginalVolume,
    string State,
    string Condition,
    DateTimeOffset? CreatedUtc,
    DateOnly? ValidUntil,
    bool Modifiable,
    bool Deletable);

/// <summary>A fill (deal) as reported by the broker; used for reconciliation (Phase 6).</summary>
public sealed record BrokerDeal(
    string DealId,
    OrderId OrderId,
    AccountId Account,
    OrderbookId OrderbookId,
    OrderSide Side,
    decimal Price,
    decimal Volume,
    DateTimeOffset TimeUtc);
