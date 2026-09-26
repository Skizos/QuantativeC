using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Calendar;

namespace QuantAnalyst.Data.Tests;

/// <summary>
/// Gate item: every weekday of 2026 and 2027 is classified by the committed XSTO calendar files. The drafts are also
/// recomputed from the Swedish holiday rules so a transcription slip fails the build.
/// </summary>
public sealed class MarketCalendarTests
{
    private static readonly string ConfigDir = Path.Combine(AppContext.BaseDirectory, "config");

    private static MarketCalendar Committed() => MarketCalendarLoader.LoadDirectory(ConfigDir);

    [Fact]
    public void EveryDayOf2026And2027IsClassified_WeekdaysAsFullHalfOrClosed()
    {
        MarketCalendar calendar = Committed();
        Assert.Equal([2026, 2027], calendar.Years.Order());
        foreach (int year in new[] { 2026, 2027 })
        {
            var counts = new Dictionary<TradingDayKind, int>();
            for (var d = new DateOnly(year, 1, 1); d.Year == year; d = d.AddDays(1))
            {
                TradingDay day = calendar.Classify(d);
                counts[day.Kind] = counts.GetValueOrDefault(day.Kind) + 1;
                bool weekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                Assert.Equal(weekend, day.Kind == TradingDayKind.Weekend);
                if (day.IsTradingDay)
                {
                    Assert.Equal(new TimeOnly(9, 0), day.Open);
                    Assert.Equal(day.Kind == TradingDayKind.Half ? new TimeOnly(13, 0) : new TimeOnly(17, 30), day.Close);
                }
                else
                {
                    Assert.Null(day.Open);
                }
            }

            int weekdays = counts[TradingDayKind.Full] + counts[TradingDayKind.Half] + counts[TradingDayKind.Closed];
            Assert.Equal(261, weekdays); // both years have 261 weekdays
            Assert.Equal(5, counts[TradingDayKind.Half]);
        }

        Assert.Equal(10, Enumerable.Range(0, 365).Count(i => calendar.Classify(new DateOnly(2026, 1, 1).AddDays(i)).Kind == TradingDayKind.Closed));
        Assert.Equal(8, Enumerable.Range(0, 365).Count(i => calendar.Classify(new DateOnly(2027, 1, 1).AddDays(i)).Kind == TradingDayKind.Closed));
    }

    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    public void CommittedDraftEqualsTheHolidayRules(int year)
    {
        CalendarYear file = MarketCalendarLoader.LoadYear(Path.Combine(ConfigDir, MarketCalendarLoader.FileName("XSTO", year)));
        (DateOnly[] closed, DateOnly[] half) = SwedishExchangeRules(year);
        Assert.Equal(closed, file.Closed.Select(e => e.Date).Order());
        Assert.Equal(half, file.HalfDays.Select(e => e.Date).Order());
    }

    [Fact]
    public void EasterComputusMatchesKnownDates()
    {
        Assert.Equal(new DateOnly(2026, 4, 5), Easter(2026));
        Assert.Equal(new DateOnly(2027, 3, 28), Easter(2027));
        Assert.Equal(new DateOnly(2024, 3, 31), Easter(2024));
    }

    [Fact]
    public void VerificationFlagFollowsTheFiles()
    {
        MarketCalendar calendar = Committed();
        bool allVerified = calendar.Years.All(y => calendar.GetYear(y).VerifiedOn is not null);
        Assert.Equal(allVerified, calendar.IsVerified);
        Assert.Equal(calendar.Years.Where(y => calendar.GetYear(y).VerifiedOn is null).Order(), calendar.UnverifiedYears);

        MarketCalendar synthetic = new([Year(2030, verifiedOn: new DateOnly(2029, 12, 1)), Year(2031, verifiedOn: null)]);
        Assert.False(synthetic.IsVerified);
        Assert.Equal([2031], synthetic.UnverifiedYears);
    }

    [Fact]
    public void KnownDays()
    {
        MarketCalendar c = Committed();
        Assert.Equal(TradingDayKind.Closed, c.Classify(new DateOnly(2026, 6, 19)).Kind); // Midsummer Eve, Friday
        Assert.Equal("Midsummer Eve", c.Classify(new DateOnly(2026, 6, 19)).Name);
        Assert.Equal(TradingDayKind.Closed, c.Classify(new DateOnly(2026, 12, 24)).Kind);
        Assert.Equal(TradingDayKind.Half, c.Classify(new DateOnly(2026, 4, 30)).Kind);
        Assert.Equal(TradingDayKind.Weekend, c.Classify(new DateOnly(2026, 6, 6)).Kind); // National Day on a Saturday
        Assert.Equal(TradingDayKind.Full, c.Classify(new DateOnly(2026, 9, 28)).Kind);
        Assert.Equal(new DateOnly(2026, 12, 28), c.NextTradingDay(new DateOnly(2026, 12, 23)).Date);
        Assert.Equal(new DateOnly(2027, 1, 4), c.NextTradingDay(new DateOnly(2026, 12, 30)).Date); // 31 Dec, 1 Jan closed; weekend
    }

