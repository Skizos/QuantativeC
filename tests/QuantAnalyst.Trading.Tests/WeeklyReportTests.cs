using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Data.History;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Reports;

namespace QuantAnalyst.Trading.Tests;

/// <summary>Plan 20: the weekly summary, Paper against the recorded backtest.</summary>
public sealed class WeeklyReportTests
{
    private static readonly DateOnly Monday = new(2026, 9, 28);
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly StrategySpec Strategy = new("ma-cross", new Dictionary<string, string> { ["fast"] = "20", ["slow"] = "100" });

    /// <summary>A Paper day that ran from <paramref name="start"/> to <paramref name="end"/> SEK with <paramref name="cash"/> left in cash.</summary>
    private static EodReport Day(DateOnly date, decimal start, decimal end, decimal cash, bool clean = true) =>
        PromotionGateTests.Day(0, clean) with
        {
            Date = date,
            Account = new EodAccount(start, end, end - start, decimal.Round((end - start) / start, 6), cash, 1m),
        };

    private static TrialRecord Trial(long sequence, string[] universe, double sharpePerDay = 0.05, double vol = 0.16, string source = "avanza-price-chart",
        TrialStatus status = TrialStatus.Ok, string slow = "100") => new()
        {
            Sequence = sequence,
            Id = $"T{sequence:000000}",
            RecordedAtUtc = Now,
            Runner = "owner",
            Study = "s",
            Strategy = "ma-cross",
            Parameters = new Dictionary<string, string> { ["fast"] = "20", ["slow"] = slow },
            Universe = universe,
            DataSource = source,
            PointInTime = false,
            SurvivorshipFree = false,
            From = new DateOnly(2016, 1, 4),
            To = new DateOnly(2026, 9, 25),
            Seed = 1,
            CostModel = "avanza-start",
            CostsVerified = true,
            HoldoutTouched = false,
            Status = status,
            Metrics = new TrialMetrics(500, sharpePerDay, sharpePerDay * Math.Sqrt(252), 0, 3, 0.1, 0.05, vol, 0.2, 1, 100, 0.9, null, 1, null),
        };

    private static GateResult Gate(int clean) => new(false, [], [.. Enumerable.Range(0, clean).Select(i => PromotionGateTests.Day(i))]);

    [Fact]
    public void TheExpectation_IsTheBacktestsAverageDay_ScaledByTheShareInvested_WithA95PercentRange()
    {
        BacktestExpectation b = BacktestExpectation.Of(Trial(1, ["ERIC B"]), sameList: true);
        double sd = 0.16 / Math.Sqrt(252);
        Assert.Equal(sd, b.StdDaily, 12);
        Assert.Equal(0.05 * sd, b.MeanDaily, 12);

        PaperVsBacktest week = b.Compare(4, -0.002m, 0.5m);
        double half = 1.96 * 0.5 * sd * 2; // √4
        Assert.Equal(0.5 * 4 * 0.05 * sd, week.Expected, 12);
        Assert.Equal((week.Expected - half, week.Expected + half), (week.Low, week.High));
        Assert.Equal("within the range", week.Verdict);

        Assert.StartsWith("BELOW the range", b.Compare(4, -0.05m, 0.5m).Verdict, StringComparison.Ordinal);
        Assert.Equal("above the range", b.Compare(4, 0.05m, 0.5m).Verdict);
        Assert.StartsWith("not invested", b.Compare(4, -0.001m, 0.005m).Verdict, StringComparison.Ordinal);
        Assert.Equal("4 day(s) at 50 % invested: Paper -0.20%; the backtest expects +0.10% (95 % range -1.87% to +2.08%): within the range", week.Describe());
    }

    [Fact]
    public void TheTrial_IsTheLatestOfTheSameStrategyOnImportedData_PreferringTodaysList()
    {
        TrialRecord[] trials =
        [
            Trial(1, ["ERIC B", "VOLV B"]),
            Trial(2, ["ERIC B"]),
            Trial(3, ["ERIC B", "VOLV B"], source: SyntheticMarket.SourceName), // synthetic: never
            Trial(4, ["ERIC B", "VOLV B"], status: TrialStatus.Failed),
            Trial(5, ["ERIC B", "VOLV B"], slow: "200"), // other parameters
        ];

        BacktestExpectation? onList = BacktestExpectation.Find(trials, Strategy, ["VOLV-B", "eric b"]);
        Assert.Equal(("T000001", true), (onList!.TrialId, onList.SameList));

        BacktestExpectation? other = BacktestExpectation.Find(trials, Strategy, ["SAND"]);
        Assert.Equal(("T000002", false), (other!.TrialId, other.SameList)); // the latest, flagged as another list

        Assert.Null(BacktestExpectation.Find(trials, new StrategySpec("buy-and-hold", new Dictionary<string, string>()), ["ERIC B"]));
        Assert.Equal("T000001, ma-cross(fast=20, slow=100) on ERIC B, VOLV B, 2016-01-04..2026-09-25, costs avanza-start (its average day +0.050%, sd 1.01%)", onList.Describe());
    }

