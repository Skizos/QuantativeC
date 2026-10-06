using QuantAnalyst.Desktop.Core.Charts;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>The chart maths the window draws from (docs/plans/11-app-redesign.md): tested here, drawn in WPF.</summary>
public sealed class ChartTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 7, 0, 0, TimeSpan.Zero); // 09:00 Stockholm

    private static IReadOnlyList<ChartPoint> Daily(params double[] values) =>
        [.. values.Select((v, i) => new ChartPoint(Monday.AddDays(i), v))];

    [Theory]
    [InlineData(10, 2.5)]
    [InlineData(1, 0.25)]
    [InlineData(7, 2)]
    [InlineData(3, 1)]
    [InlineData(100, 25)]
    [InlineData(0.9, 0.25)]
    [InlineData(0, 1)]
    public void Steps_AreNice(double range, double step) => Assert.Equal(step, ChartLayout.NiceStep(range), 9);

    [Theory]
    [InlineData(0.25, 2)]
    [InlineData(5, 0)]
    [InlineData(0.1, 1)]
    [InlineData(2.5, 1)]
    [InlineData(0.005, 3)]
    public void Labels_UseTheDecimalsTheirStepNeeds(double step, int decimals) => Assert.Equal(decimals, ChartLayout.DecimalsFor(step));

    [Fact]
    public void Points_FillThePlot_HigherValuesHigherUp()
    {
        ChartLayout l = ChartLayout.Compute(new ChartData { Main = Daily(10, 20, 15) }, 400, 200, ChartInsets.None);
        Assert.Equal(l.PlotLeft, l.Main[0].X, 6);
        Assert.Equal(l.PlotRight, l.Main[2].X, 6);
        Assert.True(l.Main[1].Y < l.Main[2].Y && l.Main[2].Y < l.Main[0].Y);
        Assert.All(l.Main, p => Assert.InRange(p.Y, l.PlotTop, l.PlotBottom));
    }

    [Fact]
    public void AFlatLine_AndASinglePoint_StillHaveAScale()
    {
        ChartLayout flat = ChartLayout.Compute(new ChartData { Main = Daily(50, 50, 50) }, 300, 150);
        Assert.True(flat.YMax > flat.YMin);
        Assert.All(flat.Main, p => Assert.Equal(flat.Main[0].Y, p.Y, 9));
        Assert.NotEmpty(flat.YTicks);

        ChartLayout one = ChartLayout.Compute(new ChartData { Main = Daily(42) }, 300, 150);
        Assert.Equal((one.PlotLeft + one.PlotRight) / 2, one.Main[0].X, 6);

        ChartLayout none = ChartLayout.Compute(ChartData.Empty, 300, 150);
        Assert.Empty(none.Main);
        Assert.Null(none.Hover(10));
    }

    [Fact]
    public void ValueTicks_AreEvenlySteppedInsideThePlot_WithSwedishLabels()
    {
        ChartLayout l = ChartLayout.Compute(new ChartData { Main = Daily(70.1, 70.9, 70.4, 71.3) }, 500, 240);
        Assert.True(l.YTicks.Count >= 3);
        Assert.All(l.YTicks, t => Assert.InRange(t.Position, l.PlotTop - 1e-6, l.PlotBottom + 1e-6));
        for (int i = 1; i < l.YTicks.Count; i++)
        {
            Assert.True(l.YTicks[i].Position < l.YTicks[i - 1].Position); // rising values, rising on screen
        }

        Assert.Contains(l.YTicks, t => t.Label.Contains(',', StringComparison.Ordinal)); // "70,50"
    }

    [Fact]
    public void Hover_PicksTheNearestPoint_AndItsChangeFromTheBaseline()
    {
        var data = new ChartData { Main = Daily(100, 110, 90), Baseline = 100 };
        ChartLayout l = ChartLayout.Compute(data, 400, 200);
        HoverInfo? h = l.Hover(l.Main[1].X + 3);
        Assert.NotNull(h);
        Assert.Equal(1, h.Index);
        Assert.Equal("110,00", h.Value);
        Assert.Equal("+10,00 %", h.Change);
        Assert.Equal(2, l.Hover(10_000)!.Index);
    }

    [Fact]
    public void TheLine_IsGreenAtOrAboveItsBaseline_AndRedBelow()
    {
        Assert.True(new ChartData { Main = Daily(100, 101), Baseline = 100 }.IsUp);
        Assert.True(new ChartData { Main = Daily(100, 100), Baseline = 100 }.IsUp);
        Assert.False(new ChartData { Main = Daily(100, 99), Baseline = 100 }.IsUp);
        Assert.False(new ChartData { Main = Daily(100, 99) }.IsUp); // no baseline: against the first value
        ChartLayout l = ChartLayout.Compute(new ChartData { Main = Daily(100, 101), Baseline = 100 }, 400, 200);
        Assert.NotNull(l.BaselineY);
        Assert.True(l.BaselineY > l.Main[1].Y);
    }

    [Fact]
    public void AnIntradayWindow_PlacesPointsByClockTime_WithHourLabels()
    {
        DateTimeOffset open = Monday; // 09:00 Stockholm
        DateTimeOffset close = Monday.AddHours(8.5); // 17:30
        var data = new ChartData
        {
            Axis = TimeAxis.Intraday,
            Window = (open, close),
            Main = [new(open.AddMinutes(10), 5000), new(open.AddHours(1), 5010)],
            Markers = [new ChartMarker(open.AddHours(1), 70.8, MarkerKind.Buy, "Buy 6 @ 70,80")],
        };
        ChartLayout l = ChartLayout.Compute(data, 1000, 300);
        Assert.True(l.Main[0].X > l.PlotLeft); // 09:10 is after the window opens
        Assert.True(l.Main[1].X < (l.PlotLeft + l.PlotRight) / 2); // 10:00 is early in the day
        Assert.Equal(l.Main[1].X, l.Markers[0].At.X, 6); // the fill sits at its time
        Assert.Equal(["09:00", "11:00", "13:00", "15:00", "17:00"], l.XTicks.Select(t => t.Label));
    }

    [Fact]
    public void DailyOverlays_AndMarkers_ShareTheMainSeriesSlots()
    {
        IReadOnlyList<ChartPoint> main = Daily(10, 11, 12, 13, 14);
        var data = new ChartData
        {
            Main = main,
            Overlays = [new ChartOverlay("2-day average", Indicators.Sma(main, 2))],
            Markers = [new ChartMarker(main[3].At, 13, MarkerKind.Sell, "Sell")],
        };
        ChartLayout l = ChartLayout.Compute(data, 500, 200);
        Assert.Equal(4, l.Overlays[0].Count);
        Assert.Equal(l.Main[1].X, l.Overlays[0][0].X, 6); // the first average sits on the second day
        Assert.Equal(l.Main[3].X, l.Markers[0].At.X, 6);
        Assert.Equal(5, l.XTicks.Count);
    }

    [Fact]
    public void MovingAverages_StartWhenTheirWindowIsFull()
    {
        IReadOnlyList<ChartPoint> sma = Indicators.Sma(Daily(1, 2, 3, 4), 2);
        Assert.Equal([1.5, 2.5, 3.5], sma.Select(p => p.Value));
        Assert.Equal(Monday.AddDays(1), sma[0].At);
        Assert.Empty(Indicators.Sma(Daily(1, 2), 3));
    }

    [Fact]
    public void Ranges_KeepTheirMonths_AndAllKeepsEverything()
    {
        IReadOnlyList<ChartPoint> year = [.. Enumerable.Range(0, 400).Select(i => new ChartPoint(Monday.AddDays(i), i))];
        IReadOnlyList<ChartPoint> month = ChartRange.All.Single(r => r.Label == "1M").Take(year);
        Assert.InRange(month.Count, 28, 32);
        Assert.Equal(year[^1], month[^1]);
        Assert.Equal(year.Count, ChartRange.All.Single(r => r.Label == "All").Take(year).Count);
        Assert.Equal(["1M", "3M", "6M", "1Y", "3Y", "All"], ChartRange.All.Select(r => r.Label));
        Assert.Equal("1Y", ChartRange.OneYear.Label);
    }
}
