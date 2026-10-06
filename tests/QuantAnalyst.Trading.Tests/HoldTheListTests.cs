using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Reports;

namespace QuantAnalyst.Trading.Tests;

/// <summary>Plan 24: Paper against holding its own list equally, at the same close prices.</summary>
public sealed class HoldTheListTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>> Dividends = new Dictionary<string, IReadOnlyList<DividendEvent>>
    {
        ["5269"] = [new DividendEvent(new DateOnly(2026, 9, 28), null, 4m, "SEK", "ORDINARY"), new DividendEvent(new DateOnly(2026, 12, 1), null, 9m, "SEK", "ORDINARY")],
    };

    private static EodCloseMark Mark(string id, string ticker, decimal? close, decimal sekPerUnit = 1m, string currency = "SEK") => new(id, ticker, currency, close, sekPerUnit);

    private static EodReport Day(DateOnly date, decimal start, decimal end, decimal cash, IReadOnlyList<EodCloseMark> marks, string mode = "Paper") =>
        PromotionGateTests.Day(0, mode: mode) with
        {
            Date = date,
            Account = new EodAccount(start, end, end - start, decimal.Round((end - start) / start, 6), cash, 1m),
            CloseMarks = marks,
        };

    /// <summary>
    /// Thu: the first day (nothing before it). Fri: nothing moves. Mon: ERIC +2 %, VOLV −2 % with a 4 SEK dividend (0 %),
    /// AAPL flat in USD but +5 % in SEK. Tue: no session. Wed (from Monday's close): ERIC splits 2:1 (left out), VOLV
    /// +1.02 %, AAPL without a price (left out), TELIA new (left out). Thu: a Confirm day, not Paper.
    /// </summary>
    private static EodReport[] Reports() =>
    [
        Day(new DateOnly(2026, 9, 24), 5_000m, 5_000m, 2_500m, [Mark("5240", "ERIC B", 100m), Mark("5269", "VOLV B", 200m), Mark("4478", "AAPL", 10m, 10m, "USD")]),
        Day(new DateOnly(2026, 9, 25), 5_000m, 5_000m, 2_500m, [Mark("5240", "ERIC B", 100m), Mark("5269", "VOLV B", 200m), Mark("4478", "AAPL", 10m, 10m, "USD")]),
        Day(new DateOnly(2026, 9, 28), 5_000m, 5_050m, 2_500m, [Mark("5240", "ERIC B", 102m), Mark("5269", "VOLV B", 196m), Mark("4478", "AAPL", 10m, 10.5m, "USD")]),
        Day(new DateOnly(2026, 9, 30), 5_050m, 5_100m, 2_550m,
            [Mark("5240", "ERIC B", 51m), Mark("5269", "VOLV B", 198m), Mark("4478", "AAPL", null, 10.5m, "USD"), Mark("5479", "TELIA", 40m)]),
        Day(new DateOnly(2026, 10, 1), 5_100m, 9_999m, 0m, [Mark("5240", "ERIC B", 999m), Mark("5269", "VOLV B", 999m)], mode: "Confirm"),
    ];

    [Fact]
    public void EachPaperDay_IsTheListsAverageReturnFromThePaperCloseBefore_WithDividendsAndFx()
    {
        IReadOnlyList<ListDay> days = HoldTheList.Days(Reports(), Dividends);

        Assert.Equal([new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 28), new DateOnly(2026, 9, 30)], days.Select(d => d.Date));
        Assert.Equal((0m, 0m, 0.5m, 3), (days[0].Return, days[0].PaperReturn, days[0].Exposure, days[0].Shares));

        ListDay monday = days[1];
        Assert.Equal((0.023333m, 0.01m, 3), (monday.Return, monday.PaperReturn, monday.Shares)); // (2 % + 0 % + 5 %) / 3
        Assert.Empty(monday.LeftOut);

        ListDay wednesday = days[2];
        Assert.Equal(new DateOnly(2026, 9, 28), wednesday.From); // across Tuesday, which had no session
        Assert.Equal((0.010204m, 0.505m, 1), (wednesday.Return, wednesday.Exposure, wednesday.Shares)); // VOLV 196 → 198; (5,050 − 2,500) / 5,050 invested at Monday's close
        Assert.Equal(["ERIC B (split-like move)", "AAPL (no price at a close)", "TELIA (not on the list on 2026-09-28)"], wednesday.LeftOut);

        // Without dividends VOLV's ex-day reads as its −2 %.
        Assert.Equal(0.016667m, HoldTheList.Days(Reports(), null)[1].Return); // (2 % − 2 % + 5 %) / 3
    }

    [Fact]
    public void TheComparison_CompoundsBothSides_AndScalesTheListByPapersExposure()
    {
        PaperVsList c = HoldTheList.Compare(HoldTheList.Days(Reports(), Dividends))!;

        Assert.Equal((3, 0.02m, 0.033775m), (c.Days, c.PaperReturn, c.ListReturn)); // 1.01 × 1.009901; 1.023333 × 1.010204
        Assert.Equal((0.5017m, 0.01688m), (c.Exposure, c.ListAtExposure)); // (0.5 + 0.5 + 0.505) / 3; 1 × 1.0116665 × 1.0051530
        Assert.Equal(0.00312m, c.Difference);
        Assert.Null(c.TStat); // 3 days: too few
        Assert.Equal("3 day(s): Paper +2.00%; the list +3.38% (at Paper's 50 % invested +1.69%): Paper 0.31 points ahead at the same exposure", c.Describe());
        Assert.Null(HoldTheList.Compare([]));
    }

    [Fact]
    public void FromFiveDays_ItSaysWhetherTheDifferenceIsMoreThanNoise()
    {
        ListDay D(int i, decimal paper) => new(new DateOnly(2026, 9, 28).AddDays(i), new DateOnly(2026, 9, 27).AddDays(i), paper, 0m, 1m, 3, []);

        PaperVsList ahead = HoldTheList.Compare([D(0, 0.01m), D(1, 0.02m), D(2, 0m), D(3, 0.01m), D(4, 0.02m)])!;
        Assert.Equal(3.21, ahead.TStat); // mean 1.2 %, sd 0.84 %, √5
        Assert.EndsWith("ahead at the same exposure (t +3.2: more than noise)", ahead.Describe(), StringComparison.Ordinal);

        PaperVsList noise = HoldTheList.Compare([D(0, 0.01m), D(1, -0.02m), D(2, 0m), D(3, 0.01m), D(4, -0.01m)])!;
        Assert.EndsWith("behind at the same exposure (t -0.3: not yet more than noise)", noise.Describe(), StringComparison.Ordinal); // mean −0.2 %, sd 1.3 %
        Assert.Null(HoldTheList.Compare([D(0, 0.01m), D(1, 0.01m), D(2, 0.01m), D(3, 0.01m), D(4, 0.01m)])!.TStat); // no spread
    }

    [Fact]
    public void TheWeek_ComparesPaperWithHoldingTheList_ThisWeekAndSinceTheStart()
    {
        DateOnly monday = new(2026, 9, 28);
        DateOnly[] trading = [.. Enumerable.Range(0, 5).Select(monday.AddDays)];
        GateResult gate = new(false, [], []);

        WeeklyReport w = WeeklyReport.Build(monday, trading, Reports(), gate, null, null, null, Now, Dividends);

        Assert.Equal(2, w.HoldThisWeek!.Days);
        Assert.Equal((new DateOnly(2026, 9, 25), 3), (w.HoldFirstDay, w.HoldSinceStart!.Days));
        Assert.Null(w.HoldDividendsMissing);
        IReadOnlyList<string> lines = w.Lines();
        int at = lines.ToList().FindIndex(l => l.StartsWith("Against holding the list", StringComparison.Ordinal));
        Assert.Equal("Against holding the list (equal weights, dividends included, no costs; the same close prices as Paper):", lines[at]);
        Assert.StartsWith("  this week: 2 day(s): Paper +2.00%; the list +3.38%", lines[at + 1], StringComparison.Ordinal);
        Assert.StartsWith("  since 2026-09-25: 3 day(s): Paper +2.00%", lines[at + 2], StringComparison.Ordinal);
        Assert.Equal("  left out this week: Wed ERIC B (split-like move); Wed AAPL (no price at a close); Wed TELIA (not on the list on 2026-09-28)", lines[at + 3]);
        Assert.StartsWith("Limits this week:", lines[at + 4], StringComparison.Ordinal);

        WeeklyReport noDividends = WeeklyReport.Build(monday, trading, Reports(), gate, null, null, null, Now);
        Assert.Contains("(equal weights, dividends left out (not read), no costs;", noDividends.Lines().Single(l => l.StartsWith("Against holding", StringComparison.Ordinal)), StringComparison.Ordinal);

        // Plan 24 B: the market index over the same intervals (Thu→Fri, Fri→Mon, Mon→Wed; Wednesday's close is missing).
        var closes = new Dictionary<DateOnly, decimal> { [new DateOnly(2026, 9, 24)] = 2000m, [new DateOnly(2026, 9, 25)] = 2010m, [new DateOnly(2026, 9, 28)] = 2030.1m };
        WeeklyReport withIndex = WeeklyReport.Build(monday, trading, Reports(), gate, null, null, null, Now, Dividends, ("OMX Stockholm 30", closes));
        Assert.Equal(new BenchmarkReturn(1, 0.01m, 1), withIndex.BenchmarkThisWeek); // Fri→Mon +1 %; Mon→Wed has no Wednesday close
        Assert.Equal(new BenchmarkReturn(2, 0.01505m, 1), withIndex.BenchmarkSinceStart); // 1.005 × 1.01
        Assert.Contains("  the market (OMX Stockholm 30) over the same days: this week +1.00% (1 day(s), 1 without closes), since 2026-09-25 +1.51% (2 day(s), 1 without closes)",
            withIndex.Lines());
        Assert.Contains("  the market (OMX Stockholm 30): no closes stored for these days yet ('qa benchmark import')",
            WeeklyReport.Build(monday, trading, Reports(), gate, null, null, null, Now, Dividends, ("OMX Stockholm 30", new Dictionary<DateOnly, decimal>())).Lines());

        WeeklyReport firstWeek = WeeklyReport.Build(monday, trading, [Reports()[0]], gate, null, null, null, Now, Dividends);
        Assert.Null(firstWeek.HoldSinceStart);
        Assert.Contains("Against holding the list: from the second Paper day with close prices (each day runs from the Paper close before it).", firstWeek.Lines());
    }
}
