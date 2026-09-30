namespace QuantAnalyst.Core.Market;

/// <summary>Where a quote's bid/ask came from.</summary>
public enum QuoteSource
{
    None,

    /// <summary>The pushed order-depth stream.</summary>
    Stream,

    /// <summary>The polled market-data snapshot.</summary>
    Poll,
}

/// <summary>
/// A composed live quote (ADR 0002 §3): best bid/ask and depth from the newer of the depth stream and the poll, last
/// trade and volume from the poll. Missing values are null, never zero.
/// </summary>
/// <param name="DepthAtUtc">When the last depth-stream snapshot arrived (our clock).</param>
/// <param name="PollAtUtc">When the last successful poll arrived (our clock).</param>
/// <param name="AsOfUtc">The newest of <paramref name="DepthAtUtc"/> and <paramref name="PollAtUtc"/>: the age of our knowledge.</param>
/// <param name="ComposedAtUtc">When this quote was composed.</param>
/// <param name="IsStale">True when the quote must not be traded on; <paramref name="StaleReason"/> says why.</param>
/// <param name="DayHigh">The day's highest trade so far, from the poll (plan 19); null when unknown.</param>
/// <param name="DayLow">The day's lowest trade so far, from the poll (plan 19); null when unknown.</param>
public sealed record Quote(
    OrderbookId OrderbookId,
    decimal? Bid,
    decimal BidVolume,
    decimal? Ask,
    decimal AskVolume,
    decimal? Last,
    DateTimeOffset? TimeOfLastUtc,
    decimal? TotalVolumeTraded,
    IReadOnlyList<DepthLevel> Depth,
    QuoteSource BidAskSource,
    DateTimeOffset? DepthAtUtc,
    DateTimeOffset? PollAtUtc,
    DateTimeOffset? AsOfUtc,
    DateTimeOffset ComposedAtUtc,
    bool IsStale,
    string? StaleReason,
    decimal? DayHigh = null,
    decimal? DayLow = null)
{
    /// <summary>Age of the newest information at composition time, or null when there is none.</summary>
    public TimeSpan? Age => AsOfUtc is { } asOf ? ComposedAtUtc - asOf : null;
}
