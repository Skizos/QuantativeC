using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QuantAnalyst.Desktop.Core.Charts;

namespace QuantAnalyst.Desktop.Controls;

/// <summary>
/// A candlestick chart in the app's style (docs/plans/12-charts-window.md): green candles that closed at or above
/// their open and red ones below, volume bars under them, moving averages as thin lines, your trades as ▲/▼, levels as
/// dashed lines and the last price on the right axis. Hovering shows a crosshair, the price at the mouse and the
/// candle's date, open, high, low, close, change and volume. The mouse wheel zooms around the mouse, dragging pans and
/// a double-click goes back to the range. Every position comes from <see cref="CandleLayout"/> in Desktop.Core, which
/// is tested; this class only draws and turns the mouse into viewport changes.
/// </summary>
public sealed class CandleChart : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(CandleChartData), typeof(CandleChart),
        new FrameworkPropertyMetadata(CandleChartData.Empty, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(CandleChart), new FrameworkPropertyMetadata("No candles yet", FrameworkPropertyMetadataOptions.AffectsRender));

    private const double ZoomStep = 0.85;
    private const double AxisText = 12;

    private CandleViewport? _view;
    private CandleLayout? _layout;
    private Point? _mouse;
    private Point? _dragFrom;
    private CandleViewport _dragView;

    public CandleChart()
    {
        Focusable = false;
        Cursor = Cursors.Cross;
    }

    public CandleChartData? Data
    {
        get => (CandleChartData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>Gets or sets what the chart says while it has no candles.</summary>
    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        if (_dragFrom is { } from && _layout is { SlotWidth: > 0 } layout && Data is { } data)
        {
            int candles = (int)Math.Round((from.X - _mouse.Value.X) / layout.SlotWidth);
            _view = _dragView.Pan(data.Candles.Count, candles);
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            _view = null; // back to the range
            _dragFrom = null;
            InvalidateVisual();
            return;
        }

        if (_layout is not null)
        {
            _dragFrom = e.GetPosition(this);
            _dragView = _layout.View;
            CaptureMouse();
            Cursor = Cursors.SizeWE;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragFrom = null;
        ReleaseMouseCapture();
        Cursor = Cursors.Cross;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseWheel(e);
        if (_layout is null || Data is not { IsEmpty: false } data)
        {
            return;
        }

        double factor = e.Delta > 0 ? ZoomStep : 1 / ZoomStep;
        _view = _layout.View.Zoom(data.Candles.Count, factor, _layout.Fraction(e.GetPosition(this).X));
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        DrawingContext dc = drawingContext;
        var box = new Rect(RenderSize);
        dc.DrawRectangle(Brushes.Transparent, null, box); // so the whole area gets the mouse
        _layout = null;
        if (box.Width < 40 || box.Height < 40)
        {
            return;
        }

        CandleChartData data = Data ?? CandleChartData.Empty;
        Brush muted = Res("InkMutedBrush", Brushes.Gray);
        if (data.IsEmpty)
        {
            FormattedText empty = Text(EmptyText, 14, muted);
            dc.DrawText(empty, new Point((box.Width - empty.Width) / 2, (box.Height - empty.Height) / 2));
            return;
        }

        CandleLayout layout = CandleLayout.Compute(data, _view, box.Width, box.Height);
        _layout = layout;
        Brush up = Res("PositiveBrush", Brushes.Green);
        Brush down = Res("NegativeBrush", Brushes.Red);

        DrawAxes(dc, layout, muted);
        DrawVolume(dc, layout, up, down);
        DrawLevels(dc, layout);
        DrawCandles(dc, layout, up, down);
        DrawOverlays(dc, layout);
        DrawMarkers(dc, layout, up, down);
        DrawLastPrice(dc, layout, up, down);
        if (_mouse is { } m && _dragFrom is null && m.X >= layout.PlotLeft && m.X <= layout.PlotRight && m.Y >= layout.PlotTop && m.Y <= layout.VolumeBottom)
        {
            DrawHover(dc, layout, m, up, down, muted);
        }
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (CandleChart)d;
        var before = e.OldValue as CandleChartData;
        var after = e.NewValue as CandleChartData;
        chart._view = before is not null && after is not null && before.Key.Length > 0 && before.Key == after.Key && chart._view is { } view
            ? view.Follow(before.Candles.Count, after.Candles.Count) // the same candles, more of them: keep the zoom, follow the latest
            : null;
    }

    private static double Snap(double v) => Math.Round(v) + 0.5;

    private void DrawAxes(DrawingContext dc, CandleLayout layout, Brush muted)
    {
        var grid = new Pen(Res("LineBrush", Brushes.Gainsboro), 1);
        foreach (AxisTick t in layout.YTicks)
        {
            dc.DrawLine(grid, new Point(layout.PlotLeft, Snap(t.Position)), new Point(layout.PlotRight, Snap(t.Position)));
            FormattedText label = Text(t.Label, AxisText, muted);
            dc.DrawText(label, new Point(layout.PlotRight + 8, t.Position - (label.Height / 2)));
        }

        if (layout.HasVolume)
        {
            dc.DrawLine(grid, new Point(layout.PlotLeft, Snap(layout.VolumeBottom)), new Point(layout.PlotRight, Snap(layout.VolumeBottom)));
            FormattedText label = Text("Volume", AxisText - 0.5, muted);
            dc.DrawText(label, new Point(layout.PlotRight + 8, layout.VolumeTop));
        }

        foreach (AxisTick t in layout.XTicks)
        {
            FormattedText label = Text(t.Label, AxisText, muted);
            double left = Math.Clamp(t.Position - (label.Width / 2), 0, Math.Max(0, layout.PlotRight - label.Width));
            dc.DrawText(label, new Point(left, layout.VolumeBottom + 6));
        }
    }

    private static void DrawVolume(DrawingContext dc, CandleLayout layout, Brush up, Brush down)
    {
        if (!layout.HasVolume)
        {
            return;
        }

        Brush upSoft = Faded(up, 90);
        Brush downSoft = Faded(down, 90);
        double width = Math.Max(1, layout.BodyWidth);
        foreach (PlacedCandle c in layout.Candles)
        {
            if (c.VolumeTop is { } top && layout.VolumeBottom - top > 0.2)
            {
                dc.DrawRectangle(c.IsUp ? upSoft : downSoft, null, new Rect(c.X - (width / 2), top, width, layout.VolumeBottom - top));
            }
        }
    }

    private void DrawLevels(DrawingContext dc, CandleLayout layout)
    {
        Brush info = Res("InfoBrush", Brushes.SteelBlue);
        var pen = new Pen(info, 1) { DashStyle = new DashStyle([5, 3], 0) };
        foreach (PlacedLevel level in layout.Levels)
        {
            if (level.Y < layout.PlotTop || level.Y > layout.PlotBottom)
            {
                continue;
            }

            dc.DrawLine(pen, new Point(layout.PlotLeft, Snap(level.Y)), new Point(layout.PlotRight, Snap(level.Y)));
            FormattedText label = Text(level.Level.Label, AxisText, info);
            dc.DrawText(label, new Point(layout.PlotLeft + 6, level.Y - label.Height - 1));
        }
    }

    private static void DrawCandles(DrawingContext dc, CandleLayout layout, Brush up, Brush down)
    {
        var upPen = new Pen(up, 1);
        var downPen = new Pen(down, 1);
        foreach (PlacedCandle c in layout.Candles)
        {
            Pen pen = c.IsUp ? upPen : downPen;
            double x = Snap(c.X);
            dc.DrawLine(pen, new Point(x, c.WickTop), new Point(x, c.WickBottom));
            if (layout.BodyWidth >= 2)
            {
                dc.DrawRectangle(c.IsUp ? up : down, null, new Rect(Math.Round(c.X - (layout.BodyWidth / 2)), c.BodyTop, layout.BodyWidth, Math.Max(1, c.BodyBottom - c.BodyTop)));
            }
        }
    }

    private void DrawOverlays(DrawingContext dc, CandleLayout layout)
    {
        Brush[] brushes = [Res("InfoBrush", Brushes.SteelBlue), Res("WarningBrush", Brushes.DarkOrange)];
        for (int i = 0; i < layout.Overlays.Count; i++)
        {
            IReadOnlyList<Pixel> points = layout.Overlays[i];
            if (points.Count < 2)
            {
                continue;
            }

            var geometry = new StreamGeometry();
            using (StreamGeometryContext g = geometry.Open())
            {
                g.BeginFigure(new Point(points[0].X, points[0].Y), false, false);
                g.PolyLineTo([.. points.Skip(1).Select(p => new Point(p.X, p.Y))], true, true);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(brushes[i % brushes.Length], 1.5) { LineJoin = PenLineJoin.Round }, geometry);
        }
    }

    private static void DrawMarkers(DrawingContext dc, CandleLayout layout, Brush up, Brush down)
    {
        var outline = new Pen(Brushes.White, 1.5);
        foreach (PlacedMarker m in layout.Markers)
        {
            bool buy = m.Marker.Kind == MarkerKind.Buy;
            double x = m.At.X;
            double y = m.At.Y;
            const double s = 7.5;
            var triangle = new StreamGeometry();
            using (StreamGeometryContext g = triangle.Open())
            {
                // ▲ under the fill price for a buy, ▼ over it for a sell; the tip touches the price.
                if (buy)
                {
                    g.BeginFigure(new Point(x, y), true, true);
                    g.PolyLineTo([new Point(x + s, y + (s * 1.6)), new Point(x - s, y + (s * 1.6))], true, true);
                }
                else
                {
                    g.BeginFigure(new Point(x, y), true, true);
                    g.PolyLineTo([new Point(x + s, y - (s * 1.6)), new Point(x - s, y - (s * 1.6))], true, true);
                }
            }

            triangle.Freeze();
            dc.DrawGeometry(buy ? up : down, outline, triangle);
        }
    }

    private void DrawLastPrice(DrawingContext dc, CandleLayout layout, Brush up, Brush down)
    {
        if (layout.LastPrice is not { } tag || tag.Y < layout.PlotTop - 2 || tag.Y > layout.PlotBottom + 2)
        {
            return;
        }

        Brush tone = tag.IsUp ? up : down;
        dc.DrawLine(new Pen(tone, 1) { DashStyle = new DashStyle([1, 3], 0) }, new Point(layout.PlotLeft, Snap(tag.Y)), new Point(layout.PlotRight, Snap(tag.Y)));
        FormattedText text = Text(tag.Label, AxisText, Brushes.White, bold: true);
        var rect = new Rect(layout.PlotRight + 2, tag.Y - (text.Height / 2) - 2, text.Width + 12, text.Height + 4);
        dc.DrawRoundedRectangle(tone, null, rect, 4, 4);
        dc.DrawText(text, new Point(rect.Left + 6, rect.Top + 2));
    }

    private void DrawHover(DrawingContext dc, CandleLayout layout, Point mouse, Brush up, Brush down, Brush muted)
    {
        if (layout.Hover(mouse.X) is not { } hover)
        {
            return;
        }

        var cross = new Pen(muted, 1) { DashStyle = new DashStyle([2, 3], 0) };
        dc.DrawLine(cross, new Point(Snap(hover.At.X), layout.PlotTop), new Point(Snap(hover.At.X), layout.VolumeBottom));
        Brush ink = Res("InkBrush", Brushes.Black);
        if (mouse.Y <= layout.PlotBottom)
        {
            dc.DrawLine(cross, new Point(layout.PlotLeft, Snap(mouse.Y)), new Point(layout.PlotRight, Snap(mouse.Y)));
            string price = Data?.FormatValue?.Invoke(layout.PriceAt(mouse.Y)) ?? layout.PriceAt(mouse.Y).ToString("0.00", CultureInfo.InvariantCulture);
            FormattedText label = Text(price, AxisText, Brushes.White, bold: true);
            var rect = new Rect(layout.PlotRight + 2, mouse.Y - (label.Height / 2) - 2, label.Width + 12, label.Height + 4);
            dc.DrawRoundedRectangle(ink, null, rect, 4, 4);
            dc.DrawText(label, new Point(rect.Left + 6, rect.Top + 2));
        }

        // The candle's numbers in a strip at the top left, so the bubble never hides the candles.
        Brush tone = hover.IsUp ? up : down;
        Brush secondary = Res("InkSecondaryBrush", Brushes.DimGray);
        var parts = new List<(string Text, Brush Brush, bool Bold)>
        {
            (hover.Time + "   ", Res("InkBrush", Brushes.Black), true),
            ("O ", secondary, false), (hover.Open + "  ", ink, true),
            ("H ", secondary, false), (hover.High + "  ", ink, true),
            ("L ", secondary, false), (hover.Low + "  ", ink, true),
            ("C ", secondary, false), (hover.Close + "  ", ink, true),
            (hover.Change + "  ", tone, true),
        };
        if (hover.Volume is { } volume)
        {
            parts.Add(("Vol ", secondary, false));
            parts.Add((volume, ink, true));
        }

        FormattedText[] texts = [.. parts.Select(p => Text(p.Text, 13, p.Brush, p.Bold))];
        double width = texts.Sum(t => t.WidthIncludingTrailingWhitespace) + 16;
        double height = texts.Max(t => t.Height) + 8;
        dc.DrawRoundedRectangle(Faded(Res("SurfaceBrush", Brushes.White), 235), new Pen(Res("LineBrush", Brushes.Gainsboro), 1),
            new Rect(layout.PlotLeft + 4, layout.PlotTop + 2, width, height), 6, 6);
        double x = layout.PlotLeft + 12;
        foreach (FormattedText t in texts)
        {
            dc.DrawText(t, new Point(x, layout.PlotTop + 6));
            x += t.WidthIncludingTrailingWhitespace;
        }

        // A trade under the mouse: its label next to it.
        PlacedMarker? near = layout.Markers.Where(m => Math.Abs(m.At.X - mouse.X) <= 10 && Math.Abs(m.At.Y - mouse.Y) <= 14).FirstOrDefault();
        if (near is not null)
        {
            FormattedText label = Text(near.Marker.Label, 12.5, Brushes.White);
            double left = Math.Clamp(near.At.X + 12, layout.PlotLeft, Math.Max(layout.PlotLeft, layout.PlotRight - label.Width - 16));
            var rect = new Rect(left, near.At.Y - (label.Height / 2) - 4, label.Width + 16, label.Height + 8);
            dc.DrawRoundedRectangle(ink, null, rect, 6, 6);
            dc.DrawText(label, new Point(rect.Left + 8, rect.Top + 4));
        }
    }

    private static Brush Faded(Brush brush, byte alpha)
    {
        if (brush is not SolidColorBrush solid)
        {
            return brush;
        }

        var faded = new SolidColorBrush(Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B));
        faded.Freeze();
        return faded;
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
