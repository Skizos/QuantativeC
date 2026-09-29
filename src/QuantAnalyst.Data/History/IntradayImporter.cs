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

/// <summary>What one catch-up found and stored (plan 17 A2b).</summary>
/// <param name="Missed">The trading days of the last week that had neither 5- nor 10-minute bars.</param>
/// <param name="Filled">The missed days the 10-minute answer covered, now stored.</param>
/// <param name="Bars">What was written; null when nothing was missed, so nothing was asked.</param>
public sealed record IntradayCatchUpReport(OrderbookId OrderbookId, IReadOnlyList<DateOnly> Missed, IReadOnlyList<DateOnly> Filled, WriteCounts? Bars)
{
    /// <summary>Gets the missed days the answer did not cover: lost for intraday research.</summary>
    public IReadOnlyList<DateOnly> Lost => [.. Missed.Except(Filled)];
}

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

    /// <summary>
    /// The owner's probe (2026-09-29): 1- and 5-minute bars exist for today only, and <c>one_week</c> gives 10-minute bars.
    /// So a missed day is caught up at this resolution, from this far back.
    /// </summary>
    public const ChartResolution FallbackResolution = ChartResolution.TenMinutes;

    /// <summary>Calendar days back that <c>one_week</c> reaches (the probe: 7 days, today included).</summary>
    public const int FallbackDays = 7;

    /// <summary>A bar's length.</summary>
    public static TimeSpan Length(ChartResolution resolution) => resolution switch
    {
        ChartResolution.Minute => TimeSpan.FromMinutes(1),
        ChartResolution.FiveMinutes => TimeSpan.FromMinutes(5),
        ChartResolution.TenMinutes => TimeSpan.FromMinutes(10),
        _ => throw new ArgumentException($"Intraday bars are 1, 5 or 10 minutes, not {resolution}.", nameof(resolution)),
    };

    /// <summary>
    /// Plan 17 A2b, the catch-up: the XSTO trading days of the last <see cref="FallbackDays"/> days, before today, on which
    /// this share has neither 5- nor 10-minute bars are fetched once from <c>one_week</c> at 10 minutes, with the same
    /// strictness as <see cref="ImportAsync"/>, and only those days' bars are stored. Nothing missed: nothing is asked.
    /// Days the calendar has not loaded are skipped, never guessed.
    /// </summary>
    public static async Task<IntradayCatchUpReport> CatchUpAsync(
        HistoryStore store, IBrokerGateway gateway, OrderbookId id, MarketCalendar calendar, string sourceVersion, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(time);
        DateTimeOffset now = time.GetUtcNow();
        DateOnly today = DateOnly.FromDateTime(MarketTime.ToStockholm(now).DateTime);
        DateOnly first = today.AddDays(-FallbackDays);
        var window = new List<DateOnly>();
        for (DateOnly d = first; d < today; d = d.AddDays(1))
        {
            if (calendar.Years.Contains(d.Year) && calendar.Classify(d).IsTradingDay)
            {
                window.Add(d);
            }
        }

        string source = AvanzaChartImporter.AvanzaPriceChart.Name;
        DateTimeOffset since = calendar.ToUtc(first, TimeOnly.MinValue);
        HashSet<DateOnly> have = [.. new[] { ChartResolution.FiveMinutes, FallbackResolution }
            .SelectMany(r => store.GetIntradayBars(id, r, source, since))
            .Select(b => DateOnly.FromDateTime(MarketTime.ToStockholm(b.Bar.TimestampUtc).DateTime))];
        DateOnly[] missed = [.. window.Where(d => !have.Contains(d))];
        if (missed.Length == 0)
        {
            return new IntradayCatchUpReport(id, [], [], null);
        }

        (List<Bar> complete, _) = await FetchAsync(gateway, id, ChartPeriod.OneWeek, FallbackResolution, now, ct).ConfigureAwait(false);
        Bar[] kept = [.. complete.Where(b => missed.Contains(DateOnly.FromDateTime(MarketTime.ToStockholm(b.TimestampUtc).DateTime)))];
        store.RegisterSource(AvanzaChartImporter.AvanzaPriceChart);
        WriteCounts written = store.UpsertIntradayBars(id, FallbackResolution, kept, AvanzaChartImporter.AvanzaPriceChart, sourceVersion, now);
        DateOnly[] filled = [.. kept.Select(b => DateOnly.FromDateTime(MarketTime.ToStockholm(b.TimestampUtc).DateTime)).Distinct()];
        return new IntradayCatchUpReport(id, missed, filled, written);
    }

    public static async Task<IntradayImportReport> ImportAsync(
        HistoryStore store, IBrokerGateway gateway, OrderbookId id, ChartPeriod period, ChartResolution resolution, string sourceVersion, TimeProvider time,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(time);
        _ = Length(resolution); // 1, 5 or 10 minutes only
        if (period is not (ChartPeriod.Today or ChartPeriod.OneWeek or ChartPeriod.OneMonth or ChartPeriod.ThreeMonths))
        {
            throw new ArgumentException($"Intraday bars come for today, one week, one month or three months at most, not {period}.", nameof(period));
        }

        DateTimeOffset now = time.GetUtcNow();
        (List<Bar> complete, int inProgress) = await FetchAsync(gateway, id, period, resolution, now, ct).ConfigureAwait(false);
        store.RegisterSource(AvanzaChartImporter.AvanzaPriceChart);
        WriteCounts written = store.UpsertIntradayBars(id, resolution, complete, AvanzaChartImporter.AvanzaPriceChart, sourceVersion, now);
        int days = complete.Select(b => DateOnly.FromDateTime(MarketTime.ToStockholm(b.TimestampUtc).DateTime)).Distinct().Count();
        return new IntradayImportReport(id, resolution, written, complete.Count > 0 ? complete[0].TimestampUtc : null, complete.Count > 0 ? complete[^1].TimestampUtc : null,
            days, inProgress);
    }

    /// <summary>One chart call, checked: the resolution asked, on its grid, rising; bars still open at <paramref name="now"/> left out.</summary>
    private static async Task<(List<Bar> Complete, int InProgress)> FetchAsync(
        IBrokerGateway gateway, OrderbookId id, ChartPeriod period, ChartResolution resolution, DateTimeOffset now, CancellationToken ct)
    {
        TimeSpan length = Length(resolution);
        PriceHistory history = await gateway.GetPriceHistoryAsync(id, period, resolution, ct).ConfigureAwait(false);
        if (history.Resolution != resolution)
        {
            throw new HistoryImportException(
                $"Avanza answered with {history.Resolution} bars for {period}, not {resolution}; nothing was stored. 'qa intraday probe' shows what it gives.");
        }

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

        return (complete, inProgress);
    }
}
