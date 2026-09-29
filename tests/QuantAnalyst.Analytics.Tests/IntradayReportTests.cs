using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Tests;

/// <summary>
/// Plan 17 step A6: the go/no-go report on hand-made 5-minute bars. Share A breaks out of its opening range at 10:00
/// and trends up into the close (a real edge for orb-long); share B falls all day, so buying everything (open-close)
/// loses. The report's runs are logged, the candidate is re-costed at Mini, and the holdout is checked only when given.
/// </summary>
public sealed class IntradayReportTests : IDisposable
{
    private const string HoldoutLabel = "test-intraday-holdout";

    private static readonly TickSizeTable Cents = new([new TickSizeBand(0m, 99_999m, 0.01m)]);

    private static readonly DataSourceInfo Source = new("test-intraday", false, false, "hand-made 5-minute bars");

    private static readonly MarketCalendar Calendar = new([new CalendarYear(
        "XSTO", 2025, new TimeOnly(9, 0), new TimeOnly(17, 30), new TimeOnly(9, 0), new TimeOnly(13, 0), [], [], "https://example.invalid/calendar", null)]);

    private static readonly IntradayClock Clock = new(Calendar, TimeSpan.FromMinutes(5));

    private static readonly CostModel Mini = BacktestFixtures.Costs with { Name = "test-mini", DisplayName = "Mini", CourtageMin = 1m, CourtageRate = 0.0025m, FxFeeRate = 0m };

    private static readonly CostModel Start = BacktestFixtures.Costs with
    {
        Name = "test-start",
        DisplayName = "Start",
        CourtageMin = 0m,
        CourtageRate = 0m,
        FxFeeRate = 0m,
        FreeTrades = new FreeTradeAllowance(500, 12, Mini, null, null),
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qa-intraday-report", Guid.NewGuid().ToString("N"));

    public IntradayReportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static DateOnly[] TradingDays(int count)
    {
        var days = new List<DateOnly>();
        for (DateOnly d = new(2025, 1, 2); days.Count < count; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days.Add(d);
            }
        }

        return [.. days];
    }

    /// <summary>
    /// A: 100 until 10:00 (small bumps at 09:10 and 09:25 make the 5-, 15- and 30-minute ranges differ), then a jump
    /// that varies by day and, with <paramref name="trend"/>, a climb into the close. B: 50 falling 0.05 a bar. 09:00 to
    /// 17:25 every day.
    /// </summary>
    private static MarketPanel Panel(DateOnly[] days, bool trend = true)
    {
        var a = new List<Bar>();
        var b = new List<Bar>();
        for (int d = 0; d < days.Length; d++)
        {
            DateTimeOffset open = Calendar.ToUtc(days[d], new TimeOnly(9, 0));
            double jump = new[] { 0.1, 0.2, 0.6, 1.0 }[d % 4], slope = trend ? 0.01 * (1 + (d % 5)) : 0;
            double previousA = 100, previousB = 50;
            for (int k = 0; k < 102; k++)
            {
                double levelA = k switch { 2 => 100.1, 5 => 100.3, < 12 => 100, _ => 100 + jump + ((k - 12) * slope) };
                double levelB = 50 - (0.05 * k);
                a.Add(Make(open.AddMinutes(5 * k), previousA, levelA));
                b.Add(Make(open.AddMinutes(5 * k), previousB, levelB));
                (previousA, previousB) = (levelA, levelB);
            }
        }

        return MarketPanel.FromIntradayBars([(new PanelInstrument("A", 1, false, Cents), a), (new PanelInstrument("B", 1, false, Cents), b)], Source);

        static Bar Make(DateTimeOffset start, double open, double close)
        {
            decimal o = Math.Round((decimal)open, 2), c = Math.Round((decimal)close, 2);
            return new Bar(start, o, Math.Max(o, c) + 0.05m, Math.Min(o, c) - 0.05m, c, 1_000_000);
        }
    }

    private IntradayReportRequest Request(MarketPanel data, HoldoutPolicy holdout, MarketPanel? held = null) => new()
    {
        Template = BacktestFixtures.Request(data, IntradayStrategyCatalog.Create("orb-long", new Dictionary<string, string>(), Clock), new TrialLedger(Path.Combine(_dir, "ledger.jsonl"))) with
        {
            Costs = Start,
            InitialCash = 10_000m,
            Holdout = holdout,
            Execution = new ExecutionOptions { OrderType = BacktestOrderType.MarketOnOpen },
        },
        Clock = Clock,
        HoldoutData = held,
    };

    private static HoldoutPolicy LockedAfter(DateOnly[] days) => new(true, days[^1].AddDays(1), HoldoutLabel);

    private static double MiniCourtage(TimedFill f) => Math.Max(1.0, 0.0025 * f.Fill.Quantity * f.Fill.Price);

    private TrialRecord[] Logged() => [.. new TrialLedger(Path.Combine(_dir, "ledger.jsonl")).ReadAll()];

