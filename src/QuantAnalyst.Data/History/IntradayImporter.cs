using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Data.History;

/// <summary>What one intraday import stored.</summary>
/// <param name="InProgress">Bars left out because they had not closed yet when asked.</param>
public sealed record IntradayImportReport(
    OrderbookId OrderbookId, ChartResolution Resolution, WriteCounts Bars, DateTimeOffset? FirstUtc, DateTimeOffset? LastUtc, int Days, int InProgress);

/// <summary>
/// Intraday bars from Avanza's public price chart into the store (plan 17 step A2, ADR 0006):
/// <list type="bullet">
/// <item>1- and 5-minute bars only (the resolutions plan 17 uses)</item>
/// <item>the answer must be the resolution asked for; any other is refused and nothing is stored</item>
/// <item>each bar must start on its resolution's grid (whole minutes; for 5 minutes, :00, :05, …) and the starts must
/// rise; anything else is refused rather than guessed (Tier B strictness)</item>
/// <item>a bar that has not closed yet is left out, so a mid-day import never stores a half bar</item>
/// </list>
/// The source is the same as the daily bars: <see cref="AvanzaChartImporter.AvanzaPriceChart"/>, NOT point-in-time and
/// NOT survivorship-free.
/// </summary>
public static class IntradayImporter
{
    public static IReadOnlyList<ChartResolution> Resolutions { get; } = [ChartResolution.Minute, ChartResolution.FiveMinutes];

    /// <summary>A bar's length.</summary>
    public static TimeSpan Length(ChartResolution resolution) => resolution switch
    {
        ChartResolution.Minute => TimeSpan.FromMinutes(1),
        ChartResolution.FiveMinutes => TimeSpan.FromMinutes(5),
        _ => throw new ArgumentException($"Intraday bars are 1 or 5 minutes, not {resolution}.", nameof(resolution)),
    };

    public static async Task<IntradayImportReport> ImportAsync(
        HistoryStore store, IBrokerGateway gateway, OrderbookId id, ChartPeriod period, ChartResolution resolution, string sourceVersion, TimeProvider time,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(time);
        TimeSpan length = Length(resolution);
        if (period is not (ChartPeriod.Today or ChartPeriod.OneWeek or ChartPeriod.OneMonth or ChartPeriod.ThreeMonths))
        {
            throw new ArgumentException($"Intraday bars come for today, one week, one month or three months at most, not {period}.", nameof(period));
        }

        PriceHistory history = await gateway.GetPriceHistoryAsync(id, period, resolution, ct).ConfigureAwait(false);
        if (history.Resolution != resolution)
        {
            throw new HistoryImportException(
                $"Avanza answered with {history.Resolution} bars for {period}, not {resolution}; nothing was stored. 'qa intraday probe' shows what it gives.");
        }

        DateTimeOffset now = time.GetUtcNow();
        var complete = new List<Bar>(history.Bars.Count);
        int inProgress = 0;
        DateTimeOffset? previous = null;
        foreach (Bar b in history.Bars)
        {
            DateTimeOffset start = b.TimestampUtc;
            if (start.Ticks % length.Ticks != 0)
            {
                throw new HistoryImportException(string.Create(CultureInfo.InvariantCulture,
                    $"A {resolution} bar starts at {MarketTime.ToStockholm(start):yyyy-MM-dd HH:mm:ss} Stockholm time, off the {length.TotalMinutes:0}-minute grid; nothing was stored."));
            }

            if (previous is { } p && start <= p)
            {
                throw new HistoryImportException(string.Create(CultureInfo.InvariantCulture,
                    $"Avanza returned {resolution} bars out of order or twice at {MarketTime.ToStockholm(start):yyyy-MM-dd HH:mm}; nothing was stored."));
            }

            previous = start;
            if (start + length > now)
            {
                inProgress++;
                continue;
            }

            complete.Add(b);
        }

        store.RegisterSource(AvanzaChartImporter.AvanzaPriceChart);
        WriteCounts written = store.UpsertIntradayBars(id, resolution, complete, AvanzaChartImporter.AvanzaPriceChart, sourceVersion, now);
        int days = complete.Select(b => DateOnly.FromDateTime(MarketTime.ToStockholm(b.TimestampUtc).DateTime)).Distinct().Count();
        return new IntradayImportReport(id, resolution, written, complete.Count > 0 ? complete[0].TimestampUtc : null, complete.Count > 0 ? complete[^1].TimestampUtc : null,
            days, inProgress);
    }
}
