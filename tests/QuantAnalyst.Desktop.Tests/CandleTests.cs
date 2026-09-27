using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Desktop.Core.Charts;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Reports;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>The candlestick maths the charts window draws from (docs/plans/12-charts-window.md): tested here, drawn in WPF.</summary>
public sealed class CandleTests
{
    private static readonly DateOnly Monday = new(2026, 9, 28);

    private static Candle Day(DateOnly date, double o, double h, double l, double c, long? v = 100) =>
        new(CandlePeriod.Midnight(date), o, h, l, c, v);

    /// <summary>Mon 28 Sep to Thu 1 Oct 2026: up, down, a doji, up.</summary>
    private static IReadOnlyList<Candle> FourDays(bool volume = true) =>
    [
        Day(Monday, 10, 12, 9, 11, volume ? 100 : null),
        Day(Monday.AddDays(1), 11, 11.5, 10, 10.2, volume ? 200 : null),
        Day(Monday.AddDays(2), 10.2, 10.2, 10.2, 10.2, volume ? 50 : null),
        Day(Monday.AddDays(3), 10.2, 13, 10, 12.5, volume ? 400 : null),
    ];

    private static DateTimeOffset Stockholm(int month, int day, int hour, int minute, int second = 0) =>
        new(2026, month, day, hour - 2, minute, second, TimeSpan.Zero); // CEST until 25 Oct

    // ---- periods and aggregation -----------------------------------------------------------------------------

