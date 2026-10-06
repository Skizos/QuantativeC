using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Data.History;

/// <summary>One share's intraday bars over some trading days (plan 20).</summary>
/// <param name="Fine">Days with 1- or 5-minute bars.</param>
/// <param name="Coarse">Days with only the 10-minute catch-up bars.</param>
/// <param name="Missing">Days without intraday bars.</param>
public sealed record IntradayShareCoverage(string Ticker, int Fine, int Coarse, IReadOnlyList<DateOnly> Missing);

/// <summary>
/// The intraday collection over some trading days (the weekly summary, plan 20), and the days collected so far (plan 17's
/// go/no-go counts them).
/// </summary>
public sealed record IntradayCoverage(IReadOnlyList<DateOnly> TradingDays, IReadOnlyList<IntradayShareCoverage> Shares, int CollectedDays)
{
    /// <summary>Measures <paramref name="shares"/> over <paramref name="tradingDays"/> in <paramref name="store"/>.</summary>
    public static IntradayCoverage Measure(HistoryStore store, IEnumerable<(OrderbookId Id, string Ticker)> shares, IReadOnlyList<DateOnly> tradingDays, string source)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentNullException.ThrowIfNull(tradingDays);
        var result = new List<IntradayShareCoverage>();
        foreach ((OrderbookId id, string ticker) in shares)
        {
            HashSet<DateOnly> fine = [.. store.GetIntradayDays(id, ChartResolution.Minute, source), .. store.GetIntradayDays(id, ChartResolution.FiveMinutes, source)];
            HashSet<DateOnly> coarse = [.. store.GetIntradayDays(id, IntradayImporter.FallbackResolution, source)];
            result.Add(new IntradayShareCoverage(
                ticker,
                tradingDays.Count(fine.Contains),
                tradingDays.Count(d => !fine.Contains(d) && coarse.Contains(d)),
                [.. tradingDays.Where(d => !fine.Contains(d) && !coarse.Contains(d))]));
        }

        return new IntradayCoverage(tradingDays, result, store.GetIntradayCollectedDays(source).Count);
    }
}
