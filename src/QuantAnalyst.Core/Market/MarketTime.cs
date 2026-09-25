namespace QuantAnalyst.Core.Market;

/// <summary>
/// Nasdaq Stockholm local time (CLAUDE.md: store UTC, display Europe/Stockholm). Avanza sends some timestamps
/// without an offset. The owner's live recording of 2026-09-25 shows they are Stockholm local time: the
/// marketdata <c>timeOfLast</c> "17:29:40" equals the epoch-ms <c>receivedTime</c>/<c>dealTime</c> 15:29:40Z.
/// </summary>
public static class MarketTime
{
    public static TimeZoneInfo Stockholm { get; } = FindStockholm();

    /// <summary>
    /// Converts a Stockholm wall-clock time to UTC. Returns false for times that don't exist or are ambiguous
    /// because of a DST change (never guessed).
    /// </summary>
    public static bool TryStockholmToUtc(DateTime local, out DateTimeOffset utc)
    {
        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (Stockholm.IsInvalidTime(unspecified) || Stockholm.IsAmbiguousTime(unspecified))
        {
            utc = default;
            return false;
        }

        utc = new DateTimeOffset(unspecified, Stockholm.GetUtcOffset(unspecified)).ToUniversalTime();
        return true;
    }

    public static DateTimeOffset ToStockholm(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Stockholm);

    private static TimeZoneInfo FindStockholm()
    {
        // IANA id (Linux, and Windows with ICU); the Windows id as a fallback. Never silently UTC.
        foreach (string id in new[] { "Europe/Stockholm", "W. Europe Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? zone))
            {
                return zone;
            }
        }

        throw new TimeZoneNotFoundException("Europe/Stockholm time zone data is not available on this system.");
    }
}
