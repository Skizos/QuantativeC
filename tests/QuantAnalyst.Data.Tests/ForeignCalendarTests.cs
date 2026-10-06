using System.Globalization;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Calendar;

namespace QuantAnalyst.Data.Tests;

/// <summary>
/// ADR 0005: the US (XNYS) and Canadian (XTSE) calendars, in their own time zones. The committed drafts are recomputed
/// from the US and Canadian holiday rules, so a transcription slip fails the build, as for XSTO.
/// </summary>
public sealed class ForeignCalendarTests
{
    private static readonly string ConfigDir = Path.Combine(AppContext.BaseDirectory, "config");

    [Fact]
    public void MarketsFollowTheCurrency()
    {
        Assert.Equal("XSTO", Markets.ForCurrency("SEK")!.Mic);
        Assert.Equal("XNYS", Markets.ForCurrency("USD")!.Mic);
        Assert.Equal("XTSE", Markets.ForCurrency("CAD")!.Mic);
        Assert.Null(Markets.ForCurrency("EUR"));
        Assert.Null(Markets.ForCurrency(null));
        Assert.Equal("SEK, USD, CAD", Markets.CurrencyList);
        Assert.False(Markets.IsForeign("SEK"));
        Assert.True(Markets.IsForeign("USD"));
        Assert.Same(Markets.UnitedStates, Markets.ForMic("XNYS"));
        Assert.Throws<TimeZoneNotFoundException>(() => MarketTime.Zone("Europe/Oslo"));
    }

    [Theory]
    [InlineData("XNYS")]
    [InlineData("XTSE")]
    public void EveryDayOf2026And2027IsClassified_InTheMarketsOwnTime(string mic)
    {
        MarketCalendar calendar = MarketCalendarLoader.LoadDirectory(ConfigDir, mic);
        Assert.Equal([2026, 2027], calendar.Years.Order());
        Assert.Equal(Markets.ForMic(mic)!.TimeZoneId, calendar.TimeZoneId);
        foreach (int year in new[] { 2026, 2027 })
        {
            for (var d = new DateOnly(year, 1, 1); d.Year == year; d = d.AddDays(1))
            {
                TradingDay day = calendar.Classify(d);
                Assert.Equal(d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday, day.Kind == TradingDayKind.Weekend);
                if (day.IsTradingDay)
                {
                    Assert.Equal(new TimeOnly(9, 30), day.Open);
                    Assert.Equal(day.Kind == TradingDayKind.Half ? new TimeOnly(13, 0) : new TimeOnly(16, 0), day.Close);
                }
            }
        }
    }

    [Theory]
    [InlineData("XNYS", 2026)]
    [InlineData("XNYS", 2027)]
    [InlineData("XTSE", 2026)]
    [InlineData("XTSE", 2027)]
    public void CommittedDraftEqualsTheHolidayRules(string mic, int year)
    {
        CalendarYear file = MarketCalendarLoader.LoadYear(Path.Combine(ConfigDir, MarketCalendarLoader.FileName(mic, year)));
        (DateOnly[] closed, DateOnly[] half) = mic == "XNYS" ? UsRules(year) : CanadianRules(year);
        Assert.Equal(closed, file.Closed.Select(e => e.Date).Order());
        Assert.Equal(half, file.HalfDays.Select(e => e.Date).Order());
        Assert.Null(file.VerifiedOn); // drafts until the owner checks them (ADR 0005 open item 2)
    }

