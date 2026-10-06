using System.Globalization;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Desktop.Core.Charts;

/// <summary>A point in pixels inside the control.</summary>
public readonly record struct Pixel(double X, double Y);

/// <summary>An axis label and where it goes (x for the time axis, y for the value axis).</summary>
public sealed record AxisTick(double Position, string Label);

public sealed record PlacedMarker(Pixel At, ChartMarker Marker);

public sealed record PlacedLevel(double Y, ChartLevel Level);

/// <summary>What the hover bubble shows for the point under the mouse.</summary>
public sealed record HoverInfo(Pixel At, string Time, string Value, string? Change, int Index);

/// <summary>Sizes around the plot: room for the value labels at the right and the time labels below.</summary>
public sealed record ChartInsets(double Left, double Top, double Right, double Bottom)
{
    /// <summary>A full chart: values at the right, times below.</summary>
    public static ChartInsets Axes { get; } = new(4, 10, 64, 26);

    /// <summary>A sparkline: no labels, a little air.</summary>
    public static ChartInsets None { get; } = new(1, 3, 1, 3);
}

/// <summary>
/// Where everything of a <see cref="ChartData"/> goes in a box of a given size: the lines' pixels, the axis ticks at
/// "nice" steps (1, 2, 2.5 or 5 × 10ⁿ), the baseline, markers and levels. Pure maths, so it is tested on Linux; the
/// WPF control only draws what this says (docs/plans/11-app-redesign.md).
/// </summary>
public sealed class ChartLayout
{
    private readonly ChartData _data;
    private readonly double _xFrom;
    private readonly double _xTo;

    private ChartLayout(ChartData data, double width, double height, ChartInsets insets)
    {
        _data = data;
        Width = width;
        Height = height;
        PlotLeft = insets.Left;
        PlotTop = insets.Top;
        PlotRight = Math.Max(insets.Left + 1, width - insets.Right);
        PlotBottom = Math.Max(insets.Top + 1, height - insets.Bottom);

        (_xFrom, _xTo) = XDomain(data);
        (YMin, YMax, YStep) = YDomain(data);
        Main = [.. data.Main.Select((p, i) => ToPixel(i, p))];
        Overlays = [.. data.Overlays.Select(o => (IReadOnlyList<Pixel>)[.. o.Points.Select(p => ToPixel(IndexOf(p.At), p))])];
        BaselineY = data.Baseline is { } b ? Y(b) : null;
        Markers = [.. data.Markers.Select(m => new PlacedMarker(new Pixel(X(m.At), Y(m.Value)), m))];
        Levels = [.. data.Levels.Select(l => new PlacedLevel(Y(l.Value), l))];
        YTicks = BuildYTicks();
        XTicks = BuildXTicks();
    }

    public double Width { get; }

    public double Height { get; }

    public double PlotLeft { get; }

    public double PlotTop { get; }

    public double PlotRight { get; }

    public double PlotBottom { get; }

    public double YMin { get; }

    public double YMax { get; }

    public double YStep { get; }

    public IReadOnlyList<Pixel> Main { get; }

    public IReadOnlyList<IReadOnlyList<Pixel>> Overlays { get; }

    public double? BaselineY { get; }

    public IReadOnlyList<PlacedMarker> Markers { get; }

    public IReadOnlyList<PlacedLevel> Levels { get; }

    public IReadOnlyList<AxisTick> YTicks { get; }

    public IReadOnlyList<AxisTick> XTicks { get; }

    public static ChartLayout Compute(ChartData data, double width, double height, ChartInsets? insets = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new ChartLayout(data, Math.Max(1, width), Math.Max(1, height), insets ?? ChartInsets.Axes);
    }

