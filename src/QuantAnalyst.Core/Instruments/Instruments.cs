namespace QuantAnalyst.Core.Instruments;

/// <summary>
/// Order-entry parameters of one orderbook, as known at <see cref="KnownAtUtc"/> (Avanza <c>orderbook/{id}</c>).
/// The tick table here is authoritative for rounding; RTS 11 is only a cross-check (avanza-endpoints.md §7).
/// </summary>
public sealed record InstrumentTradingParams(
    OrderbookId OrderbookId,
    string Name,
    string? TickerSymbol,
    string Isin,
    string Currency,
    string MarketPlace,
    string CountryCode,
    string InstrumentType,
    string? OrderbookStatus,
    TickSizeTable TickSizes,
    int VolumeFactor,
    int TradingUnit,
    DateOnly? MinValidUntil,
    DateOnly? MaxValidUntil,
    DateTimeOffset KnownAtUtc);

/// <summary>One instrument search result.</summary>
public sealed record InstrumentSearchHit(
    OrderbookId OrderbookId,
    string Name,
    string Type,
    string MarketPlaceName,
    bool Tradeable,
    decimal? LastPrice,
    string? Currency);
