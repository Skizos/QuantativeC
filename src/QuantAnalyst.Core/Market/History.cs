namespace QuantAnalyst.Core.Market;

/// <summary>A chart response: the bars and the resolution the broker actually used (it may differ from the one asked for).</summary>
public sealed record PriceHistory(IReadOnlyList<Bar> Bars, ChartResolution Resolution, decimal? PreviousClose);

/// <summary>One daily bar keyed by its exchange trading date (Europe/Stockholm).</summary>
public readonly record struct DailyBar(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

/// <summary>
/// A historical data source and the honesty labels every result from it must carry (CLAUDE.md backtesting rules).
/// </summary>
/// <param name="Name">Stable key stored with every row, e.g. <c>avanza-price-chart</c>.</param>
/// <param name="PointInTime">True only if the source serves history as it was known at the time (no later adjustments).</param>
/// <param name="SurvivorshipFree">True only if delisted instruments are included.</param>
/// <param name="Notes">Human-readable caveats printed with results.</param>
public sealed record DataSourceInfo(string Name, bool PointInTime, bool SurvivorshipFree, string Notes)
{
    /// <summary>One-line label for reports, e.g. "avanza-price-chart — NOT survivorship-free, NOT point-in-time".</summary>
    public string Label =>
        $"{Name} — {(SurvivorshipFree ? "survivorship-free" : "NOT survivorship-free")}, {(PointInTime ? "point-in-time" : "NOT point-in-time")}";
}