    [Theory]
    // New York: 09:30-16:00 EDT/EST. 28 Sep 2026: EDT (UTC-4).
    [InlineData("XNYS", "2026-09-28T13:30:00Z", true)]
    [InlineData("XNYS", "2026-09-28T13:29:59Z", false)]
    [InlineData("XNYS", "2026-09-28T19:59:59Z", true)]
    [InlineData("XNYS", "2026-09-28T20:00:00Z", false)]
    // 9 March 2026: New York is on summer time (since 8 March), Stockholm not yet (29 March): the open is 14:30 Stockholm.
    [InlineData("XNYS", "2026-03-09T13:30:00Z", true)]
    [InlineData("XNYS", "2026-03-09T13:29:00Z", false)]
    // Day after Thanksgiving: early close 13:00 EST = 18:00Z.
    [InlineData("XNYS", "2026-11-27T17:59:00Z", true)]
    [InlineData("XNYS", "2026-11-27T18:00:00Z", false)]
    [InlineData("XNYS", "2026-07-03T15:00:00Z", false)] // Independence Day observed
    [InlineData("XTSE", "2026-10-12T15:00:00Z", false)] // Canadian Thanksgiving; New York is open
    [InlineData("XTSE", "2026-12-28T15:00:00Z", false)] // Boxing Day observed
    [InlineData("XTSE", "2026-12-24T17:59:00Z", true)]  // Christmas Eve, early close 13:00 EST
    [InlineData("XTSE", "2026-12-24T18:00:00Z", false)]
    public void IsOpenUsesTheMarketsOwnClockAcrossDst(string mic, string utc, bool open) =>
        Assert.Equal(open, MarketCalendarLoader.LoadDirectory(ConfigDir, mic).IsOpen(DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture)));

    [Fact]
    public void SessionTimesConvertToStockholmThroughBothDstChanges()
    {
        MarketCalendar ny = MarketCalendarLoader.LoadDirectory(ConfigDir, "XNYS");
        Assert.Equal("15:30", Stockholm(ny.ToUtc(new DateOnly(2026, 9, 28), new TimeOnly(9, 30))));
        Assert.Equal("14:30", Stockholm(ny.ToUtc(new DateOnly(2026, 3, 9), new TimeOnly(9, 30)))); // US on DST, Sweden not
        Assert.Equal("14:30", Stockholm(ny.ToUtc(new DateOnly(2026, 10, 26), new TimeOnly(9, 30)))); // Sweden off DST, US not
        Assert.Equal("15:30", Stockholm(ny.ToUtc(new DateOnly(2026, 11, 2), new TimeOnly(9, 30))));
        Assert.Equal(new DateOnly(2026, 9, 28), ny.LocalDate(DateTimeOffset.Parse("2026-09-29T03:00:00Z", CultureInfo.InvariantCulture))); // 23:00 in New York

        static string Stockholm(DateTimeOffset utc) => MarketTime.ToStockholm(utc).ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    [Fact]
    public void EachMarketNeedsItsOwnTimeZone_AndOnlyKnownMarketsLoad()
    {
        using var dir = new TempDir();
        string text = File.ReadAllText(Path.Combine(ConfigDir, MarketCalendarLoader.FileName("XNYS", 2026)));
        File.WriteAllText(dir.File(MarketCalendarLoader.FileName("XNYS", 2026)), text.Replace("America/New_York", "Europe/Stockholm", StringComparison.Ordinal));
        var ex = Assert.Throws<CalendarConfigException>(() => MarketCalendarLoader.LoadDirectory(dir.Path, "XNYS"));
        Assert.Contains("timeZone must be America/New_York", ex.Message, StringComparison.Ordinal);

        File.Delete(dir.File(MarketCalendarLoader.FileName("XNYS", 2026)));
        File.WriteAllText(dir.File(MarketCalendarLoader.FileName("XOSL", 2026)), text.Replace("\"XNYS\"", "\"XOSL\"", StringComparison.Ordinal));
        ex = Assert.Throws<CalendarConfigException>(() => MarketCalendarLoader.LoadDirectory(dir.Path, "XOSL"));
        Assert.Contains("XOSL is not a market the program trades on", ex.Message, StringComparison.Ordinal);

        // One calendar never mixes markets or time zones.
        var ny = new CalendarYear("XNYS", 2026, new TimeOnly(9, 30), new TimeOnly(16, 0), new TimeOnly(9, 30), new TimeOnly(13, 0), [], [], "https://example.invalid/", null)
        {
            TimeZoneId = "America/New_York",
        };
        Assert.Throws<ArgumentException>(() => new MarketCalendar([ny, ny with { Year = 2027, TimeZoneId = "America/Toronto" }]));
    }

    /// <summary>NYSE rules (weekend holidays observed on the nearest weekday; New Year's on a Saturday is not moved).</summary>
    private static (DateOnly[] Closed, DateOnly[] Half) UsRules(int y)
    {
        DateOnly goodFriday = Easter(y).AddDays(-2);
        DateOnly thanksgiving = Nth(y, 11, DayOfWeek.Thursday, 4);
        DateOnly[] closed =
        [
            Observed(new DateOnly(y, 1, 1), saturdayToFriday: false), Nth(y, 1, DayOfWeek.Monday, 3), Nth(y, 2, DayOfWeek.Monday, 3), goodFriday,
            Last(y, 5, DayOfWeek.Monday), Observed(new DateOnly(y, 6, 19)), Observed(new DateOnly(y, 7, 4)), Nth(y, 9, DayOfWeek.Monday, 1),
            thanksgiving, Observed(new DateOnly(y, 12, 25)),
        ];
        var half = new List<DateOnly> { thanksgiving.AddDays(1) };
        foreach (DateOnly eve in new[] { new DateOnly(y, 7, 3), new DateOnly(y, 12, 24) })
        {
            if (eve.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Thursday && !closed.Contains(eve))
            {
                half.Add(eve);
            }
        }

        return (Weekdays(closed), Weekdays([.. half]));
    }

    /// <summary>TSX rules: weekend holidays move to the next weekday; Christmas Eve closes at 13:00 on a weekday.</summary>
    private static (DateOnly[] Closed, DateOnly[] Half) CanadianRules(int y)
    {
        DateOnly christmas = new(y, 12, 25);
        DateOnly christmasObserved = christmas.DayOfWeek switch
        {
            DayOfWeek.Saturday => christmas.AddDays(2),
            DayOfWeek.Sunday => christmas.AddDays(1),
            _ => christmas,
        };
        DateOnly boxing = christmasObserved.AddDays(1);
        while (boxing.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || boxing <= christmasObserved)
        {
            boxing = boxing.AddDays(1);
        }

        DateOnly victoria = Enumerable.Range(18, 7).Select(d => new DateOnly(y, 5, d)).Single(d => d.DayOfWeek == DayOfWeek.Monday); // the Monday before 25 May
        DateOnly[] closed =
        [
            NextWeekday(new DateOnly(y, 1, 1)), Nth(y, 2, DayOfWeek.Monday, 3), Easter(y).AddDays(-2), victoria, NextWeekday(new DateOnly(y, 7, 1)),
            Nth(y, 8, DayOfWeek.Monday, 1), Nth(y, 9, DayOfWeek.Monday, 1), Nth(y, 10, DayOfWeek.Monday, 2), christmasObserved, boxing,
        ];
        DateOnly eve = new(y, 12, 24);
        DateOnly[] half = eve.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? [] : [eve];
        return (Weekdays(closed), half);
    }

    private static DateOnly Observed(DateOnly d, bool saturdayToFriday = true) => d.DayOfWeek switch
    {
        DayOfWeek.Saturday => saturdayToFriday ? d.AddDays(-1) : d,
        DayOfWeek.Sunday => d.AddDays(1),
        _ => d,
    };

    private static DateOnly NextWeekday(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Saturday => d.AddDays(2),
        DayOfWeek.Sunday => d.AddDays(1),
        _ => d,
    };

    private static DateOnly Nth(int y, int month, DayOfWeek day, int n) =>
        Enumerable.Range(1, 31).Where(d => d <= DateTime.DaysInMonth(y, month)).Select(d => new DateOnly(y, month, d)).Where(d => d.DayOfWeek == day).ElementAt(n - 1);

    private static DateOnly Last(int y, int month, DayOfWeek day) =>
        Enumerable.Range(1, DateTime.DaysInMonth(y, month)).Select(d => new DateOnly(y, month, d)).Last(d => d.DayOfWeek == day);

    private static DateOnly[] Weekdays(DateOnly[] days) => [.. days.Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).Distinct().Order()];

    /// <summary>Anonymous Gregorian computus (Meeus/Jones/Butcher).</summary>
    private static DateOnly Easter(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = ((19 * a) + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        int m = (a + (11 * h) + (22 * l)) / 451;
        int month = (h + l - (7 * m) + 114) / 31, day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(y, month, day);
    }
}
