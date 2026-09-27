using System.Globalization;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Desktop.Core.Presentation;

namespace QuantAnalyst.Desktop.Core.Charts;

/// <summary>
/// Everything one candlestick chart shows (docs/plans/12-charts-window.md): the candles, the period they are in, lines
/// over them (moving averages), your trades as markers, levels (limits, yesterday's close) and whether the volume pane
/// is drawn. All candles are here; <see cref="InitialCount"/> says how many of the latest are on screen at first (the
/// range), so older ones can be reached by zooming out or panning, and the averages are complete at the left edge.
/// </summary>
public sealed record CandleChartData
{
    public static CandleChartData Empty { get; } = new();

    public IReadOnlyList<Candle> Candles { get; init; } = [];

    public CandlePeriod Period { get; init; } = CandlePeriod.Day;

    public IReadOnlyList<ChartOverlay> Overlays { get; init; } = [];

    public IReadOnlyList<ChartMarker> Markers { get; init; } = [];

    public IReadOnlyList<ChartLevel> Levels { get; init; } = [];

    public bool ShowVolume { get; init; } = true;

    /// <summary>Gets how many of the latest candles are shown at first; null for all.</summary>
    public int? InitialCount { get; init; }

    /// <summary>
    /// Gets what the candles are (instrument, source, period, range). While it stays the same the chart keeps its zoom
    /// and follows new candles; a new key starts again from <see cref="InitialCount"/>.
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Gets how a price reads on the axis and in the hover bubble.</summary>
    public Func<double, string>? FormatValue { get; init; }

    public bool IsEmpty => Candles.Count == 0;

    /// <summary>Gets a value indicating whether volume bars are drawn: asked for, and some candle has volume.</summary>
    public bool HasVolume => ShowVolume && Candles.Any(c => c.Volume is > 0);

    /// <summary>The candle whose period holds <paramref name="at"/>, or -1 (before the first, after the last, or in a gap).</summary>
    public int IndexOf(DateTimeOffset at)
    {
        int lo = 0;
        int hi = Candles.Count - 1;
        int found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Candles[mid].At <= at)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found >= 0 && at < Period.Next(Candles[found].At) ? found : -1;
    }
}

/// <summary>
/// Which candles are on screen: <see cref="Count"/> of them from <see cref="Start"/>. Zooming keeps the candle under
/// the mouse where it is; panning stops at both ends; at least <see cref="MinCount"/> candles (or all, when fewer).
/// </summary>
public readonly record struct CandleViewport(int Start, int Count)
{
    public const int MinCount = 10;

    public int End => Start + Count;

    /// <summary>The latest <paramref name="count"/> of <paramref name="total"/> candles (all for null).</summary>
    public static CandleViewport Latest(int total, int? count)
    {
        if (total <= 0)
        {
            return default;
        }

        int n = Math.Clamp(count ?? total, Math.Min(MinCount, total), total);
        return new CandleViewport(total - n, n);
    }

    /// <summary>
    /// Zooms by <paramref name="factor"/> (below 1 zooms in) around <paramref name="anchor"/>, the mouse's place across
    /// the plot from 0 (left) to 1 (right).
    /// </summary>
    public CandleViewport Zoom(int total, double factor, double anchor)
    {
        if (total <= 0)
        {
            return default;
        }

        anchor = Math.Clamp(anchor, 0, 1);
        int n = Math.Clamp((int)Math.Round(Count * factor, MidpointRounding.AwayFromZero), Math.Min(MinCount, total), total);
        if (n == Count && factor != 1)
        {
            n = Math.Clamp(Count + Math.Sign(factor - 1), Math.Min(MinCount, total), total);
        }

        double at = Start + (anchor * Count);
        int start = (int)Math.Round(at - (anchor * n), MidpointRounding.AwayFromZero);
        return new CandleViewport(Math.Clamp(start, 0, total - n), n);
    }

    /// <summary>Moves by <paramref name="candles"/> (positive: towards the latest).</summary>
    public CandleViewport Pan(int total, int candles)
    {
        if (total <= 0)
        {
            return default;
        }

        int n = Math.Clamp(Count, Math.Min(MinCount, total), total);
        return new CandleViewport(Math.Clamp(Start + candles, 0, total - n), n);
    }

    /// <summary>After the candles changed from <paramref name="oldTotal"/> to <paramref name="newTotal"/>: at the right edge it follows the new ones.</summary>
    public CandleViewport Follow(int oldTotal, int newTotal)
    {
        if (newTotal <= 0)
        {
            return default;
        }

        if (End >= oldTotal)
        {
            return Latest(newTotal, Count);
        }

        int n = Math.Clamp(Count, Math.Min(MinCount, newTotal), newTotal);
        return new CandleViewport(Math.Clamp(Start, 0, newTotal - n), n);
    }
}

