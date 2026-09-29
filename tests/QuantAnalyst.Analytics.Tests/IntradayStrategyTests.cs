using System.Globalization;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Tests;

/// <summary>
/// Plan 17 step A5: the intraday strategies (orb-long, late-momentum, open-close) on hand-made 5-minute bars. Each
/// decision is at a bar's close and trades at the next bar's open; one entry per name per day; flat from the exit time
/// on, which follows the calendar's close (half days too); the strongest signals take the free slots.
/// </summary>
public sealed class IntradayStrategyTests
{
    private static readonly TickSizeTable Cents = new([new TickSizeBand(0m, 99_999m, 0.01m)]);

    private static readonly DataSourceInfo Source = new("test-intraday", false, false, "hand-made 5-minute bars");

    // March 2025, before the fixtures' locked holdout. Friday 7 March is a (made-up) half day closing 13:00.
    private static readonly DateOnly Monday = new(2025, 3, 3);

    private static readonly DateOnly HalfDay = new(2025, 3, 7);

    private static readonly MarketCalendar Calendar = new([new CalendarYear(
        "XSTO", 2025, new TimeOnly(9, 0), new TimeOnly(17, 30), new TimeOnly(9, 0), new TimeOnly(13, 0),
        [], [new CalendarEntry(HalfDay, "test half day")], "https://example.invalid/calendar", null)]);

    private static readonly IntradayClock Clock = new(Calendar, TimeSpan.FromMinutes(5));

    private static readonly CostModel Free = BacktestFixtures.Costs with { CourtageMin = 0m, CourtageRate = 0m, FxFeeRate = 0m };

    /// <summary>
    /// 5-minute bars from 09:00 to the close on each day for each share; <paramref name="close"/> gives the close of bar k
    /// (k = 0 at 09:00) of share i on day d, the open is the previous close, and high/low are 0.1 outside them. A null
    /// close is a bar the share did not trade; <paramref name="open"/> can gap a bar's open away from the previous close.
    /// </summary>
    private static MarketPanel Panel(DateOnly[] days, Func<int, int, int, double?> close, int instruments = 1, Func<int, int, int, double?>? open = null)
    {
        var series = new List<(PanelInstrument, IReadOnlyList<Bar>)>();
        for (int i = 0; i < instruments; i++)
        {
            var bars = new List<Bar>();
            for (int d = 0; d < days.Length; d++)
            {
                TradingDay day = Calendar.Classify(days[d]);
                DateTimeOffset first = Calendar.ToUtc(days[d], day.Open!.Value), end = Calendar.ToUtc(days[d], day.Close!.Value);
                double previous = close(i, d, 0) ?? 100;
                for (int k = 0; first.AddMinutes(5 * k) < end; k++)
                {
                    if (close(i, d, k) is not { } c)
                    {
                        continue;
                    }

                    decimal o = (decimal)(open?.Invoke(i, d, k) ?? previous), cl = (decimal)c;
                    bars.Add(new Bar(first.AddMinutes(5 * k), o, Math.Max(o, cl) + 0.1m, Math.Min(o, cl) - 0.1m, cl, 1_000_000));
                    previous = c;
                }
            }

            series.Add((new PanelInstrument(((char)('A' + i)).ToString(), 1, false, Cents), bars));
        }

        return MarketPanel.FromIntradayBars(series, Source);
    }

    private static int K(int hour, int minute) => (((hour - 9) * 60) + minute) / 5;

    private static BacktestResult Run(MarketPanel data, string name, params (string Key, string Value)[] parameters) =>
        BacktestRunner.Run(BacktestFixtures.Request(data, IntradayStrategyCatalog.Create(name, parameters.ToDictionary(p => p.Key, p => p.Value), Clock)) with
        {
            Costs = Free,
            InitialCash = 10_000m,
            Execution = new ExecutionOptions { OrderType = BacktestOrderType.MarketOnOpen },
        });

    /// <summary>"03-03 10:05 buy A 9": each fill's bar (Stockholm), side, share and quantity.</summary>
    private static string[] Fills(BacktestResult r, MarketPanel data) =>
        [.. r.Fills.Select(f => string.Create(CultureInfo.InvariantCulture,
            $"{MarketTime.ToStockholm(data.BarStartsUtc![f.Bar]):MM-dd HH:mm} {(f.Fill.Side == BacktestSide.Buy ? "buy" : "sell")} {data.Instruments[f.Fill.Instrument].Symbol} {f.Fill.Quantity}"))];

