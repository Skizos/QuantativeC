namespace QuantAnalyst.Core.Market;

/// <summary>One order-depth level. An empty side has a null price and zero volume.</summary>
public sealed record DepthLevel(decimal? BidPrice, decimal BidVolume, decimal? AskPrice, decimal AskVolume);

/// <summary>
/// A polled market-data snapshot for one orderbook (Avanza <c>marketdata/{id}</c>). Missing values are null,
/// never zero. Timestamps are UTC.
/// </summary>
public sealed record MarketSnapshot(
    OrderbookId OrderbookId,
    decimal? Bid,
    decimal? Ask,
    decimal? Last,
    decimal? High,
    decimal? Low,
    decimal? Change,
    decimal? ChangePercent,
    decimal? VolumeWeightedAveragePrice,
    decimal TotalVolumeTraded,
    decimal TotalValueTraded,
    DateTimeOffset? TimeOfLastUtc,
    DateTimeOffset? UpdatedUtc,
    IReadOnlyList<DepthLevel> Depth,
    DateTimeOffset? DepthReceivedUtc,
    DateTimeOffset RetrievedAtUtc);

/// <summary>One OHLCV bar; <see cref="TimestampUtc"/> is the bar start.</summary>
public readonly record struct Bar(DateTimeOffset TimestampUtc, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

/// <summary>Chart look-back periods common to the reference clients (avanza-endpoints.md §3).</summary>
public enum ChartPeriod
{
    Today,
    OneWeek,
    OneMonth,
    ThreeMonths,
    ThisYear,
    OneYear,
    ThreeYears,
    FiveYears,
}

public enum ChartResolution
{
    Minute,
    TwoMinutes,
    FiveMinutes,
    TenMinutes,
    ThirtyMinutes,
    Hour,
    Day,
    Week,
    Month,
    Quarter,
}
