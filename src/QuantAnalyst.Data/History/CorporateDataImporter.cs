using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Data.History;

/// <summary>One share's dividends and share count, fetched today, with the count stored before today (the split check).</summary>
/// <param name="PreviousShares">The latest share count stored before today; null the first time.</param>
public sealed record CorporateSnapshot(CorporateData Data, decimal? PreviousShares);

/// <summary>
/// Plan 21: fetches a share's dividends and share count from the broker's stock details (public, read-only) and stores
/// them, known-at versioned. Informational data (ADR 0002 Tier B): a failure disables the dividends and the split
/// check for the day, it never stops a session.
/// </summary>
public static class CorporateDataImporter
{
    public static DataSourceInfo AvanzaStockDetails { get; } = new(
        "avanza-stock-details",
        PointInTime: false,
        SurvivorshipFree: false,
        "Avanza's stock details: dividends (past amounts per current share, adjusted for later splits) and the company's share count, as shown on the fetch date.");

    public static async Task<CorporateSnapshot> ImportAsync(
        HistoryStore store, IBrokerGateway gateway, OrderbookId id, string sourceVersion, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(time);
        CorporateData data = await gateway.GetCorporateDataAsync(id, ct).ConfigureAwait(false);
        DateTimeOffset now = time.GetUtcNow();
        DateOnly today = DateOnly.FromDateTime(MarketTime.ToStockholm(now).DateTime);
        store.RegisterSource(AvanzaStockDetails);
        decimal? before = store.LatestShareCount(id, AvanzaStockDetails.Name, today.AddDays(-1))?.Shares;
        store.UpsertDividends(id, [.. data.Dividends.DistinctBy(d => (d.ExDate, d.Type))], AvanzaStockDetails, sourceVersion, now);
        if (data.SharesOutstanding is { } shares)
        {
            store.UpsertShareCount(id, today, shares, AvanzaStockDetails, sourceVersion, now);
        }

        return new CorporateSnapshot(data, before);
    }
}
