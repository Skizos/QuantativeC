using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Data.History;

/// <summary>A history source refused or could not serve the request (e.g. no daily bars for the period).</summary>
public sealed class HistoryImportException(string message) : Exception(message);

/// <summary>
/// A source of daily history: Avanza's chart today, a vendor later (the "vendor slot", master plan §1). Every result
/// carries the source's honesty labels through <see cref="Source"/>.
/// </summary>
public interface IHistoricalDataProvider
{
    DataSourceInfo Source { get; }

    /// <summary>Version of the source's payload and routes, stored with every row (e.g. a DTO version).</summary>
    string SourceVersion { get; }

    /// <summary>Daily bars with <c>fromDate ≤ date ≤ toDate</c>, ordered by date. May start later than <paramref name="fromDate"/> if the source has less history.</summary>
    Task<IReadOnlyList<DailyBar>> GetDailyBarsAsync(OrderbookId id, DateOnly fromDate, DateOnly toDate, CancellationToken ct);
}

/// <summary>
/// Daily bars from Avanza's price chart through <see cref="IBrokerGateway"/>:
/// <list type="bullet">
/// <item>asks for the smallest chart period that covers <c>from</c> (up to five years) with <c>resolution=day</c>, and
/// refuses the answer if Avanza used another resolution</item>
/// <item>maps each bar to its Stockholm trading date; Avanza stamps daily bars at Stockholm midnight (recorded
/// 2026-09-25: 1787608800000 = 2026-08-25 00:00 CEST), and any other time is refused rather than guessed</item>
/// </list>
/// Avanza's history is adjusted as of today and lists current instruments only, so the source is labelled
/// NOT point-in-time and NOT survivorship-free.
/// </summary>
public sealed class AvanzaChartImporter(IBrokerGateway gateway, TimeProvider time, string sourceVersion) : IHistoricalDataProvider
{
    public static DataSourceInfo AvanzaPriceChart { get; } = new(
        "avanza-price-chart",
        PointInTime: false,
        SurvivorshipFree: false,
        "Avanza web price chart: history as Avanza shows it on the import date (later corporate-action adjustments included); current listings only.");

    public DataSourceInfo Source => AvanzaPriceChart;

    public string SourceVersion { get; } = sourceVersion;

    public async Task<IReadOnlyList<DailyBar>> GetDailyBarsAsync(OrderbookId id, DateOnly fromDate, DateOnly toDate, CancellationToken ct)
    {
        if (toDate < fromDate)
        {
            throw new ArgumentException("toDate is before fromDate.", nameof(toDate));
        }

        DateOnly today = DateOnly.FromDateTime(MarketTime.ToStockholm(time.GetUtcNow()).DateTime);
        ChartPeriod period = PeriodCovering(fromDate, today);
        PriceHistory history = await gateway.GetPriceHistoryAsync(id, period, ChartResolution.Day, ct).ConfigureAwait(false);
        if (history.Resolution != ChartResolution.Day)
        {
            throw new HistoryImportException(
                $"Avanza answered with {history.Resolution} bars for the period {period}; daily bars are not available for it. Import a shorter range.");
        }

        var bars = new List<DailyBar>(history.Bars.Count);
        foreach (Bar b in history.Bars)
        {
            DateOnly date = TradingDate(b.TimestampUtc);
            if (date >= fromDate && date <= toDate)
            {
                bars.Add(new DailyBar(date, b.Open, b.High, b.Low, b.Close, b.Volume));
            }
        }

        for (int i = 1; i < bars.Count; i++)
        {
            if (bars[i].Date <= bars[i - 1].Date)
            {
                throw new HistoryImportException($"Avanza returned daily bars out of order or twice for {bars[i].Date:yyyy-MM-dd}.");
            }
        }

        return bars;
    }

    /// <summary>The smallest Avanza chart period whose look-back reaches <paramref name="from"/>; five years at most.</summary>
    public static ChartPeriod PeriodCovering(DateOnly from, DateOnly today) =>
        from >= today.AddDays(-7) ? ChartPeriod.OneWeek
        : from >= today.AddMonths(-1) ? ChartPeriod.OneMonth
        : from >= today.AddMonths(-3) ? ChartPeriod.ThreeMonths
        : from >= today.AddYears(-1) ? ChartPeriod.OneYear
        : from >= today.AddYears(-3) ? ChartPeriod.ThreeYears
        : ChartPeriod.FiveYears;

    /// <summary>The Stockholm date of a daily bar stamped at Stockholm midnight; anything else is refused.</summary>
    public static DateOnly TradingDate(DateTimeOffset timestampUtc)
    {
        DateTimeOffset local = MarketTime.ToStockholm(timestampUtc);
        if (local.TimeOfDay != TimeSpan.Zero)
        {
            throw new HistoryImportException(
                $"A daily bar is stamped {local:yyyy-MM-dd HH:mm} Stockholm time, not at midnight; refusing to guess its trading date.");
        }

        return DateOnly.FromDateTime(local.DateTime);
    }
}
