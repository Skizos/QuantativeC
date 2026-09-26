using QuantAnalyst.Core;

namespace QuantAnalyst.Trading.Model;

/// <summary>Order types the pipeline knows. Only <see cref="Limit"/> passes R3 (ADR 0003: limit orders only).</summary>
public enum IntentOrderType
{
    Limit,
    Market,
}

/// <summary>
/// What a strategy (or a person) wants to trade, before normalisation, tick rounding and risk checks (ADR 0003 §2).
/// </summary>
/// <param name="OrderbookId">The instrument, by Avanza orderbook id (never by ticker).</param>
/// <param name="Ticker">For messages only.</param>
/// <param name="Quantity">Shares; the normaliser turns it into whole lots.</param>
/// <param name="LimitPrice">The unrounded limit; null means "no limit" (a market order), which R3 rejects.</param>
/// <param name="Reason">Why, in words (shown on the Confirm card and in the audit log).</param>
/// <param name="DecisionPrice">The price the decision was based on.</param>
/// <param name="StrategyId">Which strategy or command produced it.</param>
public sealed record OrderIntent(
    OrderbookId OrderbookId,
    string Ticker,
    OrderSide Side,
    long Quantity,
    decimal? LimitPrice,
    string Reason,
    decimal? DecisionPrice,
    DateTimeOffset DecisionTimeUtc,
    string StrategyId,
    IntentOrderType Type = IntentOrderType.Limit,
    string Condition = "NORMAL");

/// <summary>An intent after normalisation and tick rounding: whole lots and a valid limit. The risk engine judges this.</summary>
/// <param name="RoundedFrom">The limit before rounding (for the audit and the Confirm card).</param>
public sealed record PreparedOrder(OrderIntent Intent, long Volume, decimal? LimitPrice, decimal? RoundedFrom)
{
    public OrderbookId OrderbookId => Intent.OrderbookId;

    public OrderSide Side => Intent.Side;

    /// <summary>Gets volume × limit in the instrument currency (0 without a limit).</summary>
    public decimal Value => LimitPrice is { } p ? Volume * p : 0m;
}
