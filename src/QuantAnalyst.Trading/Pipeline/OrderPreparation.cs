using QuantAnalyst.Core;
using QuantAnalyst.Trading.Model;

namespace QuantAnalyst.Trading.Pipeline;

/// <summary>Why an intent never reached the risk engine.</summary>
public sealed class OrderPreparationException(string message) : Exception(message);

/// <summary>
/// First two pipeline steps (ADR 0003 §2): whole lots, then the limit rounded to the instrument's tick, the passive
/// way (buy down, sell up), before any risk check (CLAUDE.md "rounded to the valid tick size BEFORE risk checks").
/// </summary>
public static class OrderPreparation
{
    /// <summary>The account's currency: every risk limit is in SEK (a USD or CAD share's order is converted, ADR 0005).</summary>
    public const string Currency = "SEK";

    public static PreparedOrder Prepare(OrderIntent intent, InstrumentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(spec);
        if (intent.OrderbookId != spec.OrderbookId)
        {
            throw new ArgumentException("The instrument spec does not belong to the intent.", nameof(spec));
        }

        long volume = Normalize(intent.Quantity, spec.LotSize);
        if (volume <= 0)
        {
            throw new OrderPreparationException($"{intent.Quantity} shares is less than one lot of {spec.LotSize} for {spec.Ticker}.");
        }

        decimal? rounded = null;
        if (intent.LimitPrice is { } limit)
        {
            if (limit <= 0)
            {
                throw new OrderPreparationException($"Limit {limit} must be positive.");
            }

            try
            {
                rounded = spec.TickSizes.RoundForOrder(limit, intent.Side);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new OrderPreparationException($"{spec.Ticker}: no valid tick for limit {limit} ({ex.Message.Split(Environment.NewLine)[0]}).");
            }
        }

        return new PreparedOrder(intent, volume, rounded, intent.LimitPrice);
    }

    /// <summary>Largest whole number of lots not above the requested quantity (never rounds up).</summary>
    public static long Normalize(long quantity, long lotSize)
    {
        if (lotSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lotSize), lotSize, "Lot size must be >= 1.");
        }

        return quantity <= 0 ? 0 : quantity / lotSize * lotSize;
    }

    /// <summary>The side a rounding went (for the audit and the Confirm card).</summary>
    public static string RoundingNote(PreparedOrder order) =>
        order.RoundedFrom is { } from && order.LimitPrice is { } to && from != to
            ? $"{from} rounded {(order.Side == OrderSide.Buy ? "down" : "up")} to {to}"
            : "on tick";
}
