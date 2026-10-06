namespace QuantAnalyst.Core.Market;

/// <summary>
/// Nasdaq Stockholm local time (CLAUDE.md: store UTC, display Europe/Stockholm), and the other markets' zones (ADR 0005). Avanza sends some timestamps
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

    /// <summary>
    /// Converts a wall-clock time in <paramref name="zone"/> to UTC. Returns false for times that don't exist or are
    /// ambiguous because of a DST change (never guessed).
    /// </summary>
    public static bool TryLocalToUtc(DateTime local, TimeZoneInfo zone, out DateTimeOffset utc)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified) || zone.IsAmbiguousTime(unspecified))
        {
            utc = default;
            return false;
        }

        utc = new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
        return true;
    }

    /// <summary>The wall-clock time in <paramref name="zone"/> at <paramref name="utc"/>.</summary>
    public static DateTimeOffset ToZone(DateTimeOffset utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(utc, zone);

    /// <summary>A market's time zone by its IANA id (ADR 0005), with the Windows id as a fallback. Never silently UTC.</summary>
    public static TimeZoneInfo Zone(string ianaId) => ianaId switch
    {
        "Europe/Stockholm" => Stockholm,
        "America/New_York" => NewYork.Value,
        "America/Toronto" => Toronto.Value,
        _ => throw new TimeZoneNotFoundException($"{ianaId} is not the time zone of a market the program trades on."),
    };

    private static readonly Lazy<TimeZoneInfo> NewYork = new(() => Find("America/New_York", "Eastern Standard Time"));

    // Toronto and New York keep the same clock (Eastern time, the same DST dates).
    private static readonly Lazy<TimeZoneInfo> Toronto = new(() => Find("America/Toronto", "Eastern Standard Time"));

    private static TimeZoneInfo FindStockholm() => Find("Europe/Stockholm", "W. Europe Standard Time");

    private static TimeZoneInfo Find(string ianaId, string windowsId)
    {
        // IANA id (Linux, and Windows with ICU); the Windows id as a fallback. Never silently UTC.
        foreach (string id in new[] { ianaId, windowsId })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? zone))
            {
                return zone;
            }
        }

        throw new TimeZoneNotFoundException($"{ianaId} time zone data is not available on this system.");
    }
}
