using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Halts;

namespace QuantAnalyst.Trading.Risk;

/// <summary>Execution mode (ADR 0003 §1), promoted strictly in this order.</summary>
public enum TradingMode
{
    Backtest,
    Paper,
    Confirm,
    Auto,
}

/// <summary>An order the OMS considers open (working, partially filled, sent or Unknown).</summary>
/// <param name="IsUnknown">True for an order whose submit outcome is unknown (R18 blocks its instrument).</param>
public sealed record OpenOrderView(Guid ClientOrderId, OrderbookId OrderbookId, OrderSide Side, long RemainingVolume, decimal LimitPrice, bool IsUnknown);

/// <summary>A recent intent for the duplicate check (R14).</summary>
public sealed record RecentIntent(OrderbookId OrderbookId, OrderSide Side, long Volume, decimal? LimitPrice, DateTimeOffset AtUtc);

/// <summary>Avanza's own <c>validate</c> answer (R21; live modes only).</summary>
public sealed record BrokerPreflight(bool AllValid, IReadOnlyList<string> Failures);

/// <summary>Whether the constants behind costs, calendar and tick sizes were checked by the owner (R20).</summary>
public sealed record VerifiedConstants(bool Courtage, bool Calendar, bool TickTable)
{
    public bool All => Courtage && Calendar && TickTable;
}

/// <summary>
/// Everything the risk engine needs to judge one order, captured at one instant so a check result can be reproduced from
/// the audit record. Money is SEK (the account currency); instrument prices are in the instrument currency.
/// </summary>
public sealed record RiskContext
{
    public required TradingMode Mode { get; init; }

    public required DateTimeOffset NowUtc { get; init; }

    /// <summary>Gets the account the order would be sent for (Paper: the paper book, id PAPER).</summary>
    public required AccountId Account { get; init; }

    /// <summary>Gets the full ids of the allowed accounts (R1). Never logged.</summary>
    public required IReadOnlySet<string> AllowedAccountIds { get; init; }

    public required Universe Universe { get; init; }

    /// <summary>Gets cash plus positions at their reference prices (R6–R8, R19).</summary>
    public required decimal AccountValue { get; init; }

    /// <summary>Gets the cash buys can use now (broker <c>availableForPurchase</c>, or paper cash minus working buys).</summary>
    public required decimal AvailableCash { get; init; }

    /// <summary>Gets shares held per instrument, settled plus pending (R4).</summary>
    public required IReadOnlyDictionary<OrderbookId, long> Positions { get; init; }

    /// <summary>Gets the value of each position at its reference price (R7, R8).</summary>
    public required IReadOnlyDictionary<OrderbookId, decimal> PositionValues { get; init; }

    public required IReadOnlyList<OpenOrderView> OpenOrders { get; init; }

    /// <summary>Gets the composed quote of the order's instrument, or null when there is none (R5, R15).</summary>
    public required Quote? Quote { get; init; }

    /// <summary>Gets orders placed today (Stockholm trading day, R10).</summary>
    public required int OrdersPlacedToday { get; init; }

    /// <summary>Gets the times of recent place/modify/cancel actions (R11).</summary>
    public required IReadOnlyList<DateTimeOffset> RecentActionsUtc { get; init; }

    /// <summary>Gets the time of the last place/modify/cancel on this instrument (R12).</summary>
    public required DateTimeOffset? LastActionOnInstrumentUtc { get; init; }

    public required IReadOnlyList<RecentIntent> RecentIntents { get; init; }

    /// <summary>Gets the calendar classification of today (Stockholm), or null when the calendar has no such year (R16).</summary>
    public required TradingDay? Today { get; init; }

    public required IReadOnlyList<HaltState> Halts { get; init; }

    /// <summary>Gets the account value at the start of the trading day (R19).</summary>
    public required decimal StartOfDayValue { get; init; }

    /// <summary>Gets courtage + FX fee for this order: the model's in Paper/Backtest, Avanza's preliminary fee live (R9).</summary>
    public required decimal EstimatedFees { get; init; }

    public required VerifiedConstants Verified { get; init; }

    /// <summary>Gets Avanza's validate answer, or null when it was not asked (Paper/Backtest) (R21).</summary>
    public BrokerPreflight? Preflight { get; init; }

    public bool IsLive => Mode is TradingMode.Confirm or TradingMode.Auto;
}