/// <summary>A candle in pixels: its centre, body, wick and volume bar (null without the volume pane).</summary>
public sealed record PlacedCandle(int Index, double X, double BodyTop, double BodyBottom, double WickTop, double WickBottom, bool IsUp, double? VolumeTop);

/// <summary>What the hover bubble shows for the candle under the mouse.</summary>
public sealed record CandleHover(Pixel At, string Time, string Open, string High, string Low, string Close, string Change, string? Volume, bool IsUp, int Index);

/// <summary>The last price on the right axis, in its candle's colour.</summary>
public sealed record PriceTag(double Y, string Label, bool IsUp);

/// <summary>
/// Where everything of a <see cref="CandleChartData"/> goes in a box of a given size (docs/plans/12-charts-window.md):
/// one slot per candle on screen, so nights, weekends and holidays leave no gaps; a wick from high to low and a body
/// from open to close; the volume pane under the prices; value ticks at nice steps over what is on screen; lines,
/// markers and levels. Pure maths, tested on Linux; the WPF control only draws what this says.
/// </summary>
public sealed class CandleLayout
{
    private const double PaneGap = 10;
    private const double MinBodyHeight = 1;

    private readonly CandleChartData _data;

    private CandleLayout(CandleChartData data, CandleViewport view, double width, double height, ChartInsets insets)
    {
        _data = data;
        View = view;
        Width = width;
        Height = height;
        PlotLeft = insets.Left;
        PlotTop = insets.Top;
        PlotRight = Math.Max(insets.Left + 1, width - insets.Right);
        double innerBottom = Math.Max(insets.Top + 1, height - insets.Bottom);
        HasVolume = data.HasVolume && innerBottom - insets.Top > 120;
        VolumeBottom = innerBottom;
        VolumeTop = HasVolume ? innerBottom - Math.Round((innerBottom - insets.Top) * 0.2) : innerBottom;
        PlotBottom = HasVolume ? Math.Max(PlotTop + 1, VolumeTop - PaneGap) : innerBottom;
        SlotWidth = (PlotRight - PlotLeft) / Math.Max(1, view.Count);
        BodyWidth = Math.Clamp(Math.Floor(SlotWidth * 0.7), 1, 24);

        (YMin, YMax, YStep) = ChartLayout.ValueScale(VisibleValues());
        long maxVolume = 0;
        for (int i = view.Start; i < view.End; i++)
        {
            maxVolume = Math.Max(maxVolume, data.Candles[i].Volume ?? 0);
        }

        Candles = [.. Enumerable.Range(view.Start, view.Count).Select(i => Place(i, maxVolume))];
        Overlays = [.. data.Overlays.Select(PlaceOverlay)];
        Markers = [.. data.Markers.Select(m => (Marker: m, Index: data.IndexOf(m.At)))
            .Where(x => InView(x.Index))
            .Select(x => new PlacedMarker(new Pixel(XOf(x.Index), Y(x.Marker.Value)), x.Marker))];
        Levels = [.. data.Levels.Select(l => new PlacedLevel(Y(l.Value), l))];
        YTicks = BuildYTicks();
        XTicks = BuildXTicks();
        LastPrice = view.Count > 0 ? new PriceTag(Y(data.Candles[view.End - 1].Close), Format(data.Candles[view.End - 1].Close), data.Candles[view.End - 1].IsUp) : null;
    }

    public CandleViewport View { get; }

    public double Width { get; }

