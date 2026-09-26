namespace QuantAnalyst.Core.Accounts;

/// <summary>An account from the overview. Amounts are in <see cref="Currency"/> (SEK for Swedish accounts).</summary>
public sealed record Account(
    AccountId Id,
    string Type,
    string Name,
    string Status,
    string Currency,
    decimal Balance,
    decimal TotalValue,
    decimal BuyingPower);

/// <summary>
/// A tradable account with what is available for purchase (the cash check input). <see cref="IsDiscretionary"/>
/// is null when the broker didn't say; the order path (Phase 6) treats null like true and refuses to trade.
/// </summary>
public sealed record TradingAccount(
    AccountId Id,
    string Name,
    string AccountType,
    decimal AvailableForPurchase,
    bool IsTradable,
    bool HasCredit,
    bool? IsDiscretionary,
    IReadOnlyList<CurrencyBalance> CurrencyBalances);

public sealed record CurrencyBalance(string Currency, decimal Balance);

/// <summary>An instrument holding. <see cref="Value"/> and <see cref="AcquiredValue"/> are in the account currency.</summary>
public sealed record Position(
    AccountId Account,
    OrderbookId? OrderbookId,
    string InstrumentName,
    string? Isin,
    string InstrumentType,
    string Currency,
    decimal Volume,
    decimal Value,
    decimal? AverageAcquiredPrice,
    decimal? AcquiredValue,
    decimal? LastPrice);

public sealed record CashPosition(AccountId Account, decimal Balance, string Currency);

public sealed record PortfolioSnapshot(
    IReadOnlyList<Position> Positions,
    IReadOnlyList<CashPosition> Cash,
    DateTimeOffset RetrievedAtUtc);

/// <summary>A booked transaction (trade, dividend, fee, deposit …) as reported by the broker. Read-only.</summary>
public sealed record BrokerTransaction(
    string Id,
    DateOnly Date,
    AccountId Account,
    string Type,
    string Description,
    OrderbookId? OrderbookId,
    string? Isin,
    decimal? Volume,
    decimal? Price,
    decimal? Amount,
    decimal? Commission,
    string? Currency,
    bool Cancelled);