    [Fact]
    public void OnARealEdge_EveryCriterionPasses_TheHoldoutToo_AndTheOwnerDecides()
    {
        DateOnly[] days = TradingDays(160);
        MarketPanel all = Panel(days);
        var unlocked = new HoldoutPolicy(false, days[140], HoldoutLabel);
        IntradayReportResult r = IntradayReport.Run(Request(all.Between(days[0], days[139]), unlocked, all.Between(days[140], days[^1])));

        Assert.All(r.Criteria, c => Assert.True(c.Status == CriterionStatus.Pass, $"{c.Name}: {c.Detail}"));
        Assert.StartsWith("GO on the proposed bar. The decision is yours", r.Verdict, StringComparison.Ordinal);
        Assert.Equal(140, r.Days);
        Assert.Equal("orb-long range=5", IntradayReport.Describe(r.Candidate!)); // the earliest range catches the most of the trend
        Assert.Equal(2.0, r.FillsPerDay); // in and out once a day: never resized
        Assert.Equal((140, 140), (r.Trades!.RoundTrips, r.Trades.Days));
        Assert.True(r.Trades.ClusteredT > 2, $"t {r.Trades.ClusteredT}");
        Assert.True(r.OpenCloseAfter!.TotalReturn < 0 && r.CandidateAfter!.TotalReturn > 0);
        Assert.Equal(80, r.WalkForward!.OutOfSampleDays); // 60 to start, then four blocks of 20 (140 days)
        Assert.Equal(20, r.Holdout!.Days);

        // Seven runs logged: the grid, the two controls, and the two on the holdout, which alone touch it.
        TrialRecord[] logged = Logged();
        Assert.Equal([false, false, false, false, false, true, true], logged.Select(t => t.HoldoutTouched));
        Assert.Equal(r.Candidate!.Record.Parameters, logged[5].Parameters);
    }

    [Fact]
    public void WithTheHoldoutLocked_ItPassesSoFar_AndNeverReadsIt()
    {
        DateOnly[] days = TradingDays(140);
        IntradayReportResult r = IntradayReport.Run(Request(Panel(days), LockedAfter(days)));

        Criterion last = r.Criteria[^1];
        Assert.Equal(("holds on the holdout", CriterionStatus.Wait), (last.Name, last.Status));
        Assert.Contains("the owner unlocks it", last.Detail, StringComparison.Ordinal);
        Assert.StartsWith("PASSES SO FAR", r.Verdict, StringComparison.Ordinal);
        Assert.Null(r.Holdout);
        Assert.Equal(5, Logged().Length);
        Assert.DoesNotContain(Logged(), t => t.HoldoutTouched);
    }

    [Fact]
    public void WithFewDays_ItSaysNotYet_WhateverTheRest()
    {
        DateOnly[] days = TradingDays(30);
        IntradayReportResult r = IntradayReport.Run(Request(Panel(days), LockedAfter(days)));

        Assert.Equal((CriterionStatus.Wait, "30 before the holdout"), (r.Criteria[0].Status, r.Criteria[0].Detail));
        Assert.Equal(CriterionStatus.Wait, r.Criteria.Single(c => c.Name.StartsWith("PBO", StringComparison.Ordinal)).Status); // 30 < 40 days
        Assert.Null(r.WalkForward); // 30 < 60 + 20 days
        Assert.StartsWith("NOT YET", r.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnEdge_ItIsANoGo()
    {
        // The same breakouts without the climb: after the spread and Mini's courtage the trades lose.
        DateOnly[] days = TradingDays(125);
        IntradayReportResult r = IntradayReport.Run(Request(Panel(days, trend: false), LockedAfter(days)));

        Criterion perTrade = r.Criteria.Single(c => c.Name.StartsWith("net per trade", StringComparison.Ordinal));
        Assert.True(perTrade.Status == CriterionStatus.Fail, perTrade.Detail);
        Assert.StartsWith("NO-GO", r.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void Recosting_ChargesTheOtherClassOnEveryFill_WithoutRunningAgain()
    {
        DateOnly[] days = TradingDays(10);
        BacktestRequest request = Request(Panel(days), LockedAfter(days)).Template;
        BacktestResult free = BacktestRunner.Run(request);
        Assert.True(free.Ok, free.Record.Note);

        RecostedRun mini = BacktestRunner.Recost(request, free, Mini);
        double expected = free.Fills.Sum(MiniCourtage);
        Assert.Equal(expected, mini.Courtage, 1e-9);
        Assert.Equal(free.Equity[^1] - expected, mini.Equity[^1], 1e-6);
        Assert.Equal(10, mini.DailyReturns.Count);
        Assert.Equal(free.Equity[^1], BacktestRunner.Recost(request, free, Start).Equity[^1], 1e-9); // its own class: unchanged

        // An allowance that ran out changed only the equity, not the trades; at Mini both runs are the same.
        BacktestRequest shortAllowance = request with { Costs = Start with { FreeTrades = new FreeTradeAllowance(4, 12, Mini, null, null) } };
        BacktestResult ranOut = BacktestRunner.Run(shortAllowance);
        Assert.True(ranOut.AllowanceCourtage > 0);
        Assert.Equal(mini.Equity[^1], BacktestRunner.Recost(shortAllowance, ranOut, Mini).Equity[^1], 1e-6);
        Assert.Equal(2, Logged().Length); // the two runs; recosting logs nothing, since it runs nothing
    }

    [Fact]
    public void PerTrade_IsTheRoundTripNetOfCourtage_ClusteredByDay()
    {
        DateOnly[] days = TradingDays(1);
        MarketPanel data = Panel(days);
        BacktestResult r = BacktestRunner.Run(Request(data, LockedAfter(days)).Template);
        TimedFill buy = r.Fills.Single(f => f.Fill.Side == BacktestSide.Buy), sell = r.Fills.Single(f => f.Fill.Side == BacktestSide.Sell);

        TradeStatistics t = IntradayReport.Trades(data, r.Fills, Mini)!;
        double net = (((sell.Fill.Quantity * sell.Fill.Price) - MiniCourtage(sell)) / ((buy.Fill.Quantity * buy.Fill.Price) + MiniCourtage(buy))) - 1;
        Assert.Equal((1, 1), (t.RoundTrips, t.Days));
        Assert.Equal(net, t.Mean, 1e-12);
        Assert.True(double.IsNaN(t.ClusteredT)); // one day is one cluster: no standard error
    }
}
