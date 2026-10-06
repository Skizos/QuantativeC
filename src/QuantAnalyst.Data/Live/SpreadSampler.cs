using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Data.Live;

/// <summary>
/// Keeps one best bid/ask per instrument per minute from a running session's quotes (plan 17 step A2): the first
/// fresh, two-sided quote in each minute. The session writes them to the store at its end, so the intraday backtest's
/// spread cost is measured, not guessed. Thread-safe; quotes arrive on the session's pump threads.
/// </summary>
public sealed class SpreadSampler
{
    private readonly Lock _lock = new();
    private readonly Dictionary<OrderbookId, DateTimeOffset> _lastMinute = [];
    private readonly List<SpreadSample> _samples = [];

    /// <summary>Gets the source the samples are stored under: live observations, so point-in-time.</summary>
    public static DataSourceInfo Source { get; } = new(
        "session-quotes",
        PointInTime: true,
        SurvivorshipFree: false,
        "Best bid/ask a running Paper session saw, once a minute per instrument (Avanza's depth stream, else its poll).");

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _samples.Count;
            }
        }
    }

    /// <summary>Takes the quote if it is the instrument's first fresh, two-sided one this minute. Returns whether it did.</summary>
    public bool Offer(Quote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (quote.IsStale || quote.Bid is not { } bid || quote.Ask is not { } ask || bid <= 0m || ask < bid)
        {
            return false;
        }

        DateTimeOffset at = quote.AsOfUtc ?? quote.ComposedAtUtc;
        var minute = new DateTimeOffset(at.UtcTicks - (at.UtcTicks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
        lock (_lock)
        {
            if (_lastMinute.TryGetValue(quote.OrderbookId, out DateTimeOffset last) && last >= minute)
            {
                return false;
            }

            _lastMinute[quote.OrderbookId] = minute;
            _samples.Add(new SpreadSample(quote.OrderbookId, at, bid, ask, quote.BidVolume, quote.AskVolume));
            return true;
        }
    }

    /// <summary>The samples taken so far, and forgets them.</summary>
    public IReadOnlyList<SpreadSample> Drain()
    {
        lock (_lock)
        {
            SpreadSample[] all = [.. _samples];
            _samples.Clear();
            return all;
        }
    }
}
