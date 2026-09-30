using System.Globalization;

namespace QuantAnalyst.Trading.Scheduling;

/// <summary>
/// Holds a day's decision until every share has a usable live price, for at most <see cref="Wait"/> after the decision
/// became due. A session started after its decision time (or one that decides at start) otherwise decides in its first
/// second, before the quote feeds' first answer, and the planner skips every share ("no fresh live price") for the
/// whole day. After the wait it decides anyway; a share still without a price is skipped as before.
/// </summary>
/// <param name="ready">True when every share of the decision has a usable price at that moment; null: always ready.</param>
public sealed class PriceGate(Func<DateTimeOffset, bool>? ready)
{
    /// <summary>The quote feeds poll at once and then every 5 s, so a minute is ample for a share that trades.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

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
        if (ready is null)
        {
            return true;
        }

        if (_day != day)
        {
            _day = day;
            _since = now;
            _told = false;
        }

        if (ready(now))
        {
            return true;
        }

        if (now - _since >= Wait)
        {
            say(string.Create(CultureInfo.InvariantCulture,
                $"no live price for every share after {Wait.TotalSeconds:0} s; deciding anyway (a share without one is skipped)."));
            return true;
        }

        if (!_told)
        {
            _told = true;
            say(string.Create(CultureInfo.InvariantCulture, $"waiting for live prices before deciding (at most {Wait.TotalSeconds:0} s)."));
        }

        return false;
    }
}
