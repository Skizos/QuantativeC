using System.Windows;
using System.Windows.Controls;
using QuantAnalyst.Desktop.Core.Presentation;

namespace QuantAnalyst.Desktop.Controls;

/// <summary>
/// Two parts (a list and its details, a form and its result) side by side when there is room, else stacked with the
/// first on top (docs/plans/14-adaptive-pages.md). The sizes come from <see cref="Adaptive"/> in Desktop.Core, which is
/// tested; this panel only measures and places. Extra children are ignored.
/// </summary>
public sealed class AdaptiveSplit : Panel
{
    public static readonly DependencyProperty BreakpointProperty = Register(nameof(Breakpoint), 720d);
    public static readonly DependencyProperty FirstWidthProperty = Register(nameof(FirstWidth), 0d);
    public static readonly DependencyProperty FirstShareProperty = Register(nameof(FirstShare), 0.5);
    public static readonly DependencyProperty SpacingProperty = Register(nameof(Spacing), 16d);
    public static readonly DependencyProperty StackedFirstMaxHeightProperty = Register(nameof(StackedFirstMaxHeight), double.PositiveInfinity);
    public static readonly DependencyProperty StackedSecondMinHeightProperty = Register(nameof(StackedSecondMinHeight), 200d);

    private double _firstDesired;
    private double _secondDesired;

    /// <summary>Gets or sets the narrowest width that still shows the parts side by side.</summary>
    public double Breakpoint
    {
        get => (double)GetValue(BreakpointProperty);
        set => SetValue(BreakpointProperty, value);
    }

    /// <summary>Gets or sets the first part's width side by side (0: use <see cref="FirstShare"/>).</summary>
    public double FirstWidth
    {
        get => (double)GetValue(FirstWidthProperty);
        set => SetValue(FirstWidthProperty, value);
    }

    public double FirstShare
    {
        get => (double)GetValue(FirstShareProperty);
        set => SetValue(FirstShareProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Gets or sets the first part's greatest height when stacked (a list then scrolls).</summary>
    public double StackedFirstMaxHeight
    {
        get => (double)GetValue(StackedFirstMaxHeightProperty);
        set => SetValue(StackedFirstMaxHeightProperty, value);
    }

    public double StackedSecondMinHeight
    {
        get => (double)GetValue(StackedSecondMinHeightProperty);
        set => SetValue(StackedSecondMinHeightProperty, value);
    }

    private SplitSpec Spec => new(Breakpoint, FirstWidth, FirstShare, Spacing, StackedFirstMaxHeight, StackedSecondMinHeight);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count < 2)
        {
            return MeasureAll(availableSize);
        }

        UIElement first = InternalChildren[0];
        UIElement second = InternalChildren[1];
        SplitPlan plan = Adaptive.Split(availableSize.Width, Spec);
        if (!plan.Stacked)
        {
            first.Measure(new Size(plan.FirstWidth, availableSize.Height));
            second.Measure(new Size(plan.SecondWidth, availableSize.Height));
            double height = Math.Max(first.DesiredSize.Height, second.DesiredSize.Height);
            double width = double.IsInfinity(availableSize.Width) ? plan.FirstWidth + Spacing + second.DesiredSize.Width : availableSize.Width;
            return new Size(width, double.IsInfinity(availableSize.Height) ? height : Math.Min(height, availableSize.Height));
        }

        first.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        second.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        _firstDesired = first.DesiredSize.Height;
        _secondDesired = second.DesiredSize.Height;
        (double top, double bottom) = Adaptive.Stack(availableSize.Height, Spec, _firstDesired, _secondDesired);
        first.Measure(new Size(availableSize.Width, top));
        second.Measure(new Size(availableSize.Width, bottom));
        double stackedWidth = double.IsInfinity(availableSize.Width) ? Math.Max(first.DesiredSize.Width, second.DesiredSize.Width) : availableSize.Width;
        return new Size(stackedWidth, top + Spacing + bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count < 2)
        {
            foreach (UIElement child in InternalChildren)
            {
                child.Arrange(new Rect(finalSize));
            }

            return finalSize;
        }

        UIElement first = InternalChildren[0];
        UIElement second = InternalChildren[1];
        SplitPlan plan = Adaptive.Split(finalSize.Width, Spec);
        if (!plan.Stacked)
        {
            first.Arrange(new Rect(0, 0, plan.FirstWidth, finalSize.Height));
            second.Arrange(new Rect(plan.FirstWidth + Spacing, 0, plan.SecondWidth, finalSize.Height));
            return finalSize;
        }

        (double top, double bottom) = Adaptive.Stack(finalSize.Height, Spec, _firstDesired, _secondDesired);
        first.Arrange(new Rect(0, 0, finalSize.Width, top));
        second.Arrange(new Rect(0, top + Spacing, finalSize.Width, bottom));
        return finalSize;
    }

    private Size MeasureAll(Size available)
    {
        var size = default(Size);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(available);
            size = new Size(Math.Max(size.Width, child.DesiredSize.Width), Math.Max(size.Height, child.DesiredSize.Height));
        }

        return size;
    }

    private static DependencyProperty Register(string name, double value) =>
        DependencyProperty.Register(name, typeof(double), typeof(AdaptiveSplit), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsMeasure));
}