    public double Height { get; }

    public double PlotLeft { get; }

    public double PlotTop { get; }

    public double PlotRight { get; }

    /// <summary>Gets the bottom of the price pane.</summary>
    public double PlotBottom { get; }

    public bool HasVolume { get; }

    public double VolumeTop { get; }

    public double VolumeBottom { get; }

    /// <summary>Gets the width of one candle's slot.</summary>
    public double SlotWidth { get; }

    public double BodyWidth { get; }

    public double YMin { get; }

    public double YMax { get; }

    public double YStep { get; }

    public IReadOnlyList<PlacedCandle> Candles { get; }

    public IReadOnlyList<IReadOnlyList<Pixel>> Overlays { get; }

    public IReadOnlyList<PlacedMarker> Markers { get; }

    public IReadOnlyList<PlacedLevel> Levels { get; }

    public IReadOnlyList<AxisTick> YTicks { get; }

    public IReadOnlyList<AxisTick> XTicks { get; }

    /// <summary>Gets the last close on screen, for the tag on the right axis; null without candles.</summary>
    public PriceTag? LastPrice { get; }

    /// <summary>Lays out <paramref name="data"/> with <paramref name="view"/> on screen (the latest <see cref="CandleChartData.InitialCount"/> when null).</summary>
    public static CandleLayout Compute(CandleChartData data, CandleViewport? view, double width, double height, ChartInsets? insets = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        int total = data.Candles.Count;
        CandleViewport v = view is { } given && given.Count > 0 && given.End <= total
            ? given
            : CandleViewport.Latest(total, data.InitialCount);
        return new CandleLayout(data, v, Math.Max(1, width), Math.Max(1, height), insets ?? ChartInsets.Axes);
    }

    /// <summary>The candle under <paramref name="x"/> with its numbers, or null without candles.</summary>
    public CandleHover? Hover(double x)
    {
        if (View.Count == 0)
        {
            return null;
        }

        int i = Math.Clamp(View.Start + (int)Math.Floor((x - PlotLeft) / SlotWidth), View.Start, View.End - 1);
        Candle c = _data.Candles[i];
        double reference = i > 0 ? _data.Candles[i - 1].Close : c.Open;
        string change = reference != 0 ? Fmt.ChangePct((decimal)((c.Close - reference) / Math.Abs(reference))) : "–";
        return new CandleHover(new Pixel(XOf(i), Y(c.Close)), HoverTime(c.At), Format(c.Open), Format(c.High), Format(c.Low), Format(c.Close), change,
            c.Volume is { } v ? Fmt.Count(v) : null, c.IsUp, i);
    }

    /// <summary>The price at a height in the price pane (for the crosshair's label).</summary>
    public double PriceAt(double y) => YMin + ((PlotBottom - y) / (PlotBottom - PlotTop) * (YMax - YMin));

    /// <summary>Where the mouse is across the plot, from 0 (left) to 1 (right): the anchor for zooming.</summary>
    public double Fraction(double x) => Math.Clamp((x - PlotLeft) / (PlotRight - PlotLeft), 0, 1);

    private bool InView(int index) => index >= View.Start && index < View.End;

    private double XOf(int index) => PlotLeft + ((index - View.Start + 0.5) * SlotWidth);

    private double Y(double value)
    {
        double t = (value - YMin) / (YMax - YMin);
        return PlotBottom - (Math.Clamp(t, -0.05, 1.05) * (PlotBottom - PlotTop));
    }

    private IEnumerable<double> VisibleValues()
    {
        for (int i = View.Start; i < View.End; i++)
        {
            yield return _data.Candles[i].High;
            yield return _data.Candles[i].Low;
        }

        foreach (ChartOverlay o in _data.Overlays)
        {
            foreach (ChartPoint p in o.Points)
            {
                if (InView(_data.IndexOf(p.At)))
                {
                    yield return p.Value;
                }
            }
        }

        foreach (ChartMarker m in _data.Markers)
        {
            if (InView(_data.IndexOf(m.At)))
            {
                yield return m.Value;
            }
        }

        foreach (ChartLevel l in _data.Levels)
        {
            yield return l.Value;
        }
    }

