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
    string? Currency)
{
    /// <summary>Gets the ticker when the broker shows one, e.g. "ERIC B" (from "Ericsson B (ERIC B)").</summary>
    public string? Ticker { get; init; }

    /// <summary>Gets the listing's country, e.g. "SE" or "FI".</summary>
    public string? FlagCode { get; init; }

    /// <summary>Gets today's change in percent, e.g. 0.66 for +0.66 %.</summary>
    public decimal? TodayChangePercent { get; init; }

    /// <summary>Gets the top-level sector in English, e.g. "Technology".</summary>
    public string? Sector { get; init; }
}
