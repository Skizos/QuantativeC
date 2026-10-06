namespace QuantAnalyst.Desktop.Core.Presentation;

/// <summary>
/// How a two-part page (a list and its details, a form and its result) shares its width: side by side from
/// <see cref="Breakpoint"/> on, else stacked, the first part above (docs/plans/14-adaptive-pages.md).
/// </summary>
/// <param name="Breakpoint">The narrowest width that still shows the parts side by side.</param>
/// <param name="FirstWidth">The first part's width side by side, in pixels; 0 to use <paramref name="FirstShare"/>.</param>
/// <param name="FirstShare">The first part's share of the width side by side (when <paramref name="FirstWidth"/> is 0).</param>
/// <param name="Spacing">The gap between the parts, across or down.</param>
/// <param name="StackedFirstMaxHeight">Stacked, the first part is at most this high (e.g. a list that then scrolls).</param>
/// <param name="StackedSecondMinHeight">Stacked in a bounded height, the second part keeps at least this much.</param>
public sealed record SplitSpec(
    double Breakpoint = 720,
    double FirstWidth = 0,
    double FirstShare = 0.5,
    double Spacing = 16,
    double StackedFirstMaxHeight = double.PositiveInfinity,
    double StackedSecondMinHeight = 200)
{
    /// <summary>Side by side, neither part gets narrower than this.</summary>
    public const double MinPart = 200;
}

/// <summary>A two-part layout's widths: stacked (both the full width) or side by side.</summary>
public readonly record struct SplitPlan(bool Stacked, double FirstWidth, double SecondWidth);

/// <summary>A row's widths: in one row (the filling child takes what is left), or stacked (each the full width).</summary>
public sealed record RowPlan(bool Stacked, IReadOnlyList<double> Widths);

/// <summary>
/// The sizing rules of the app's adaptive panels (docs/plans/14-adaptive-pages.md), so every page works as wide as the
/// window and as narrow as half of it beside another page. Pure maths, tested on Linux; the WPF panels only measure
/// their children and place them where this says.
/// </summary>
public static class Adaptive
{
    /// <summary>The widths of a two-part layout in <paramref name="width"/>.</summary>
    public static SplitPlan Split(double width, SplitSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (double.IsNaN(width) || double.IsInfinity(width))
        {
            double first = spec.FirstWidth > 0 ? spec.FirstWidth : 400;
            return new SplitPlan(false, first, double.PositiveInfinity);
        }

        if (width < spec.Breakpoint)
        {
            return new SplitPlan(true, width, width);
        }

        double wanted = spec.FirstWidth > 0 ? spec.FirstWidth : width * spec.FirstShare;
        double firstWidth = Math.Clamp(wanted, SplitSpec.MinPart, Math.Max(SplitSpec.MinPart, width - spec.Spacing - SplitSpec.MinPart));
        return new SplitPlan(false, firstWidth, Math.Max(0, width - spec.Spacing - firstWidth));
    }

    /// <summary>
    /// The heights of a stacked two-part layout. Unbounded (in a scrolling page) each part gets what it asks for;
    /// bounded, the first part gets what it asks for up to its cap, but never so much that the second part gets less
    /// than its minimum, and the second part gets the rest.
    /// </summary>
    public static (double First, double Second) Stack(double height, SplitSpec spec, double firstDesired, double secondDesired)
    {
        ArgumentNullException.ThrowIfNull(spec);
        double first = Math.Min(Math.Max(0, firstDesired), spec.StackedFirstMaxHeight);
        if (double.IsNaN(height) || double.IsInfinity(height))
        {
            return (first, Math.Max(0, secondDesired));
        }

        first = Math.Min(first, Math.Max(0, height - spec.Spacing - spec.StackedSecondMinHeight));
        return (first, Math.Max(0, height - spec.Spacing - first));
    }

    /// <summary>
    /// A row of children with their desired <paramref name="widths"/>; the one at <paramref name="fill"/> takes the rest
    /// and needs at least <paramref name="fillMin"/>. When they don't fit, they stack.
    /// </summary>
    public static RowPlan Row(double available, IReadOnlyList<double> widths, int fill, double fillMin, double spacing)
    {
        ArgumentNullException.ThrowIfNull(widths);
        if (widths.Count == 0)
        {
            return new RowPlan(false, []);
        }

        fill = Math.Clamp(fill, 0, widths.Count - 1);
        if (double.IsNaN(available) || double.IsInfinity(available))
        {
            return new RowPlan(false, widths);
        }

        double others = widths.Where((_, i) => i != fill).Sum();
        double gaps = spacing * (widths.Count - 1);
        if (others + gaps + fillMin > available)
        {
            return new RowPlan(true, [.. widths.Select(_ => available)]);
        }

        double[] result = [.. widths];
        result[fill] = available - others - gaps;
        return new RowPlan(false, result);
    }

    /// <summary>How many equal columns of at least <paramref name="minItemWidth"/> fit, for <paramref name="count"/> items (1 at least).</summary>
    public static int Columns(double width, double minItemWidth, int count, double spacing)
    {
        if (count <= 0)
        {
            return 1;
        }

        if (double.IsNaN(width) || double.IsInfinity(width))
        {
            return count;
        }

        int fit = (int)Math.Floor((width + spacing) / (Math.Max(1, minItemWidth) + spacing));
        return Math.Clamp(fit, 1, count);
    }
}
