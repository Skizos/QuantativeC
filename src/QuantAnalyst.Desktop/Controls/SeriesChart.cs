using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QuantAnalyst.Desktop.Core.Charts;

namespace QuantAnalyst.Desktop.Controls;

/// <summary>
/// A line chart in the app's style (docs/plans/11-app-redesign.md): the main line green when it is at or above its
/// baseline and red below, a soft area under it, moving averages as thin lines, fills as dots, limits as dashed levels,
/// values at the right and times below. Hovering shows a crosshair and a bubble with the time, value and change.
/// <see cref="Compact"/> draws a sparkline without axes. Every position comes from <see cref="ChartLayout"/> in
/// Desktop.Core, which is tested; this class only draws.
/// </summary>
public sealed class SeriesChart : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(ChartData), typeof(SeriesChart), new FrameworkPropertyMetadata(ChartData.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(SeriesChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(SeriesChart), new FrameworkPropertyMetadata("No data yet", FrameworkPropertyMetadataOptions.AffectsRender));

    private double? _hoverX;

    public ChartData? Data
    {
        get => (ChartData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>Gets or sets a value indicating whether this is a sparkline: no axes, no labels, no hover.</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>Gets or sets what the chart says while it has no points.</summary>
    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        if (!Compact)
        {
            _hoverX = e.GetPosition(this).X;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverX = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        DrawingContext dc = drawingContext;
        var box = new Rect(RenderSize);
        dc.DrawRectangle(Brushes.Transparent, null, box); // so the whole area gets the mouse
        if (box.Width < 4 || box.Height < 4)
        {
            return;
        }

        ChartData data = Data ?? ChartData.Empty;
        if (data.IsEmpty)
        {
            if (!Compact)
            {
                FormattedText empty = Text(EmptyText, 13, Res("InkMutedBrush", Brushes.Gray));
                dc.DrawText(empty, new Point((box.Width - empty.Width) / 2, (box.Height - empty.Height) / 2));
            }

            return;
        }

        ChartLayout layout = ChartLayout.Compute(data, box.Width, box.Height, Compact ? ChartInsets.None : ChartInsets.Axes);
        Brush tone = data.IsUp ? Res("PositiveBrush", Brushes.Green) : Res("NegativeBrush", Brushes.Red);
        Brush muted = Res("InkMutedBrush", Brushes.Gray);

        if (!Compact)
        {
            DrawAxes(dc, layout, muted);
        }

        if (layout.BaselineY is { } baseY)
        {
            var dashed = new Pen(muted, 1) { DashStyle = new DashStyle([3, 3], 0) };
            dc.DrawLine(dashed, new Point(layout.PlotLeft, Snap(baseY)), new Point(layout.PlotRight, Snap(baseY)));
        }

        foreach (PlacedLevel level in layout.Levels)
        {
            Brush info = Res("InfoBrush", Brushes.SteelBlue);
            dc.DrawLine(new Pen(info, 1) { DashStyle = new DashStyle([5, 3], 0) }, new Point(layout.PlotLeft, Snap(level.Y)), new Point(layout.PlotRight, Snap(level.Y)));
            if (!Compact)
            {
                FormattedText label = Text(level.Level.Label, 11, info);
                dc.DrawText(label, new Point(layout.PlotRight - label.Width - 4, level.Y - label.Height - 1));
            }
        }

        DrawArea(dc, layout, tone);
        DrawLine(dc, layout.Main, new Pen(tone, Compact ? 1.6 : 2) { LineJoin = PenLineJoin.Round });

        Brush[] overlayBrushes = [Res("InfoBrush", Brushes.SteelBlue), Res("WarningBrush", Brushes.DarkOrange)];
        for (int i = 0; i < layout.Overlays.Count; i++)
        {
            DrawLine(dc, layout.Overlays[i], new Pen(overlayBrushes[i % overlayBrushes.Length], 1.4) { LineJoin = PenLineJoin.Round });
        }

        foreach (PlacedMarker m in layout.Markers)
        {
            Brush fill = m.Marker.Kind == MarkerKind.Buy ? Res("PositiveBrush", Brushes.Green) : Res("NegativeBrush", Brushes.Red);
            dc.DrawEllipse(fill, new Pen(Brushes.White, 2), new Point(m.At.X, m.At.Y), 5.5, 5.5);
        }

        if (!Compact && _hoverX is { } x && layout.Hover(x) is { } hover)
        {
            DrawHover(dc, layout, hover, tone, muted);
        }
    }

    private static double Snap(double v) => Math.Round(v) + 0.5;

    private static void DrawLine(DrawingContext dc, IReadOnlyList<Pixel> points, Pen pen)
    {
        if (points.Count < 2)
        {
            if (points.Count == 1)
            {
                dc.DrawEllipse(pen.Brush, null, new Point(points[0].X, points[0].Y), 2.5, 2.5);
            }

            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(new Point(points[0].X, points[0].Y), false, false);
            g.PolyLineTo([.. points.Skip(1).Select(p => new Point(p.X, p.Y))], true, true);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void DrawArea(DrawingContext dc, ChartLayout layout, Brush tone)
    {
        if (layout.Main.Count < 2 || tone is not SolidColorBrush solid)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(new Point(layout.Main[0].X, layout.PlotBottom), true, true);
            g.PolyLineTo([.. layout.Main.Select(p => new Point(p.X, p.Y)), new Point(layout.Main[^1].X, layout.PlotBottom)], true, true);
        }

        geometry.Freeze();
        Color c = solid.Color;
        var fill = new LinearGradientBrush(Color.FromArgb(56, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, geometry);
    }

    private void DrawAxes(DrawingContext dc, ChartLayout layout, Brush muted)
    {
        var grid = new Pen(Res("LineBrush", Brushes.Gainsboro), 1);
        foreach (AxisTick t in layout.YTicks)
        {
            dc.DrawLine(grid, new Point(layout.PlotLeft, Snap(t.Position)), new Point(layout.PlotRight, Snap(t.Position)));
            FormattedText label = Text(t.Label, 11, muted);
            dc.DrawText(label, new Point(layout.PlotRight + 8, t.Position - (label.Height / 2)));
        }

        foreach (AxisTick t in layout.XTicks)
        {
            FormattedText label = Text(t.Label, 11, muted);
            double left = Math.Clamp(t.Position - (label.Width / 2), 0, Math.Max(0, layout.PlotRight - label.Width));
            dc.DrawText(label, new Point(left, layout.PlotBottom + 6));
        }
    }

    private void DrawHover(DrawingContext dc, ChartLayout layout, HoverInfo hover, Brush tone, Brush muted)
    {
        var cross = new Pen(muted, 1) { DashStyle = new DashStyle([2, 3], 0) };
        dc.DrawLine(cross, new Point(Snap(hover.At.X), layout.PlotTop), new Point(Snap(hover.At.X), layout.PlotBottom));
        dc.DrawEllipse(tone, new Pen(Brushes.White, 2), new Point(hover.At.X, hover.At.Y), 5, 5);

        Brush white = Brushes.White;
        FormattedText value = Text(hover.Value, 13, white, bold: true);
        FormattedText time = Text(hover.Time, 11, Res("LineStrongBrush", Brushes.LightGray));
        FormattedText? change = hover.Change is { } ch ? Text(ch, 11.5, white) : null;
        double width = Math.Max(value.Width + (change is null ? 0 : change.Width + 10), time.Width) + 20;
        double height = value.Height + time.Height + 14;
        double left = Math.Clamp(hover.At.X - (width / 2), layout.PlotLeft, Math.Max(layout.PlotLeft, layout.PlotRight - width));
        double top = hover.At.Y - height - 14 < layout.PlotTop ? hover.At.Y + 14 : hover.At.Y - height - 14;
        top = Math.Clamp(top, 0, Math.Max(0, layout.Height - height));

        dc.DrawRoundedRectangle(Res("InkBrush", Brushes.Black), null, new Rect(left, top, width, height), 8, 8);
        dc.DrawText(value, new Point(left + 10, top + 6));
        if (change is not null)
        {
            dc.DrawText(change, new Point(left + 10 + value.Width + 10, top + 6 + ((value.Height - change.Height) / 2)));
        }

        dc.DrawText(time, new Point(left + 10, top + 8 + value.Height));
    }

    private FormattedText Text(string text, double size, Brush brush, bool bold = false)
    {
        FontFamily family = Res<FontFamily>("UiFont") ?? new FontFamily("Segoe UI");
        var typeface = new Typeface(family, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private Brush Res(string key, Brush fallback) => Res<Brush>(key) ?? fallback;

    private T? Res<T>(string key)
        where T : class => TryFindResource(key) as T;
}