    /// <summary>
    /// A "nice" step for about <paramref name="ticks"/> intervals across <paramref name="range"/>: 1, 2, 2.5 or 5 × 10ⁿ.
    /// </summary>
    public static double NiceStep(double range, int ticks = 4)
    {
        if (!(range > 0) || double.IsInfinity(range))
        {
            return 1;
        }

        double rough = range / Math.Max(1, ticks);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double fraction = rough / magnitude;
        double nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    /// <summary>The decimals a step needs to tell its ticks apart: 0.25 → 2, 5 → 0, 0.1 → 1.</summary>
    public static int DecimalsFor(double step)
    {
        for (int d = 0; d <= 4; d++)
        {
            double scaled = step * Math.Pow(10, d);
            if (Math.Abs(scaled - Math.Round(scaled)) < 1e-9)
            {
                return d;
            }
        }

        return 4;
    }

    /// <summary>The point under the mouse: the main series' point nearest to <paramref name="x"/>, or null without points.</summary>
    public HoverInfo? Hover(double x)
    {
        if (Main.Count == 0)
        {
            return null;
        }

        int best = 0;
        for (int i = 1; i < Main.Count; i++)
        {
            if (Math.Abs(Main[i].X - x) < Math.Abs(Main[best].X - x))
            {
                best = i;
            }
        }

        ChartPoint p = _data.Main[best];
        double reference = _data.Baseline ?? _data.Main[0].Value;
        string? change = reference != 0
            ? Presentation.Fmt.ChangePct((decimal)((p.Value - reference) / Math.Abs(reference)))
            : null;
        return new HoverInfo(Main[best], TimeLabel(p.At, detailed: true), Format(p.Value), change, best);
    }

    private static (double Min, double Max, double Step) YDomain(ChartData data)
    {
        IEnumerable<double> values = data.Main.Select(p => p.Value)
            .Concat(data.Overlays.SelectMany(o => o.Points).Select(p => p.Value))
            .Concat(data.Markers.Select(m => m.Value))
            .Concat(data.Levels.Select(l => l.Value));
        if (data.Baseline is { } b)
        {
            values = values.Append(b);
        }

        return ValueScale(values);
    }

    /// <summary>
    /// The value axis for <paramref name="values"/>: their range with 8 % air on both sides (a flat line gets ±1 %), and a
    /// nice step that gives at least three labelled lines. Shared by the line and the candle chart.
    /// </summary>
    internal static (double Min, double Max, double Step) ValueScale(IEnumerable<double> values)
    {
        double[] all = [.. values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v))];
        if (all.Length == 0)
        {
            return (0, 1, 0.25);
        }

        double min = all.Min();
        double max = all.Max();
        if (max - min < 1e-12)
        {
            double pad = Math.Abs(min) > 1e-9 ? Math.Abs(min) * 0.01 : 1;
            min -= pad;
            max += pad;
        }

        double margin = (max - min) * 0.08;
        min -= margin;
        max += margin;

        // At least three labelled lines: a narrow range can fall between two coarse steps (e.g. 70,5 and 71,0 only).
        double step = NiceStep(max - min);
        for (int n = 5; n <= 10 && Math.Floor(max / step) - Math.Ceiling(min / step) + 1 < 3; n++)
        {
            step = NiceStep(max - min, n);
        }

