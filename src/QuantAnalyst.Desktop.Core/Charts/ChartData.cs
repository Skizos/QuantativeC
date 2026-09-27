namespace QuantAnalyst.Desktop.Core.Charts;

/// <summary>One value at one time: a price, an account value, a moving average.</summary>
public readonly record struct ChartPoint(DateTimeOffset At, double Value);

public enum MarkerKind
{
    Buy,
    Sell,
}

/// <summary>A trade on the chart (a fill), drawn as a dot in green (buy) or red (sell) with its label on hover.</summary>
public sealed record ChartMarker(DateTimeOffset At, double Value, MarkerKind Kind, string Label);

/// <summary>A horizontal line across the chart, e.g. a working order's limit, with a label at the right edge.</summary>
public sealed record ChartLevel(double Value, string Label);

/// <summary>An extra line over the main series, e.g. a moving average, with its legend name.</summary>
public sealed record ChartOverlay(string Name, IReadOnlyList<ChartPoint> Points);

/// <summary>How the x axis runs.</summary>
public enum TimeAxis
{
    /// <summary>Time of day, spaced by clock time (one trading day, e.g. 09:00–17:30).</summary>
    Intraday,

    /// <summary>One slot per point (trading days), so weekends and holidays leave no gaps, as bank apps draw history.</summary>
    Daily,
}

/// <summary>
/// Everything one chart shows (docs/plans/11-app-redesign.md). The main line is coloured by where its last value is
/// against the <see cref="Baseline"/> (green above or equal, red below), like a bank app's day chart.
/// </summary>
public sealed record ChartData
{
    public static ChartData Empty { get; } = new();

    public IReadOnlyList<ChartPoint> Main { get; init; } = [];

    public IReadOnlyList<ChartOverlay> Overlays { get; init; } = [];

    public IReadOnlyList<ChartMarker> Markers { get; init; } = [];

    public IReadOnlyList<ChartLevel> Levels { get; init; } = [];

    /// <summary>Gets the reference the change is measured from (start of day, previous close, first value); null for none.</summary>
    public double? Baseline { get; init; }

    public TimeAxis Axis { get; init; } = TimeAxis.Daily;

    /// <summary>Gets a fixed time window for <see cref="TimeAxis.Intraday"/> (e.g. the trading day), or null to fit the points.</summary>
    public (DateTimeOffset From, DateTimeOffset To)? Window { get; init; }

    /// <summary>Gets how a value reads on the axis and in the hover bubble (e.g. "70,85" or "5 012,40 kr").</summary>
    public Func<double, string>? FormatValue { get; init; }

    public bool IsEmpty => Main.Count == 0;

    /// <summary>Gets whether the last value is at or above the baseline (or the first value when there is none): the line's colour.</summary>
    public bool IsUp => Main.Count == 0 || Main[^1].Value >= (Baseline ?? Main[0].Value);
}