    [Fact]
    public void OrbLong_BuysTheBreakoutAtTheNextOpen_AndSellsAt1710()
    {
        // Flat at 100 through the range (09:05-09:20: high 100.1); the 10:00 bar closes at 101, above it.
        MarketPanel data = Panel([Monday], (_, _, k) => k >= K(10, 0) ? 101 : 100);
        BacktestResult r = Run(data, "orb-long");

        Assert.True(r.Ok, r.Record.Note);
        // 10 % of 99 % of 10,000 at 101: 9 shares.
        Assert.Equal(["03-03 10:05 buy A 9", "03-03 17:10 sell A 9"], Fills(r, data));
        Assert.Equal(new Dictionary<string, string> { ["buffer"] = "0", ["exit"] = "20", ["range"] = "15", ["skip"] = "5", ["weight"] = "0.1" }, r.Record.Parameters);
    }

    [Fact]
    public void OrbLong_TheRangeStartsAfterSkip_AndABufferRaisesTheBar()
    {
        // The auction's bar (09:00) spikes to 105, then 09:05 opens at 100: the spike is not in the range; 100.2 at 10:00
        // breaks the range high 100.1 by 10 bps.
        double? Close(int i, int d, int k) => k == 0 ? 105 : k >= K(10, 0) ? 100.2 : 100;
        MarketPanel data = Panel([Monday], Close, open: (_, _, k) => k == 1 ? 100 : null);

        Assert.Equal(["03-03 10:05 buy A 9", "03-03 17:10 sell A 9"], Fills(Run(data, "orb-long"), data));
        Assert.Empty(Run(data, "orb-long", ("buffer", "20")).Fills); // needs 100.1 × 1.002 = 100.3
        Assert.Empty(Run(data, "orb-long", ("skip", "0")).Fills);    // with the auction's bar the range high is 105.1
    }

    [Fact]
    public void OrbLong_StopsBelowTheRangeLow_AndEntersOncePerDay_AgainTheNextDay()
    {
        // Day 1: breakout at 10:00, a close under the range low (99.9) at 11:00, a second breakout at 12:00 (ignored).
        // Day 2: breakout at 10:00 again: a new day, a new entry.
        double? Close(int i, int d, int k) => d == 1
            ? (k >= K(10, 0) ? 101 : 100)
            : k >= K(12, 0) ? 102 : k >= K(11, 0) ? 99 : k >= K(10, 0) ? 101 : 100;
        MarketPanel data = Panel([Monday, Monday.AddDays(1)], Close);
        BacktestResult r = Run(data, "orb-long");

        Assert.True(r.Ok, r.Record.Note);
        Assert.Equal(["03-03 10:05 buy A 9", "03-03 11:05 sell A 9", "03-04 10:05 buy A 9", "03-04 17:10 sell A 9"], Fills(r, data));
        Assert.Equal(2, r.DailyReturns.Count); // the statistics and PBO are per day, not per bar
        Assert.Equal(r.Equity[^1] / 10_000 - 1, ((1 + r.DailyReturns[0]) * (1 + r.DailyReturns[1])) - 1, 1e-12);
    }

