namespace QuantAnalyst.Core.Market;

public enum TradingDayKind
{
    /// <summary>Regular session.</summary>
    Full,

    /// <summary>Shortened session (early close).</summary>
    Half,

    /// <summary>Exchange holiday on a weekday.</summary>
    Closed,

    /// <summary>Saturday or Sunday.</summary>
    Weekend,
}

/// <summary>One calendar day. <see cref="Open"/>/<see cref="Close"/> are exchange local time (Europe/Stockholm).</summary>
public sealed record TradingDay(DateOnly Date, TradingDayKind Kind, TimeOnly? Open, TimeOnly? Close, string? Name)
{
    public bool IsTradingDay => Kind is TradingDayKind.Full or TradingDayKind.Half;
}

/// <summary>A holiday or early-close entry of one calendar year.</summary>
public sealed record CalendarEntry(DateOnly Date, string Name, TimeOnly? Close = null);

/// <summary>
/// One year of an exchange calendar as configured in <c>config/market-calendar.&lt;MIC&gt;.&lt;year&gt;.json</c>.
/// <see cref="VerifiedOn"/> is null until the owner has checked it against the exchange's own calendar.
/// </summary>
public sealed record CalendarYear(
    string Mic,
    int Year,
    TimeOnly RegularOpen,
    TimeOnly RegularClose,
    TimeOnly HalfDayOpen,
    TimeOnly HalfDayClose,
    IReadOnlyList<CalendarEntry> Closed,
    IReadOnlyList<CalendarEntry> HalfDays,
    string SourceUrl,
    DateOnly? VerifiedOn);

/// <summary>
/// Trading calendar of one exchange (CLAUDE.md: trading calendar = Nasdaq Stockholm). Pure: built from
/// <see cref="CalendarYear"/> data, no I/O. Dates outside the loaded years throw instead of being guessed.
/// </summary>
public sealed class MarketCalendar
{
    private readonly Dictionary<int, CalendarYear> _years;
    private readonly Dictionary<DateOnly, CalendarEntry> _closed = [];
    private readonly Dictionary<DateOnly, CalendarEntry> _half = [];

    public MarketCalendar(IEnumerable<CalendarYear> years)
    {
        ArgumentNullException.ThrowIfNull(years);
        CalendarYear[] all = [.. years];
        if (all.Length == 0)
        {
            throw new ArgumentException("A calendar needs at least one year.", nameof(years));
        }

        Mic = all[0].Mic;
        _years = [];
        foreach (CalendarYear y in all)
        {
            Validate(y, Mic);
            if (!_years.TryAdd(y.Year, y))
            {
                throw new ArgumentException($"Calendar year {y.Year} is given twice.", nameof(years));
            }

            foreach (CalendarEntry e in y.Closed)
            {
                _closed.Add(e.Date, e);
            }

            foreach (CalendarEntry e in y.HalfDays)
            {
                if (_closed.ContainsKey(e.Date))
                {
                    throw new ArgumentException($"{e.Date:yyyy-MM-dd} is listed both as closed and as a half day.", nameof(years));
                }

                _half.Add(e.Date, e);
            }
        }
    }

    public string Mic { get; }

    public IReadOnlyCollection<int> Years => _years.Keys;

    /// <summary>True only when every loaded year has been verified against the exchange (<c>verified_on</c> set).</summary>
    public bool IsVerified => _years.Values.All(y => y.VerifiedOn is not null);

    public IEnumerable<int> UnverifiedYears => _years.Values.Where(y => y.VerifiedOn is null).Select(y => y.Year).Order();

    public CalendarYear GetYear(int year) =>
        _years.TryGetValue(year, out CalendarYear? y)
            ? y
            : throw new ArgumentOutOfRangeException(nameof(year), year, $"No {Mic} calendar is loaded for {year}; add config/market-calendar.{Mic}.{year}.json.");

    public TradingDay Classify(DateOnly date)
    {
        CalendarYear y = GetYear(date.Year);
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return new TradingDay(date, TradingDayKind.Weekend, null, null, null);
        }

        if (_closed.TryGetValue(date, out CalendarEntry? closed))
        {
            return new TradingDay(date, TradingDayKind.Closed, null, null, closed.Name);
        }

        if (_half.TryGetValue(date, out CalendarEntry? half))
        {
            return new TradingDay(date, TradingDayKind.Half, y.HalfDayOpen, half.Close ?? y.HalfDayClose, half.Name);
        }

        return new TradingDay(date, TradingDayKind.Full, y.RegularOpen, y.RegularClose, null);
    }

    /// <summary>True while the regular or half-day session is running at <paramref name="utc"/> (open inclusive, close exclusive).</summary>
    public bool IsOpen(DateTimeOffset utc)
    {
        DateTimeOffset local = MarketTime.ToStockholm(utc);
        TradingDay day = Classify(DateOnly.FromDateTime(local.DateTime));
        if (!day.IsTradingDay)
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(local.DateTime);
        return time >= day.Open!.Value && time < day.Close!.Value;
    }

    /// <summary>The first trading day strictly after <paramref name="date"/>.</summary>
    public TradingDay NextTradingDay(DateOnly date)
    {
        for (DateOnly d = date.AddDays(1); ; d = d.AddDays(1))
        {
            TradingDay day = Classify(d);
            if (day.IsTradingDay)
            {
                return day;
            }
        }
    }

    private static void Validate(CalendarYear y, string mic)
    {
        if (!string.Equals(y.Mic, mic, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Calendar years mix exchanges ({mic} and {y.Mic}).", nameof(y));
        }

        if (y.RegularOpen >= y.RegularClose || y.HalfDayOpen >= y.HalfDayClose)
        {
            throw new ArgumentException($"{y.Mic} {y.Year}: session open must be before close.", nameof(y));
        }

        var seen = new HashSet<DateOnly>();
        foreach (CalendarEntry e in y.Closed.Concat(y.HalfDays))
        {
            if (e.Date.Year != y.Year)
            {
                throw new ArgumentException($"{y.Mic} {y.Year}: {e.Date:yyyy-MM-dd} ({e.Name}) is not in {y.Year}.", nameof(y));
            }

            if (e.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                throw new ArgumentException($"{y.Mic} {y.Year}: {e.Date:yyyy-MM-dd} ({e.Name}) is a weekend day; leave it out.", nameof(y));
            }

            if (!seen.Add(e.Date))
            {
                throw new ArgumentException($"{y.Mic} {y.Year}: {e.Date:yyyy-MM-dd} is listed twice.", nameof(y));
            }

            if (string.IsNullOrWhiteSpace(e.Name))
            {
                throw new ArgumentException($"{y.Mic} {y.Year}: {e.Date:yyyy-MM-dd} needs a name.", nameof(y));
            }

            if (e.Close is { } close && (close <= y.HalfDayOpen || close >= y.RegularClose))
            {
                throw new ArgumentException($"{y.Mic} {y.Year}: {e.Date:yyyy-MM-dd} early close {close} is outside the session.", nameof(y));
            }
        }
    }
}