        return (min, max, step);
    }

    private static (double From, double To) XDomain(ChartData data)
    {
        if (data.Axis == TimeAxis.Daily)
        {
            return (0, Math.Max(1, data.Main.Count - 1));
        }

        if (data.Window is { } w && w.To > w.From)
        {
            return (w.From.ToUnixTimeMilliseconds(), w.To.ToUnixTimeMilliseconds());
        }

        if (data.Main.Count == 0)
        {
            return (0, 1);
        }

        double from = data.Main[0].At.ToUnixTimeMilliseconds();
        double to = data.Main[^1].At.ToUnixTimeMilliseconds();
        return to > from ? (from, to) : (from - 60_000, to + 60_000);
    }

    private Pixel ToPixel(int index, ChartPoint p) => new(_data.Axis == TimeAxis.Daily ? XAt(index) : X(p.At), Y(p.Value));

    /// <summary>Where a time goes: by clock time intraday, by the nearest main point's slot for daily history.</summary>
    private double X(DateTimeOffset at) => _data.Axis == TimeAxis.Daily ? XAt(IndexOf(at)) : XAt(at.ToUnixTimeMilliseconds());

    private double XAt(double value)
    {
        if (_data.Axis == TimeAxis.Daily && _data.Main.Count == 1)
        {
            return (PlotLeft + PlotRight) / 2;
        }

        double t = (value - _xFrom) / (_xTo - _xFrom);
        return PlotLeft + (Math.Clamp(t, 0, 1) * (PlotRight - PlotLeft));
    }

    private double Y(double value)
    {
        double t = (value - YMin) / (YMax - YMin);
        return PlotBottom - (Math.Clamp(t, -0.05, 1.05) * (PlotBottom - PlotTop));
    }

    /// <summary>The main series' slot for a time (the last point at or before it); for daily overlays and markers.</summary>
    private int IndexOf(DateTimeOffset at)
    {
        IReadOnlyList<ChartPoint> main = _data.Main;
        if (main.Count == 0)
        {
            return 0;
        }

        int lo = 0;
        int hi = main.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (main[mid].At <= at)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private List<AxisTick> BuildYTicks()
    {
        var ticks = new List<AxisTick>();
        int decimals = DecimalsFor(YStep);
        for (double v = Math.Ceiling(YMin / YStep) * YStep; v <= YMax + (YStep * 1e-9); v += YStep)
        {
            ticks.Add(new AxisTick(Y(v), _data.FormatValue is null ? Presentation.Fmt.Amount((decimal)v, decimals) : _data.FormatValue(v)));
        }

        return ticks;
    }

    private List<AxisTick> BuildXTicks()
    {
        var ticks = new List<AxisTick>();
        if (_data.Main.Count == 0 && _data.Window is null)
        {
            return ticks;
        }

        if (_data.Axis == TimeAxis.Daily)
        {
            int n = _data.Main.Count;
            int labels = Math.Min(5, n);
            for (int k = 0; k < labels; k++)
            {
                int i = labels == 1 ? 0 : (int)Math.Round(k * (n - 1) / (double)(labels - 1));
                ticks.Add(new AxisTick(XAt(i), TimeLabel(_data.Main[i].At, detailed: false)));
            }

            return ticks;
        }

        // Intraday: whole hours (or half hours on a short window), at most seven labels.
        var from = DateTimeOffset.FromUnixTimeMilliseconds((long)_xFrom);
        var to = DateTimeOffset.FromUnixTimeMilliseconds((long)_xTo);
        TimeSpan span = to - from;
        TimeSpan step = span <= TimeSpan.FromHours(3) ? TimeSpan.FromMinutes(30) : span <= TimeSpan.FromHours(7) ? TimeSpan.FromHours(1) : TimeSpan.FromHours(2);
        DateTimeOffset local = MarketTime.ToStockholm(from);
        DateTimeOffset first = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset);
        while (first < local)
        {
            first += step;
        }

        for (DateTimeOffset t = first; t <= MarketTime.ToStockholm(to); t += step)
        {
            ticks.Add(new AxisTick(XAt(t.ToUnixTimeMilliseconds()), t.ToString("HH:mm", CultureInfo.InvariantCulture)));
        }

        return ticks;
    }

    private string TimeLabel(DateTimeOffset at, bool detailed)
    {
        DateTimeOffset local = MarketTime.ToStockholm(at);
        if (_data.Axis == TimeAxis.Intraday)
        {
            return local.ToString(detailed ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
        }

        if (detailed)
        {
            return local.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
        }

        TimeSpan span = _data.Main.Count > 1 ? _data.Main[^1].At - _data.Main[0].At : TimeSpan.Zero;
        return local.ToString(span <= TimeSpan.FromDays(100) ? "d MMM" : "MMM yyyy", CultureInfo.InvariantCulture);
    }

    private string Format(double value) => _data.FormatValue?.Invoke(value) ?? Presentation.Fmt.Price((decimal)value);
}