    [Theory]
    [InlineData("2026-03-27T08:00:00Z", true)] // 09:00 CET, open
    [InlineData("2026-03-27T07:59:59Z", false)]
    [InlineData("2026-03-30T07:00:00Z", true)] // 09:00 CEST after the switch on 29 March
    [InlineData("2026-03-30T06:59:59Z", false)]
    [InlineData("2026-10-26T16:29:59Z", true)] // 17:29:59 CET after the switch on 25 October
    [InlineData("2026-10-26T16:30:00Z", false)] // close is exclusive
    [InlineData("2026-04-30T10:59:00Z", true)] // half day, 12:59 CEST
    [InlineData("2026-04-30T11:00:00Z", false)] // 13:00 CEST
    [InlineData("2026-06-19T10:00:00Z", false)] // closed
    [InlineData("2026-09-26T10:00:00Z", false)] // Saturday
    public void IsOpenUsesStockholmTimeAcrossDst(string utc, bool open) =>
        Assert.Equal(open, Committed().IsOpen(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void DatesOutsideTheLoadedYearsThrow()
    {
        MarketCalendar c = Committed();
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Classify(new DateOnly(2028, 1, 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Classify(new DateOnly(2025, 12, 31)));
    }

    [Theory]
    [InlineData("\"closed\": [", "\"closed\": [{\"date\":\"2026-01-03\",\"name\":\"Saturday\"},", "weekend")]
    [InlineData("\"closed\": [", "\"closed\": [{\"date\":\"2026-01-01\",\"name\":\"Twice\"},", "twice")]
    [InlineData("\"closed\": [", "\"closed\": [{\"date\":\"2027-01-04\",\"name\":\"Next year\"},", "not in 2026")]
    [InlineData("\"closed\": [", "\"closed\": [{\"date\":\"2026-01-05\",\"name\":\"Also a half day\"},", "listed twice")]
    [InlineData("\"timeZone\": \"Europe/Stockholm\"", "\"timeZone\": \"UTC\"", "timeZone")]
    [InlineData("\"verified_on\": null", "\"verified_on\": null, \"extra\": 1", "extra")]
    [InlineData("\"verified_on\": null", "\"verified_on\": \"yesterday\"", "yyyy-MM-dd")]
    [InlineData("\"verified_on\": null", "\"verified_on\": true", "verified_on must be null (not checked yet) or the date you checked the file, in quotes, e.g. \"2026-09-26\".")]
    [InlineData("\"year\": 2026", "\"year\": 2027", "must be named")]
    public void BadFilesAreRejected(string find, string replace, string expected)
    {
        using var dir = new TempDir();
        // Start from the committed file with verified_on reset, so this test does not depend on whether the owner
        // has verified the calendar yet.
        string text = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(ConfigDir, MarketCalendarLoader.FileName("XSTO", 2026))),
            "\"verified_on\": [^,\\r\\n]+",
            "\"verified_on\": null");
        Assert.Contains(find, text, StringComparison.Ordinal);
        File.WriteAllText(dir.File(MarketCalendarLoader.FileName("XSTO", 2026)), text.Replace(find, replace, StringComparison.Ordinal));
        var ex = Assert.Throws<CalendarConfigException>(() => MarketCalendarLoader.LoadDirectory(dir.Path));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFolderOrFilesAreReported()
    {
        using var dir = new TempDir();
        Assert.Throws<CalendarConfigException>(() => MarketCalendarLoader.LoadDirectory(Path.Combine(dir.Path, "nope")));
        Assert.Throws<CalendarConfigException>(() => MarketCalendarLoader.LoadDirectory(dir.Path));
    }

    private static CalendarYear Year(int year, DateOnly? verifiedOn) => new(
        "XSTO", year, new TimeOnly(9, 0), new TimeOnly(17, 30), new TimeOnly(9, 0), new TimeOnly(13, 0), [], [], "https://example.invalid/", verifiedOn);

    /// <summary>Independent restatement of the drafting rules (market-rules.md; exchange_calendars XSTO), weekend dates dropped.</summary>
    private static (DateOnly[] Closed, DateOnly[] Half) SwedishExchangeRules(int y)
    {
        DateOnly e = Easter(y);
        DateOnly midsummerEve = Enumerable.Range(19, 7).Select(d => new DateOnly(y, 6, d)).Single(d => d.DayOfWeek == DayOfWeek.Friday);
        DateOnly allSaintsEve = Enumerable.Range(0, 7).Select(i => new DateOnly(y, 10, 30).AddDays(i)).Single(d => d.DayOfWeek == DayOfWeek.Friday);
        DateOnly[] closed =
        [
            new(y, 1, 1), new(y, 1, 6), e.AddDays(-2), e.AddDays(1), new(y, 5, 1), e.AddDays(39), new(y, 6, 6), midsummerEve,
            new(y, 12, 24), new(y, 12, 25), new(y, 12, 26), new(y, 12, 31),
        ];
        DateOnly[] half = [new(y, 1, 5), e.AddDays(-3), new(y, 4, 30), e.AddDays(38), allSaintsEve];
        return (Weekdays(closed), Weekdays(half));

        static DateOnly[] Weekdays(DateOnly[] days) => [.. days.Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).Order()];
    }

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