/// <summary>
/// Children in one row, the one marked <see cref="FillProperty"/> (else the first) taking the width that is left; when
/// that would leave it less than <see cref="FillMinWidth"/>, the children stack, each the full width
/// (docs/plans/14-adaptive-pages.md). Typically text on the left and buttons on the right, the buttons moving under
/// the text in a narrow pane. Collapsed children take no room.
/// </summary>
public sealed class AdaptiveRow : Panel
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
        "Fill", typeof(bool), typeof(AdaptiveRow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty SpacingProperty = Register(nameof(Spacing), 12d);
    public static readonly DependencyProperty RowSpacingProperty = Register(nameof(RowSpacing), 8d);
    public static readonly DependencyProperty FillMinWidthProperty = Register(nameof(FillMinWidth), 200d);

    private RowPlan _plan = new(false, []);

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Gets or sets the gap between the stacked children.</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>Gets or sets the least width the filling child keeps in one row.</summary>
    public double FillMinWidth
    {
        get => (double)GetValue(FillMinWidthProperty);
        set => SetValue(FillMinWidthProperty, value);
    }

    public static bool GetFill(UIElement element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(FillProperty);

    public static void SetFill(UIElement element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(FillProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        UIElement[] shown = Shown();
        if (shown.Length == 0)
        {
            _plan = new RowPlan(false, []);
            return default;
        }

        int fill = Math.Max(0, Array.FindIndex(shown, GetFill));
        foreach (UIElement child in shown)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        }

        _plan = Adaptive.Row(availableSize.Width, [.. shown.Select(c => c.DesiredSize.Width)], fill, FillMinWidth, Spacing);
        if (!_plan.Stacked)
        {
            shown[fill].Measure(new Size(_plan.Widths[fill], availableSize.Height));
            double width = double.IsInfinity(availableSize.Width) ? _plan.Widths.Sum() + (Spacing * (shown.Length - 1)) : availableSize.Width;
            return new Size(width, shown.Max(c => c.DesiredSize.Height));
        }

        double height = 0;
        foreach (UIElement child in shown)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            height += child.DesiredSize.Height;
        }

        return new Size(availableSize.Width, height + (RowSpacing * (shown.Length - 1)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UIElement[] shown = Shown();
        foreach (UIElement hidden in InternalChildren.Cast<UIElement>().Except(shown))
        {
            hidden.Arrange(default);
        }

        if (shown.Length == 0 || _plan.Widths.Count != shown.Length)
        {
            return finalSize;
        }

        if (!_plan.Stacked)
        {
            // Widths from the measure pass, the filling child adjusted to the final width.
            int fill = Math.Max(0, Array.FindIndex(shown, GetFill));
            double others = _plan.Widths.Where((_, i) => i != fill).Sum() + (Spacing * (shown.Length - 1));
            double x = 0;
            for (int i = 0; i < shown.Length; i++)
            {
                double width = i == fill ? Math.Max(0, finalSize.Width - others) : _plan.Widths[i];
                shown[i].Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width + Spacing;
            }

            return finalSize;
        }

        double y = 0;
        foreach (UIElement child in shown)
        {
            child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height + RowSpacing;
        }

        return finalSize;
    }

    private UIElement[] Shown() => [.. InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed)];

    private static DependencyProperty Register(string name, double value) =>
        DependencyProperty.Register(name, typeof(double), typeof(AdaptiveRow), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsMeasure));
}

/// <summary>
/// Equal columns, as many as fit at <see cref="MinItemWidth"/> (at most one per child): e.g. four tiles in a wide
/// pane, two in half of it, one in a narrow one (docs/plans/14-adaptive-pages.md). Each row is as high as its tallest.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AdaptiveGrid), new FrameworkPropertyMetadata(210d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveGrid), new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        UIElement[] items = Items();
        if (items.Length == 0)
        {
            return default;
        }

        double width = double.IsInfinity(availableSize.Width) ? (MinItemWidth * items.Length) + (Spacing * (items.Length - 1)) : availableSize.Width;
        (int columns, double itemWidth) = Layout(width, items.Length);
        double height = 0;
        for (int row = 0; row * columns < items.Length; row++)
        {
            double rowHeight = 0;
            foreach (UIElement item in items.Skip(row * columns).Take(columns))
            {
                item.Measure(new Size(itemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, item.DesiredSize.Height);
            }

            height += rowHeight + (row > 0 ? Spacing : 0);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UIElement[] items = Items();
        (int columns, double itemWidth) = Layout(finalSize.Width, Math.Max(1, items.Length));
        double y = 0;
        for (int row = 0; row * columns < items.Length; row++)
        {
            UIElement[] cells = [.. items.Skip(row * columns).Take(columns)];
            double rowHeight = cells.Max(c => c.DesiredSize.Height);
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i].Arrange(new Rect(i * (itemWidth + Spacing), y, itemWidth, rowHeight));
            }

            y += rowHeight + Spacing;
        }

        return finalSize;
    }

    private (int Columns, double ItemWidth) Layout(double width, int count)
    {
        int columns = Adaptive.Columns(width, MinItemWidth, count, Spacing);
        return (columns, Math.Max(0, (width - (Spacing * (columns - 1))) / columns));
    }

    private UIElement[] Items() => [.. InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed)];
}
