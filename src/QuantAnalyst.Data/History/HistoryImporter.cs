using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Data.History;

/// <summary>Outcome of one import: what was written, what range came back, and anything a human should look at.</summary>
public sealed record ImportReport(
    DataSourceInfo Source,
    WriteCounts Bars,
    DateOnly? FirstDate,
    DateOnly? LastDate,
    DateTimeOffset KnownAtUtc,
    IReadOnlyList<string> Warnings);

/// <summary>Pulls daily bars from a provider into the <see cref="HistoryStore"/> with <c>known_at</c> = now.</summary>
public sealed class HistoryImporter(HistoryStore store, TimeProvider time, MarketCalendar? calendar = null)
{
    public async Task<ImportReport> ImportAsync(IHistoricalDataProvider provider, OrderbookId id, DateOnly fromDate, DateOnly toDate, CancellationToken ct)
    {
        IReadOnlyList<DailyBar> bars = await provider.GetDailyBarsAsync(id, fromDate, toDate, ct).ConfigureAwait(false);
        DateTimeOffset knownAt = time.GetUtcNow();
        store.RegisterSource(provider.Source);
        WriteCounts counts = store.UpsertDailyBars(id, bars, provider.Source, provider.SourceVersion, knownAt);

        var warnings = new List<string>();
        if (bars.Count == 0)
        {
            warnings.Add("The source returned no bars in the requested range.");
        }
        else if (bars[0].Date > fromDate.AddDays(7))
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"History starts {bars[0].Date:yyyy-MM-dd}, later than the requested {fromDate:yyyy-MM-dd}."));
        }

        if (counts.Restated > 0)
        {
            warnings.Add($"{counts.Restated} bar(s) differ from what the store knew: stored as restatements (the old values stay visible as of earlier known-at times).");
        }

        if (calendar is not null)
        {
            foreach (DailyBar b in bars.Where(b => calendar.Years.Contains(b.Date.Year)))
            {
                TradingDay day = calendar.Classify(b.Date);
                if (!day.IsTradingDay)
                {
                    warnings.Add(string.Create(
                        CultureInfo.InvariantCulture, $"{b.Date:yyyy-MM-dd} has a bar but the calendar says {day.Kind}{(day.Name is null ? string.Empty : $" ({day.Name})")}."));
                }
            }
        }

        return new ImportReport(provider.Source, counts, bars.Count > 0 ? bars[0].Date : null, bars.Count > 0 ? bars[^1].Date : null, knownAt, warnings);
    }
}
