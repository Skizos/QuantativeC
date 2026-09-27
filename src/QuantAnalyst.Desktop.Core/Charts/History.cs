using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Desktop.Core.Charts;

/// <summary>A chart range as the buttons above a history chart offer it: 1M, 3M, 6M, 1Y, 3Y or All.</summary>
public sealed record ChartRange(string Label, int? Months)
{
    public static IReadOnlyList<ChartRange> All { get; } =
    [
        new("1M", 1), new("3M", 3), new("6M", 6), new("1Y", 12), new("3Y", 36), new("All", null),
    ];

    public static ChartRange OneYear => All[3];

    /// <summary>The points from <see cref="Months"/> before the last point's (Stockholm) date on; all of them for All.</summary>
    public IReadOnlyList<ChartPoint> Take(IReadOnlyList<ChartPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (Months is not { } months || points.Count == 0)
        {
            return points;
        }

        DateOnly last = DateOnly.FromDateTime(MarketTime.ToStockholm(points[^1].At).DateTime);
        DateOnly from = last.AddMonths(-months);
        return [.. points.Where(p => DateOnly.FromDateTime(MarketTime.ToStockholm(p.At).DateTime) >= from)];
    }

    public override string ToString() => Label;
}

/// <summary>Lines drawn over a price history.</summary>
public static class Indicators
{
    /// <summary>
    /// The simple moving average over <paramref name="length"/> points, one value per point from the
    /// <paramref name="length"/>-th on (like the strategy's own averages; nothing before there is a full window).
    /// </summary>
    public static IReadOnlyList<ChartPoint> Sma(IReadOnlyList<ChartPoint> points, int length)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        var result = new List<ChartPoint>(Math.Max(0, points.Count - length + 1));
        double sum = 0;
        for (int i = 0; i < points.Count; i++)
        {
            sum += points[i].Value;
            if (i >= length)
            {
                sum -= points[i - length].Value;
            }

            if (i >= length - 1)
            {
                result.Add(new ChartPoint(points[i].At, sum / length));
            }
        }

        return result;
    }
}
