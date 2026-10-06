using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace QuantAnalyst.Desktop.Controls;

/// <summary>
/// A line icon from Themes/Theme.xaml ("Icon…" geometries drawn on a 24 × 24 grid), scaled to its size and drawn in
/// the surrounding text colour, so it follows a button's or a selected navigation item's foreground by itself.
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(1.8, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Gets or sets the drawing, in 24 × 24 grid units.</summary>
    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Gets or sets the line width in grid units (1.8 by default).</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(18, availableSize.Width), Math.Min(18, availableSize.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (Data is not { } data || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        double scale = Math.Min(ActualWidth, ActualHeight) / 24.0;
        var pen = new Pen(Foreground, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        drawingContext.PushTransform(new TranslateTransform((ActualWidth - (24 * scale)) / 2, (ActualHeight - (24 * scale)) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(null, pen, data);
        drawingContext.Pop();
        drawingContext.Pop();
    }
}
