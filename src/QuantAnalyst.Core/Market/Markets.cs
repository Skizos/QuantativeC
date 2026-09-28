namespace QuantAnalyst.Core.Market;

/// <summary>
/// An exchange the program trades on (ADR 0005): its calendar code, the currency its shares trade in, and the time zone
/// its session times are given in.
/// </summary>
/// <param name="Mic">The calendar's market identifier: <c>config/market-calendar.&lt;Mic&gt;.&lt;year&gt;.json</c>.</param>
public sealed record MarketInfo(string Mic, string Currency, string TimeZoneId, string Name)
{
    /// <summary>Gets the time zone of the session times (Windows or IANA data, never silently UTC).</summary>
    public TimeZoneInfo TimeZone => MarketTime.Zone(TimeZoneId);
}

/// <summary>
/// The markets by currency (ADR 0005): a share's market follows its currency. SEK is Nasdaq Stockholm; USD the US market
/// (the NYSE calendar, which Nasdaq shares); CAD the Canadian market (the TSX calendar, which TSX Venture shares). Any
/// other currency has no market here and is refused.
/// </summary>
public static class Markets
{
    public static MarketInfo Stockholm { get; } = new("XSTO", "SEK", "Europe/Stockholm", "Nasdaq Stockholm");

    public static MarketInfo UnitedStates { get; } = new("XNYS", "USD", "America/New_York", "US (NYSE, Nasdaq)");

    public static MarketInfo Canada { get; } = new("XTSE", "CAD", "America/Toronto", "Canada (TSX)");

    /// <summary>Gets every market, Stockholm first.</summary>
    public static IReadOnlyList<MarketInfo> All { get; } = [Stockholm, UnitedStates, Canada];

    /// <summary>Gets the currencies a share may trade in: "SEK, USD, CAD".</summary>
    public static string CurrencyList { get; } = string.Join(", ", All.Select(m => m.Currency));

    /// <summary>The market of a share in <paramref name="currency"/>, or null when the program does not trade that currency.</summary>
    public static MarketInfo? ForCurrency(string? currency) =>
        All.FirstOrDefault(m => string.Equals(m.Currency, currency, StringComparison.Ordinal));

    /// <summary>The market with calendar <paramref name="mic"/>, or null.</summary>
    public static MarketInfo? ForMic(string? mic) =>
        All.FirstOrDefault(m => string.Equals(m.Mic, mic, StringComparison.Ordinal));

    /// <summary>True for a currency other than the account's (SEK): the FX rate and fee apply.</summary>
    public static bool IsForeign(string currency) => !string.Equals(currency, Stockholm.Currency, StringComparison.Ordinal);
}
