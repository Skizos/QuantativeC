using QuantAnalyst.Core;

namespace QuantAnalyst.Desktop.Core.Charts;

/// <summary>
/// The stored daily candles of your instruments and your past trades, shared by every charts page and window
/// (docs/plans/12-charts-window.md). They are read while no command runs (a Paper session uses the price store and
/// writes the audit log) and kept, so the history charts still work while a session trades.
/// </summary>
public sealed class HistoryCandles
{
    private IReadOnlyDictionary<OrderbookId, IReadOnlyList<Candle>> _candles = new Dictionary<OrderbookId, IReadOnlyList<Candle>>();
    private IReadOnlyDictionary<string, IReadOnlyList<ChartMarker>> _trades = new Dictionary<string, IReadOnlyList<ChartMarker>>(StringComparer.Ordinal);

    /// <summary>Gets when the candles were last read, or null before the first read.</summary>
    public DateTimeOffset? LoadedAt { get; private set; }

    public IReadOnlyList<Candle> CandlesOf(OrderbookId id) => _candles.TryGetValue(id, out IReadOnlyList<Candle>? c) ? c : [];

    public IReadOnlyList<ChartMarker> TradesOf(string ticker) => _trades.TryGetValue(ticker, out IReadOnlyList<ChartMarker>? t) ? t : [];

    /// <summary>Replaces what is kept with a new read (all at once, so a reader sees either the old or the new).</summary>
    public void Put(
        IReadOnlyDictionary<OrderbookId, IReadOnlyList<Candle>> candles, IReadOnlyDictionary<string, IReadOnlyList<ChartMarker>> trades, DateTimeOffset at)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _trades = trades ?? throw new ArgumentNullException(nameof(trades));
        LoadedAt = at;
    }
}