    [Fact]
    public void Periods_StartOnMidnight_Monday_TheFirst_AndTheWholeMinute_InStockholm()
    {
        DateTimeOffset thursday = Stockholm(10, 1, 14, 37, 12);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.Zero), CandlePeriod.Day.StartOf(thursday));
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 22, 0, 0, TimeSpan.Zero), CandlePeriod.Week.StartOf(thursday)); // Mon 28 Sep
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.Zero), CandlePeriod.Month.StartOf(thursday)); // 1 Oct
        Assert.Equal(Stockholm(10, 1, 14, 35), CandlePeriod.FiveMinutes.StartOf(thursday));
        Assert.Equal(Stockholm(10, 1, 14, 30), CandlePeriod.FifteenMinutes.StartOf(thursday));
        Assert.Equal(Stockholm(10, 1, 14, 40), CandlePeriod.FiveMinutes.Next(Stockholm(10, 1, 14, 35)));
        Assert.Equal(CandlePeriod.Midnight(new DateOnly(2026, 11, 1)), CandlePeriod.Month.Next(CandlePeriod.Month.StartOf(thursday)));

        // Across the end of summer time (Sun 25 Oct): the next day still starts at midnight.
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero), CandlePeriod.Day.Next(CandlePeriod.Midnight(new DateOnly(2026, 10, 25))));
    }

    [Fact]
    public void Weeks_AndMonths_CombineTheDays_FirstOpen_HighestHigh_LowestLow_LastClose_SummedVolume()
    {
        IReadOnlyList<Candle> days = [.. FourDays(), Day(Monday.AddDays(4), 12.5, 12.8, 12, 12.1, 300), Day(Monday.AddDays(7), 12, 12.4, 11.8, 12.2, 10)];

        IReadOnlyList<Candle> weeks = Candles.Aggregate(days, CandlePeriod.Week);
        Assert.Equal(2, weeks.Count);
        Assert.Equal(new Candle(CandlePeriod.Midnight(Monday), 10, 13, 9, 12.1, 1050), weeks[0]);
        Assert.Equal(new Candle(CandlePeriod.Midnight(Monday.AddDays(7)), 12, 12.4, 11.8, 12.2, 10), weeks[1]);

        // The same week falls in two months: September ends on the Wednesday.
        IReadOnlyList<Candle> months = Candles.Aggregate(days, CandlePeriod.Month);
        Assert.Equal(2, months.Count);
        Assert.Equal(new Candle(CandlePeriod.Midnight(new DateOnly(2026, 9, 1)), 10, 12, 9, 10.2, 350), months[0]);
        Assert.Equal(new Candle(CandlePeriod.Midnight(new DateOnly(2026, 10, 1)), 10.2, 13, 10, 12.2, 710), months[1]);

        // Volume unknown in any part: unknown in the sum.
        Assert.Null(Candles.Aggregate([days[0], days[1] with { Volume = null }], CandlePeriod.Week)[0].Volume);
        Assert.Equal(days, Candles.Aggregate(days, CandlePeriod.Day));
    }

    [Fact]
    public void TheStoresDailyBars_BecomeDayCandles_OldestFirst()
    {
        IReadOnlyList<Candle> c = Candles.FromDaily([new DailyBar(Monday.AddDays(1), 95.2m, 96m, 94.8m, 95.9m, 7_000_000), new DailyBar(Monday, 94m, 95.5m, 93.9m, 95.2m, 6_100_000)]);
        Assert.Equal([CandlePeriod.Midnight(Monday), CandlePeriod.Midnight(Monday.AddDays(1))], c.Select(x => x.At));
        Assert.Equal(new Candle(CandlePeriod.Midnight(Monday), 94, 95.5, 93.9, 95.2, 6_100_000), c[0]);
    }

    [Fact]
    public void QuotesBecomeMinuteCandles_AndThoseFiveMinuteCandles()
    {
        var b = new CandleBuilder();
        b.Add(Stockholm(9, 28, 9, 0, 1), 95.0);
        b.Add(Stockholm(9, 28, 9, 0, 20), 95.6);
        b.Add(Stockholm(9, 28, 9, 0, 40), 94.8);
        b.Add(Stockholm(9, 28, 9, 0, 59), 95.2);
        b.Add(Stockholm(9, 28, 9, 1, 3), 95.3);
        b.Add(Stockholm(9, 28, 9, 0, 59), 96.0); // late: moves its own minute's high, not its close
        b.Add(Stockholm(9, 28, 8, 59, 59), 90.0); // before every candle: dropped
        b.Add(Stockholm(9, 28, 9, 4, 0), double.NaN); // not a price
        b.Add(Stockholm(9, 28, 9, 6, 30), 95.1);

        Assert.Equal(
            [
                new Candle(Stockholm(9, 28, 9, 0), 95.0, 96.0, 94.8, 95.2, null),
                new Candle(Stockholm(9, 28, 9, 1), 95.3, 95.3, 95.3, 95.3, null),
                new Candle(Stockholm(9, 28, 9, 6), 95.1, 95.1, 95.1, 95.1, null),
            ],
            b.Candles);

        Assert.Equal(
            [
                new Candle(Stockholm(9, 28, 9, 0), 95.0, 96.0, 94.8, 95.3, null),
                new Candle(Stockholm(9, 28, 9, 5), 95.1, 95.1, 95.1, 95.1, null),
            ],
            Candles.Aggregate(b.Candles, CandlePeriod.FiveMinutes));
    }

    // ---- layout ----------------------------------------------------------------------------------------------

    [Fact]
    public void EachCandleGetsASlot_AWickFromHighToLow_AndABodyFromOpenToClose()
    {
        CandleLayout l = CandleLayout.Compute(new CandleChartData { Candles = FourDays() }, null, 600, 300);
        Assert.Equal(new CandleViewport(0, 4), l.View);
        Assert.Equal(4, l.Candles.Count);
        Assert.Equal((l.PlotRight - l.PlotLeft) / 4, l.SlotWidth, 9);
        Assert.Equal(l.PlotLeft + (l.SlotWidth / 2), l.Candles[0].X, 9);
        Assert.All(l.Candles.Skip(1).Zip(l.Candles), p => Assert.Equal(l.SlotWidth, p.First.X - p.Second.X, 9));
        Assert.InRange(l.BodyWidth, 1, 24);

        PlacedCandle up = l.Candles[0];
        PlacedCandle down = l.Candles[1];
        PlacedCandle doji = l.Candles[2];
        Assert.True(up.IsUp);
        Assert.False(down.IsUp);
        Assert.True(up.WickTop < up.BodyTop && up.BodyTop < up.BodyBottom && up.BodyBottom < up.WickBottom);
        Assert.True(l.Candles[3].WickTop < up.WickTop); // the highest high is highest up
        Assert.Equal(1, doji.BodyBottom - doji.BodyTop, 9); // a flat candle still shows
        Assert.All(l.Candles, c => Assert.True(c.WickTop >= l.PlotTop - 1 && c.WickBottom <= l.PlotBottom + 1));

        Assert.True(l.YTicks.Count >= 3);
        Assert.All(l.YTicks, t => Assert.InRange(t.Position, l.PlotTop, l.PlotBottom));
        Assert.Equal(new PriceTag(l.Candles[3].BodyTop, "12,50", true), l.LastPrice);
    }

    [Fact]
    public void Volume_HasItsOwnPaneUnderThePrices_ScaledToTheLargest()
    {
        CandleLayout l = CandleLayout.Compute(new CandleChartData { Candles = FourDays() }, null, 600, 300);
        Assert.True(l.HasVolume);
        Assert.True(l.VolumeTop > l.PlotBottom);
        Assert.Equal(l.VolumeTop, l.Candles[3].VolumeTop!.Value, 9); // 400, the largest
        Assert.Equal(l.VolumeBottom - ((l.VolumeBottom - l.VolumeTop) / 2), l.Candles[1].VolumeTop!.Value, 9); // 200

        CandleLayout none = CandleLayout.Compute(new CandleChartData { Candles = FourDays(volume: false) }, null, 600, 300);
        Assert.False(none.HasVolume);
        Assert.Equal(none.VolumeBottom, none.PlotBottom);
        Assert.All(none.Candles, c => Assert.Null(c.VolumeTop));

        CandleLayout off = CandleLayout.Compute(new CandleChartData { Candles = FourDays(), ShowVolume = false }, null, 600, 300);
        Assert.False(off.HasVolume);
    }

    [Fact]
    public void Hover_GivesTheCandleUnderTheMouse_WithItsNumbers_AndTheChangeFromTheCloseBefore()
    {
        CandleLayout l = CandleLayout.Compute(new CandleChartData { Candles = FourDays() }, null, 600, 300);
        CandleHover h = l.Hover(l.Candles[1].X + (l.SlotWidth * 0.4))!;
        Assert.Equal(1, h.Index);
        Assert.Equal(("Tue 29 Sep 2026", "11,00", "11,50", "10,00", "10,20"), (h.Time, h.Open, h.High, h.Low, h.Close));
        Assert.Equal("−7,27 %", h.Change); // 10,20 against Monday's 11,00
        Assert.Equal("200", h.Volume);
        Assert.False(h.IsUp);
        Assert.Equal(0, l.Hover(-50)!.Index);
        Assert.Equal(3, l.Hover(10_000)!.Index);
        Assert.Null(CandleLayout.Compute(CandleChartData.Empty, null, 600, 300).Hover(10));

        CandleLayout weeks = CandleLayout.Compute(new CandleChartData { Candles = Candles.Aggregate(FourDays(), CandlePeriod.Week), Period = CandlePeriod.Week }, null, 600, 300);
        Assert.Equal("Week of 28 Sep 2026", weeks.Hover(300)!.Time);
        var minute = new CandleChartData { Candles = [new Candle(Stockholm(9, 28, 10, 15), 1, 2, 1, 2, null)], Period = CandlePeriod.FiveMinutes };
        Assert.Equal("10:15–10:20", CandleLayout.Compute(minute, null, 600, 300).Hover(300)!.Time);
    }

    [Fact]
    public void Trades_LandOnTheirCandle_AndTradesOutsideTheCandlesAreLeftOut()
    {
        DateTimeOffset tuesdayNoon = Stockholm(9, 29, 12, 0);
        var data = new CandleChartData
        {
            Candles = FourDays(),
            Markers =
            [
                new ChartMarker(tuesdayNoon, 10.5, MarkerKind.Buy, "Paper: bought 6"),
                new ChartMarker(Stockholm(10, 3, 12, 0), 12, MarkerKind.Sell, "Saturday, after the last candle"),
                new ChartMarker(Stockholm(9, 20, 12, 0), 9, MarkerKind.Sell, "before the first"),
            ],
        };
        Assert.Equal(1, data.IndexOf(tuesdayNoon));
        Assert.Equal(-1, data.IndexOf(Stockholm(10, 3, 12, 0)));

        CandleLayout l = CandleLayout.Compute(data, null, 600, 300);
        PlacedMarker m = Assert.Single(l.Markers);
        Assert.Equal(l.Candles[1].X, m.At.X, 9);
        Assert.InRange(m.At.Y, l.Candles[1].WickTop, l.Candles[1].WickBottom); // 10,5 is inside Tuesday's range
    }

    [Fact]
    public void ADailyAverage_OnWeekCandles_GivesOnePointPerWeek_TheWeeksLast()
    {
        IReadOnlyList<Candle> days = [.. FourDays(), Day(Monday.AddDays(7), 12, 12.4, 11.8, 12.2)];
        var average = new ChartOverlay("2-day", Indicators.Sma(Candles.Closes(days), 2));
        var data = new CandleChartData { Candles = Candles.Aggregate(days, CandlePeriod.Week), Period = CandlePeriod.Week, Overlays = [average] };
        CandleLayout l = CandleLayout.Compute(data, null, 600, 300);

        IReadOnlyList<Pixel> line = Assert.Single(l.Overlays);
        Assert.Equal([l.Candles[0].X, l.Candles[1].X], line.Select(p => p.X));
        CandleLayout thursdayAverage = CandleLayout.Compute(data with { Overlays = [new ChartOverlay("x", [new ChartPoint(days[0].At, 11.35)])] }, null, 600, 300);
        Assert.Equal(thursdayAverage.Overlays[0][0].Y, line[0].Y, 9); // (10,2 + 12,5) / 2, Thursday's
    }

    [Fact]
    public void TimeLabels_FollowThePeriod_WithoutRepeats()
    {
        var minutes = new CandleChartData
        {
            Candles = [.. Enumerable.Range(0, 30).Select(i => new Candle(Stockholm(9, 28, 9, 0).AddMinutes(i * 5), 1, 2, 1, 2, null))],
            Period = CandlePeriod.FiveMinutes,
        };
        CandleLayout l = CandleLayout.Compute(minutes, null, 800, 300);
        Assert.Equal("09:00", l.XTicks[0].Label);
        Assert.All(l.XTicks, t => Assert.Matches("^[0-2][0-9]:[0-5][05]$", t.Label));
        Assert.Equal(l.XTicks.Count, l.XTicks.Select(t => t.Label).Distinct().Count());

        var months = new CandleChartData { Candles = Candles.Aggregate(FourDays(), CandlePeriod.Month), Period = CandlePeriod.Month };
        Assert.Equal(["Sep 2026", "Oct 2026"], CandleLayout.Compute(months, null, 800, 300).XTicks.Select(t => t.Label));
    }

    // ---- zoom and pan ----------------------------------------------------------------------------------------

    [Fact]
    public void Zooming_KeepsTheCandleUnderTheMouse_AndStaysWithinTheCandles()
    {
        Assert.Equal(new CandleViewport(80, 20), CandleViewport.Latest(100, 20));
        Assert.Equal(new CandleViewport(0, 100), CandleViewport.Latest(100, null));
        Assert.Equal(new CandleViewport(0, 5), CandleViewport.Latest(5, 20));
        Assert.Equal(new CandleViewport(90, 10), CandleViewport.Latest(100, 3)); // at least 10

        var middle = new CandleViewport(40, 20);
        CandleViewport wider = middle.Zoom(100, 2, 0.5);
        Assert.Equal(new CandleViewport(30, 40), wider);
        Assert.Equal(50, wider.Start + (0.5 * wider.Count)); // the candle under the mouse stays under it
        Assert.Equal(new CandleViewport(40, 10), middle.Zoom(100, 0.5, 0)); // anchored at the left edge
        Assert.Equal(new CandleViewport(90, 10), CandleViewport.Latest(100, 20).Zoom(100, 0.5, 1)); // at the right edge
        Assert.Equal(new CandleViewport(90, 10), new CandleViewport(90, 10).Zoom(100, 0.5, 0.5)); // not below 10
        Assert.Equal(new CandleViewport(0, 100), middle.Zoom(100, 50, 0.5)); // not beyond all
        Assert.Equal(new CandleViewport(40, 21), middle.Zoom(100, 1.01, 0)); // a small step still moves
    }

    [Fact]
    public void Panning_StopsAtBothEnds_AndALiveChartFollowsNewCandles()
    {
        var view = new CandleViewport(40, 20);
        Assert.Equal(new CandleViewport(0, 20), view.Pan(100, -100));
        Assert.Equal(new CandleViewport(80, 20), view.Pan(100, 500));
        Assert.Equal(new CandleViewport(45, 20), view.Pan(100, 5));

        Assert.Equal(new CandleViewport(81, 20), new CandleViewport(80, 20).Follow(100, 101)); // at the right edge: follows
        Assert.Equal(new CandleViewport(40, 20), view.Follow(100, 101)); // looking back: stays
        Assert.Equal(new CandleViewport(0, 15), new CandleViewport(80, 20).Follow(100, 15));

        CandleLayout l = CandleLayout.Compute(new CandleChartData { Candles = FourDays() }, new CandleViewport(1, 2), 600, 300);
        Assert.Equal([1, 2], l.Candles.Select(c => c.Index));
        Assert.Equal(l.Candles[0].X, l.Hover(l.PlotLeft + 1)!.At.X, 9);
        Assert.Equal(new CandleViewport(0, 4), CandleLayout.Compute(new CandleChartData { Candles = FourDays() }, new CandleViewport(3, 20), 600, 300).View); // stale view: starts again
    }

    // ---- sources ---------------------------------------------------------------------------------------------

    [Fact]
    public void DailyCandles_ComeFromTheStore_WithOpenHighLowAndVolume()
    {
        string root = Path.Combine(Path.GetTempPath(), "qa-candles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "history.duckdb");
            Assert.Empty(ChartSources.DailyCandles(path, new OrderbookId("5240")));
            using (HistoryStore store = HistoryStore.Open(path))
            {
                store.RegisterSource(AvanzaChartImporter.AvanzaPriceChart);
                store.UpsertDailyBars(new OrderbookId("5240"),
                    [new DailyBar(Monday, 94m, 95.5m, 93.9m, 95.2m, 6_100_000), new DailyBar(Monday.AddDays(1), 95.2m, 96m, 94.8m, 95.9m, 7_000_000)],
                    AvanzaChartImporter.AvanzaPriceChart, "test", DateTimeOffset.UtcNow);
            }

            IReadOnlyList<Candle> c = ChartSources.DailyCandles(path, new OrderbookId("5240"));
            Assert.Equal([new Candle(CandlePeriod.Midnight(Monday), 94, 95.5, 93.9, 95.2, 6_100_000), new Candle(CandlePeriod.Midnight(Monday.AddDays(1)), 95.2, 96, 94.8, 95.9, 7_000_000)], c);
            Assert.Empty(ChartSources.DailyCandles(path, new OrderbookId("1")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void YourTrades_ComeFromTheEndOfDayReports_ByTicker()
    {
        string audit = Path.Combine(Path.GetTempPath(), "qa-candles", Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Stockholm(9, 29, 10, 5));
            var log = new AuditLog(audit, clock);
            log.Append("session-start", new { mode = "Paper" });
            log.Append("sim-fill", new { ticker = "ERIC B", side = "Buy", volume = 6L, price = 94.56m, limit = 95m, how = "touch" });
            log.Append("sim-fill", new { ticker = "VOLV B", side = "Buy", volume = 2L, price = 250m, limit = 251m, how = "touch" });
            clock.SetUtcNow(Stockholm(9, 29, 15, 10));
            log.Append("sim-fill", new { ticker = "ERIC B", side = "Sell", volume = 6L, price = 95.1m, limit = 95m, how = "touch" });

            EodReport report = EodReport.Build(audit, new DateOnly(2026, 9, 29), clock);
            IReadOnlyList<ChartMarker> markers = ChartSources.TradeMarkers([report], "ERIC B");
            Assert.Equal(2, markers.Count);
            Assert.Equal((Stockholm(9, 29, 12, 0), 94.56, MarkerKind.Buy), (markers[0].At, markers[0].Value, markers[0].Kind));
            Assert.Equal("Paper: bought 6 @ 94,56 · Tue 29 Sep 2026", markers[0].Label);
            Assert.Equal(MarkerKind.Sell, markers[1].Kind);
            Assert.Empty(ChartSources.TradeMarkers([report], "ABB"));
        }
        finally
        {
            if (Directory.Exists(audit))
            {
                Directory.Delete(audit, recursive: true);
            }
        }
    }
}