    private PlacedCandle Place(int i, long maxVolume)
    {
        Candle c = _data.Candles[i];
        double open = Y(c.Open);
        double close = Y(c.Close);
        double top = Math.Min(open, close);
        double bottom = Math.Max(open, close);
        if (bottom - top < MinBodyHeight)
        {
            double mid = (top + bottom) / 2;
            (top, bottom) = (mid - (MinBodyHeight / 2), mid + (MinBodyHeight / 2));
        }

        double? volumeTop = HasVolume && maxVolume > 0 && c.Volume is { } v
            ? VolumeBottom - ((double)v / maxVolume * (VolumeBottom - VolumeTop))
            : null;
        return new PlacedCandle(i, XOf(i), top, bottom, Y(c.High), Y(c.Low), c.IsUp, volumeTop);
    }

    /// <summary>An overlay's points on screen, one per slot (the last point in a slot wins, e.g. a daily average on week candles).</summary>
    private List<Pixel> PlaceOverlay(ChartOverlay overlay)
    {
        var pixels = new List<Pixel>();
        int lastIndex = -1;
        foreach (ChartPoint p in overlay.Points)
        {
            int i = _data.IndexOf(p.At);
            if (!InView(i) || double.IsNaN(p.Value))
            {
                continue;
            }

            var pixel = new Pixel(XOf(i), Y(p.Value));
            if (i == lastIndex)
            {
                pixels[^1] = pixel;
            }
            else
            {
                pixels.Add(pixel);
            }

            lastIndex = i;
        }

        return pixels;
    }

    private List<AxisTick> BuildYTicks()
    {
        var ticks = new List<AxisTick>();
        int decimals = ChartLayout.DecimalsFor(YStep);
        for (double v = Math.Ceiling(YMin / YStep) * YStep; v <= YMax + (YStep * 1e-9); v += YStep)
        {
            ticks.Add(new AxisTick(Y(v), _data.FormatValue is null ? Fmt.Amount((decimal)v, decimals) : _data.FormatValue(v)));
        }

        return ticks;
    }

    private List<AxisTick> BuildXTicks()
    {
        var ticks = new List<AxisTick>();
        int n = View.Count;
        if (n == 0)
        {
            return ticks;
        }

        int labels = Math.Min(n, Math.Clamp((int)((PlotRight - PlotLeft) / 110), 2, 8));
        TimeSpan span = _data.Candles[View.End - 1].At - _data.Candles[View.Start].At;
        for (int k = 0; k < labels; k++)
        {
            int i = labels == 1 ? View.Start : View.Start + (int)Math.Round(k * (n - 1) / (double)(labels - 1));
            string label = TickLabel(_data.Candles[i].At, span);
            if (ticks.Count == 0 || ticks[^1].Label != label)
            {
                ticks.Add(new AxisTick(XOf(i), label));
            }
        }

        return ticks;
    }

    private string TickLabel(DateTimeOffset at, TimeSpan span)
    {
        DateTimeOffset local = MarketTime.ToStockholm(at);
        return _data.Period.Unit switch
        {
            CandleUnit.Minute => local.ToString("HH:mm", CultureInfo.InvariantCulture),
            CandleUnit.Month => local.ToString("MMM yyyy", CultureInfo.InvariantCulture),
            _ => local.ToString(span <= TimeSpan.FromDays(120) ? "d MMM" : "MMM yyyy", CultureInfo.InvariantCulture),
        };
    }

    private string HoverTime(DateTimeOffset at)
    {
        DateTimeOffset local = MarketTime.ToStockholm(at);
        return _data.Period.Unit switch
        {
            CandleUnit.Minute => local.ToString("HH:mm", CultureInfo.InvariantCulture) + "–"
                                 + MarketTime.ToStockholm(_data.Period.Next(at)).ToString("HH:mm", CultureInfo.InvariantCulture),
            CandleUnit.Week => "Week of " + local.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
            CandleUnit.Month => local.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            _ => local.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture),
        };
    }

    private string Format(double value) => _data.FormatValue?.Invoke(value) ?? Fmt.Price((decimal)value);
}