    [Fact]
    public void TheWeek_ShowsEveryTradingDay_ComparesPaper_AndCountsSinceTheStart()
    {
        EodReport[] reports =
        [
            Day(Monday.AddDays(-3), 5000m, 5005m, 5005m), // the Friday before: since the start, not this week
            Day(Monday, 5000m, 5010m, 2500m),
            Day(Monday.AddDays(1), 5010m, 4990m, 2490m, clean: false),
        ];
        DateOnly[] trading = [Monday, Monday.AddDays(1), Monday.AddDays(2)];
        var intraday = new IntradayCoverage(trading, [new IntradayShareCoverage("ERIC B", 1, 1, [Monday.AddDays(2)])], 12);
        BacktestExpectation b = BacktestExpectation.Of(Trial(7, ["ERIC B"]), sameList: false);

        WeeklyReport w = WeeklyReport.Build(Monday.AddDays(4), trading, reports, Gate(1), b, intraday, 140, Now);

        Assert.Equal(("2026-W40", Monday, 3, 1, 1), (w.Week, w.Monday, w.TradingDays, w.CleanDays, w.NoSessionDays));
        Assert.Equal(["CLEAN", "NOT CLEAN", "no session"], w.Days.Select(d => d.State));
        Assert.Equal(-0.002m, w.Return); // 1.002 × (1 − 0.003992) − 1
        Assert.Equal((4990m, 2m), (w.EndValue, w.Fees));
        Assert.Equal(0.501m, w.ThisWeek!.Invested);
        Assert.Equal((2, -0.002m), (w.ThisWeek.Days, w.ThisWeek.PaperReturn));
        Assert.Equal((Monday.AddDays(-3), 3), (w.FirstPaperDay, w.SinceStart!.Days));
        Assert.Equal(0.334m, w.SinceStart.Invested); // (0 + 0.501 + 0.501) / 3

        IReadOnlyList<string> lines = w.Lines();
        Assert.Equal("Week 2026-W40, Mon 2026-09-28 to Sun 2026-10-04", lines[0]);
        Assert.Equal("  Mon 2026-09-28  CLEAN          +0.20%  invested 50 %  1 sent, 0 fill(s)", lines[1]);
        Assert.Equal("  Wed 2026-09-30  no session", lines[3]);
        Assert.Equal("Week: -0.20% (value 4,990.00 SEK), fees 2.00 SEK; 1 of 3 trading day(s) clean, 1 without a session; Confirm gate: 1 of 10 clean Paper days in a row.", lines[4]);
        Assert.StartsWith("Against the backtest T000007, ma-cross(fast=20, slow=100) on ERIC B", lines[5], StringComparison.Ordinal);
        Assert.StartsWith("  this week: 2 day(s) at 50 % invested: Paper -0.20%", lines[6], StringComparison.Ordinal);
        Assert.StartsWith("  since 2026-09-25: 3 day(s) at 33 % invested", lines[7], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("different list than today's allowlist", StringComparison.Ordinal));
        Assert.Equal("Limits this week: no orders.", lines[^2]);
        Assert.Equal("Intraday bars, 3 trading day(s): ERIC B 2 (1 at 10 minutes) (missing 09-30); 12 day(s) collected so far of the 140 the go/no-go needs.", lines[^1]);
    }

    [Fact]
    public void WithoutABacktestOrPaperDays_ItSaysSo_AndItRoundTrips()
    {
        using var dir = new TempDir();
        WeeklyReport w = WeeklyReport.Build(Monday, [Monday], [], Gate(0), null, null, null, Now);
        Assert.Null(w.Return);
        Assert.Contains("Week: no Paper day with values, fees 0.00 SEK; 0 of 1 trading day(s) clean, 1 without a session", w.Lines()[2], StringComparison.Ordinal);
        Assert.StartsWith("Against the backtest: no successful backtest of the saved strategy", w.Lines()[3], StringComparison.Ordinal);

        string path = w.Save(dir.File("week"));
        Assert.EndsWith("2026-W40.json", path, StringComparison.Ordinal);
        Assert.Equal(w.Lines(), WeeklyReport.Load(path).Lines());
    }

    [Theory]
    [InlineData("2026-W40", "2026-09-28")]
    [InlineData("2026-w01", "2025-12-29")] // ISO week 1 of 2026 starts in 2025
    [InlineData("2026-W53", "2026-12-28")] // 2026 has 53 ISO weeks
    [InlineData("2026-W54", null)]
    [InlineData("2026-40", null)]
    public void AnIsoWeek_ParsesToItsMonday(string week, string? monday)
    {
        if (monday is null)
        {
            Assert.Throws<ArgumentException>(() => WeeklyReport.ParseWeek(week));
            return;
        }

        DateOnly m = DateOnly.Parse(monday, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(m, WeeklyReport.ParseWeek(week));
        Assert.Equal(m, WeeklyReport.MondayOf(m.AddDays(6)));
        Assert.Equal(week.ToUpperInvariant(), WeeklyReport.WeekName(m.AddDays(3)));
    }
}