    [Fact]
    public void OrbLong_WhenMoreBreakOutThanFit_TheStrongestGoIn()
    {
        // weight 0.5: two slots. At 10:00 A closes at 101, B at 103, C at 102: B and C go in, A never (one chance a day
        // is not used up by waiting: A stays waiting, and a slot never frees before the exit).
        MarketPanel data = Panel([Monday], (i, _, k) => k >= K(10, 0) ? 101 + (i == 1 ? 2 : i == 2 ? 1 : 0) : 100, instruments: 3);
        BacktestResult r = Run(data, "orb-long", ("weight", "0.5"));

        Assert.True(r.Ok, r.Record.Note);
        Assert.Equal(["03-03 10:05 buy B", "03-03 10:05 buy C", "03-03 17:10 sell B", "03-03 17:10 sell C"], [.. Fills(r, data).Select(f => f[..f.LastIndexOf(' ')]).Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void LateMomentum_BuysAt1630WhenTheFirstHourWasUp_AndSellsAt1715()
    {
        // A is up 1 % in the first hour, B down 1 %.
        MarketPanel data = Panel([Monday], (i, _, k) => k == 0 ? 100 : k < K(10, 0) ? (i == 0 ? 101 : 99) : 100, instruments: 2);
        BacktestResult r = Run(data, "late-momentum");

        Assert.True(r.Ok, r.Record.Note);
        Assert.Equal(["03-03 16:30 buy A 9", "03-03 17:15 sell A 9"], Fills(r, data));
        Assert.Empty(Run(data, "late-momentum", ("threshold", "150")).Fills); // 1 % is not more than 1.5 %
    }

    [Fact]
    public void OpenClose_BuysEveryNameAt0910_AndSellsAt1710_EachWithAtMostOneNth()
    {
        MarketPanel data = Panel([Monday], (_, _, _) => 100, instruments: 2);
        BacktestResult r = Run(data, "open-close", ("weight", "0.8"));

        Assert.True(r.Ok, r.Record.Note);
        // min(0.8, 1/2) of 99 % of 10,000 at 100: 49 each.
        Assert.Equal(["03-03 09:10 buy A 49", "03-03 09:10 buy B 49", "03-03 17:10 sell A 49", "03-03 17:10 sell B 49"], Fills(r, data));
    }

    [Fact]
    public void APosition_IsEnteredAndLeftWhole_NeverResizedAsItsPriceMoves()
    {
        // 9 shares at 100; the price then climbs 0.2 a bar to about 117, so 10 % of equity would be 8 shares, then 7.
        // The daily no-trade band would sell a share each time; intraday, the position stays as it was bought.
        MarketPanel data = Panel([Monday], (_, _, k) => k >= K(9, 10) ? 100 + (0.2 * (k - K(9, 10))) : 100);
        BacktestResult r = Run(data, "open-close", ("weight", "0.1"));

        Assert.True(r.Ok, r.Record.Note);
        Assert.Equal(["03-03 09:10 buy A 9", "03-03 17:10 sell A 9"], Fills(r, data));
    }

    /// <summary>One day of 10-minute bars (a caught-up missed day, A2b): 100 until 10:00, then 101.</summary>
    private static MarketPanel TenMinuteDay()
    {
        DateTimeOffset open = Calendar.ToUtc(Monday, new TimeOnly(9, 0));
        var bars = new List<Bar>();
        decimal previous = 100m;
        for (int k = 0; k < 51; k++)
        {
            decimal close = k >= 6 ? 101m : 100m;
            bars.Add(new Bar(open.AddMinutes(10 * k), previous, Math.Max(previous, close) + 0.1m, Math.Min(previous, close) - 0.1m, close, 1_000_000));
            previous = close;
        }

        return MarketPanel.FromIntradayBars([(new PanelInstrument("A", 1, false, Cents), bars)], Source);
    }

    private static BacktestResult RunWith(IntradayClock clock, MarketPanel data, string name, params (string Key, string Value)[] parameters) =>
        BacktestRunner.Run(BacktestFixtures.Request(data, IntradayStrategyCatalog.Create(name, parameters.ToDictionary(p => p.Key, p => p.Value), clock)) with
        {
            Costs = Free,
            InitialCash = 10_000m,
            Execution = new ExecutionOptions { OrderType = BacktestOrderType.MarketOnOpen },
        });

    [Fact]
    public void OnATenMinuteDay_TheClockKeepsTheRealTimes()
    {
        MarketPanel data = TenMinuteDay();
        var tenMinutes = new IntradayClock(Calendar, TimeSpan.FromMinutes(5), new Dictionary<DateOnly, TimeSpan> { [Monday] = TimeSpan.FromMinutes(10) });

        // The 09:00 bar closes at 09:10 and the 17:00 bar at 17:10: in at 09:10, out at 17:10, as on a 5-minute day.
        Assert.Equal(["03-03 09:10 buy A 9", "03-03 17:10 sell A 9"], Fills(RunWith(tenMinutes, data, "open-close"), data));

        // Taken for 5-minute bars, every decision would be 5 minutes early: in at 09:20, out at 17:20.
        Assert.Equal(["03-03 09:20 buy A 9", "03-03 17:20 sell A 9"], Fills(RunWith(Clock, data, "open-close"), data));

        // orb-long: the 15-minute range from 09:05 holds only the 09:10 bar; a 5-minute range can't form at all.
        Assert.Equal(["03-03 10:10 buy A 9", "03-03 17:10 sell A 9"], Fills(RunWith(tenMinutes, data, "orb-long"), data));
        Assert.Empty(RunWith(tenMinutes, data, "orb-long", ("range", "5")).Fills);
    }

    [Fact]
    public void OnAHalfDay_TheExitFollowsTheEarlyClose()
    {
        MarketPanel data = Panel([HalfDay], (_, _, k) => k >= K(10, 0) ? 101 : 100);

        Assert.Equal(["03-07 10:05 buy A 9", "03-07 12:40 sell A 9"], Fills(Run(data, "orb-long"), data));
        Assert.Equal(["03-07 09:10 buy A 99", "03-07 12:40 sell A 99"], Fills(Run(data, "open-close", ("weight", "1")), data));
    }

    [Fact]
    public void AnExit_GoesOutEvenAfterAMinuteWithoutTrades()
    {
        // A trades nothing at 17:05 (the exit decision's bar) and nothing after 17:10. The market exit decided at 17:10
        // needs no price, so it sells in the 17:10 bar; waiting for a traded decision bar would have held A overnight.
        double? Close(int i, int d, int k) => i == 0 && (k == K(17, 5) || k > K(17, 10)) ? null : 100;
        MarketPanel data = Panel([Monday, Monday.AddDays(1)], Close, instruments: 2);
        BacktestResult r = Run(data, "open-close");

        Assert.True(r.Ok, r.Record.Note);
        Assert.Contains("03-03 17:10 sell A 9", Fills(r, data));
    }

    [Fact]
    public void OnDailyBars_OrOnACalendarHoliday_TheRunFails_AndIsLogged()
    {
        MarketPanel daily = BacktestFixtures.FromCloses([100, 101, 102], new DateOnly(2025, 3, 3));
        BacktestResult r = Run(daily, "orb-long");
        Assert.Equal(TrialStatus.Failed, r.Record.Status);
        Assert.Contains("trades intraday bars", r.Record.Note, StringComparison.Ordinal);

        // A Saturday's bars: the calendar says no session, and nothing is guessed.
        var saturday = new DateOnly(2025, 3, 8);
        DateTimeOffset nine = Calendar.ToUtc(saturday, new TimeOnly(9, 0));
        MarketPanel weekend = MarketPanel.FromIntradayBars(
            [(new PanelInstrument("A", 1, false, Cents), [new Bar(nine, 100m, 100.1m, 99.9m, 100m, 1000), new Bar(nine.AddMinutes(5), 100m, 100.1m, 99.9m, 100m, 1000)])], Source);
        r = Run(weekend, "open-close");
        Assert.Equal(TrialStatus.Failed, r.Record.Status);
        Assert.Contains("2025-03-08, which the XSTO calendar has as a weekend", r.Record.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("orb-long", "exit=5", "exit must be 10..480 minutes")]
    [InlineData("orb-long", "range=7", "range (7 minutes) must be a whole number of 5-minute bars")]
    [InlineData("orb-long", "weight=0", "weight must be in [0.01, 1]")]
    [InlineData("orb-long", "stop=1", "unknown parameter(s) stop")]
    [InlineData("late-momentum", "entry=15", "entry (15 minutes before the close) must come before exit (15)")]
    [InlineData("open-close", "entry=x", "'entry' must be a whole number of minutes")]
    public void BadParameters_AreRefused(string name, string parameter, string expected)
    {
        string[] kv = parameter.Split('=');
        var ex = Assert.ThrowsAny<ArgumentException>(() => IntradayStrategyCatalog.Create(name, new Dictionary<string, string> { [kv[0]] = kv[1] }, Clock));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCatalog_ListsEachParameterItCreates_WithItsDefault()
    {
        foreach (string name in IntradayStrategyCatalog.Names)
        {
            StrategyDefinition d = IntradayStrategyCatalog.Create(name, new Dictionary<string, string>(), Clock);
            Assert.Equal(
                IntradayStrategyCatalog.ParametersOf(name).Select(p => (p.Key, p.Default)).Order(),
                d.Spec.Parameters.Select(p => (p.Key, (string?)p.Value)).Order());
            Assert.NotEmpty(IntradayStrategyCatalog.Summary(name));
        }

        Assert.Empty(IntradayStrategyCatalog.Names.Intersect(StrategyCatalog.Names)); // never offered to the daily paper account
    }
}
