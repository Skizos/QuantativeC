using System.Globalization;

namespace QuantAnalyst.Trading.Scheduling;

/// <summary>How many of a decision's shares have a price the risk checks accept, and the first one that has none.</summary>
/// <param name="Missing">E.g. "ERIC B: stale: depth stream reconnecting (HTTP 429)"; null when every share is ready.</param>
public readonly record struct PriceCoverage(int Ready, int Total, string? Missing)
{
    public bool All => Ready >= Total;

    public bool None => Ready == 0 && Total > 0;
}

/// <summary>
/// Holds a day's decision until every share has a price the risk checks accept (plan 18). A session started after its
/// decision time, or one that decides at start, would otherwise decide in its first second, before the quote feeds'
/// first answer, and skip every share for the day.
/// <list type="bullet">
/// <item>some shares ready: decide after <see cref="Wait"/>; the others are skipped for the day</item>
/// <item>none ready (a feed outage, or Confirm's stream refused): keep waiting, up to <see cref="OutageWait"/>, so a short
/// outage delays the decision instead of costing the day; then decide anyway</item>
/// </list>
/// Every message names the first share that is missing and why.
/// </summary>
/// <param name="coverage">The shares' coverage at that moment; null: never waits.</param>
public sealed class PriceGate(Func<DateTimeOffset, PriceCoverage>? coverage)
{
    /// <summary>The quote feeds poll at once and then every 5 s, so a minute is ample for a share that trades.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    /// <summary>How long a decision waits while no share has a price at all.</summary>
    public static readonly TimeSpan OutageWait = TimeSpan.FromMinutes(30);

    private DateOnly? _day;
    private DateTimeOffset _since;
    private bool _told;

    /// <summary>
    /// True when the decision of <paramref name="day"/> may go ahead at <paramref name="now"/>. <paramref name="say"/> is
    /// told once that it waits, and once if it decides without every price.
    /// </summary>
    public bool Open(DateOnly day, DateTimeOffset now, Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);
        if (coverage is null)
        {
            return true;
        }

        if (_day != day)
        {
            _day = day;
            _since = now;
            _told = false;
        }

        PriceCoverage c = coverage(now);
        if (c.All)
        {
            return true;
        }

        TimeSpan waited = now - _since;
        CultureInfo ci = CultureInfo.InvariantCulture;
        if (!c.None && waited >= Wait)
        {
            say(string.Create(ci,
                $"no usable live price for {c.Total - c.Ready} of {c.Total} share(s) after {Wait.TotalSeconds:0} s ({c.Missing}); deciding anyway (they are skipped today)."));
            return true;
        }

        if (c.None && waited >= OutageWait)
        {
            say(string.Create(ci,
                $"no usable live price for any share after {OutageWait.TotalMinutes:0} minutes ({c.Missing}); deciding anyway (they are skipped today)."));
            return true;
        }

        if (!_told)
        {
            _told = true;
            say(string.Create(ci,
                $"waiting for live prices before deciding ({c.Ready} of {c.Total} ready; {c.Missing}): at most {Wait.TotalSeconds:0} s, or {OutageWait.TotalMinutes:0} minutes while none has one."));
        }

        return false;
    }
}
