using System.Data.Common;
using System.Globalization;
using DuckDB.NET.Data;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Data.Store;

/// <summary>
/// Intraday bars and spread samples (plan 17 step A2), added in place like <c>fx_rates</c>: an older store gains the
/// tables when opened, and the schema version stays 1.
/// </summary>
public sealed partial class HistoryStore
{
    // ---- intraday bars ----

    /// <summary>
    /// Appends the intraday bars of one resolution that are new or changed (known at <paramref name="knownAtUtc"/>), in
    /// one transaction. Same rules as the daily bars: append-only, a changed bar is a restatement.
    /// </summary>
    public WriteCounts UpsertIntradayBars(
        OrderbookId id, ChartResolution resolution, IReadOnlyList<Bar> bars, DataSourceInfo source, string sourceVersion, DateTimeOffset knownAtUtc)
    {
        ArgumentNullException.ThrowIfNull(bars);
        RequireRegistered(source);
        if (bars.Count == 0)
        {
            return new WriteCounts(0, 0, 0);
        }

        foreach (Bar b in bars)
        {
            Validate(b);
        }

        if (bars.Select(b => b.TimestampUtc).Distinct().Count() != bars.Count)
        {
            throw new ArgumentException("The same bar start appears twice in one write.", nameof(bars));
        }

        DateTime knownAt = ToStoredTime(knownAtUtc);
        Dictionary<DateTimeOffset, Bar> latest = GetIntradayBars(id, resolution, source.Name, bars.Min(b => b.TimestampUtc), bars.Max(b => b.TimestampUtc))
            .ToDictionary(s => s.Bar.TimestampUtc, s => s.Bar);

        int added = 0, restated = 0, unchanged = 0;
        using DbTransaction tx = _db.BeginTransaction();
        using (DuckDBAppender appender = _db.CreateAppender("intraday_bars"))
        {
            foreach (Bar b in bars.OrderBy(b => b.TimestampUtc))
            {
                if (latest.TryGetValue(b.TimestampUtc, out Bar known))
                {
                    if (known == b)
                    {
                        unchanged++;
                        continue;
                    }

                    restated++;
                }
                else
                {
                    added++;
                }

                appender.CreateRow()
                    .AppendValue(id.Value).AppendValue(resolution.ToString()).AppendValue(ToStoredTime(b.TimestampUtc))
                    .AppendValue(b.Open).AppendValue(b.High).AppendValue(b.Low).AppendValue(b.Close)
                    .AppendValue(b.Volume).AppendValue(knownAt).AppendValue(source.Name).AppendValue(sourceVersion)
                    .EndRow();
            }
        }

        tx.Commit();
        return new WriteCounts(added, restated, unchanged);
    }

    /// <summary>
    /// Intraday bars of one resolution and source with a start in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>],
    /// as known at <paramref name="asOfUtc"/> (default: latest), ordered by start.
    /// </summary>
    public IReadOnlyList<StoredIntradayBar> GetIntradayBars(
        OrderbookId id, ChartResolution resolution, string source, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, DateTimeOffset? asOfUtc = null)
    {
        using DuckDBCommand cmd = Command(
            """
            SELECT bar_start, open, high, low, close, volume, known_at, source, source_version
            FROM intraday_bars
            WHERE orderbook_id = $id AND resolution = $resolution AND source = $source AND bar_start BETWEEN $from AND $to AND known_at <= $asof
            QUALIFY row_number() OVER (PARTITION BY bar_start ORDER BY known_at DESC) = 1
            ORDER BY bar_start
            """,
            ("id", id.Value), ("resolution", resolution.ToString()), ("source", source),
            ("from", fromUtc is { } f ? ToStoredTime(f) : DateTime.MinValue), ("to", toUtc is { } t ? ToStoredTime(t) : DateTime.MaxValue), ("asof", AsOf(asOfUtc)));
        using DbDataReader r = cmd.ExecuteReader();
        var result = new List<StoredIntradayBar>();
        while (r.Read())
        {
            var bar = new Bar(
                FromStoredTime(r.GetDateTime(0)), Normalize(r.GetDecimal(1)), Normalize(r.GetDecimal(2)), Normalize(r.GetDecimal(3)), Normalize(r.GetDecimal(4)), r.GetInt64(5));
            result.Add(new StoredIntradayBar(bar, resolution, FromStoredTime(r.GetDateTime(6)), r.GetString(7), r.GetString(8)));
        }

        return result;
    }

    /// <summary>The distinct Stockholm trading dates with intraday bars of this resolution (the collection's reach).</summary>
    public IReadOnlyList<DateOnly> GetIntradayDays(OrderbookId id, ChartResolution resolution, string source) =>
        [.. GetIntradayBars(id, resolution, source).Select(b => DateOnly.FromDateTime(MarketTime.ToStockholm(b.Bar.TimestampUtc).DateTime)).Distinct()];

    /// <summary>
    /// The distinct Stockholm trading dates with intraday bars of this resolution for any share (the collection's
    /// calendar; plan 17's holdout counts back from its end).
    /// </summary>
    public IReadOnlyList<DateOnly> GetIntradayCollectedDays(ChartResolution resolution, string source) => [.. CollectedDays(resolution, source)];

    /// <summary>The distinct Stockholm trading dates with intraday bars of any resolution (plan 17: the holdout's calendar).</summary>
    public IReadOnlyList<DateOnly> GetIntradayCollectedDays(string source) => [.. CollectedDays(null, source)];

    private SortedSet<DateOnly> CollectedDays(ChartResolution? resolution, string source)
    {
        // Hours are few (about nine a day), and each converts to its Stockholm date without guessing the offset in SQL.
        using DuckDBCommand cmd = resolution is { } one
            ? Command("SELECT DISTINCT date_trunc('hour', bar_start) FROM intraday_bars WHERE resolution = $resolution AND source = $source", ("resolution", one.ToString()), ("source", source))
            : Command("SELECT DISTINCT date_trunc('hour', bar_start) FROM intraday_bars WHERE source = $source", ("source", source));
        using DbDataReader r = cmd.ExecuteReader();
        var days = new SortedSet<DateOnly>();
        while (r.Read())
        {
            days.Add(DateOnly.FromDateTime(MarketTime.ToStockholm(FromStoredTime(r.GetDateTime(0))).DateTime));
        }

        return days;
    }

    // ---- spread samples ----

    /// <summary>
    /// Stores spread samples; a sample already stored for the same instrument, source and moment is kept as it was (a
    /// sample is an observation, never restated). Returns how many were new.
    /// </summary>
    public int AddSpreadSamples(IReadOnlyList<SpreadSample> samples, DataSourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(samples);
        RequireRegistered(source);
        int added = 0;
        using DbTransaction tx = _db.BeginTransaction();
        foreach (SpreadSample s in samples)
        {
            if (s.Bid <= 0m || s.Ask <= 0m || s.Ask < s.Bid || s.BidVolume < 0m || s.AskVolume < 0m)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"{s.OrderbookId} at {s.AtUtc:O}: bid {s.Bid} / ask {s.Ask} is not a quote."), nameof(samples));
            }

            foreach (decimal v in (ReadOnlySpan<decimal>)[s.Bid, s.Ask, s.BidVolume, s.AskVolume])
            {
                Validate(v, $"{s.OrderbookId} spread sample");
            }

            using DuckDBCommand cmd = Command(
                "INSERT INTO spread_samples VALUES ($id, $at, $bid, $ask, $bv, $av, $source) ON CONFLICT DO NOTHING",
                ("id", s.OrderbookId.Value), ("at", ToStoredTime(s.AtUtc)), ("bid", s.Bid), ("ask", s.Ask), ("bv", s.BidVolume), ("av", s.AskVolume), ("source", source.Name));
            added += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return added;
    }

    /// <summary>Spread samples of one instrument and source in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>], by time.</summary>
    public IReadOnlyList<SpreadSample> GetSpreadSamples(OrderbookId id, string source, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null)
    {
        using DuckDBCommand cmd = Command(
            """
            SELECT sampled_at, bid, ask, bid_volume, ask_volume FROM spread_samples
            WHERE orderbook_id = $id AND source = $source AND sampled_at BETWEEN $from AND $to
            ORDER BY sampled_at
            """,
            ("id", id.Value), ("source", source),
            ("from", fromUtc is { } f ? ToStoredTime(f) : DateTime.MinValue), ("to", toUtc is { } t ? ToStoredTime(t) : DateTime.MaxValue));
        using DbDataReader r = cmd.ExecuteReader();
        var result = new List<SpreadSample>();
        while (r.Read())
        {
            result.Add(new SpreadSample(id, FromStoredTime(r.GetDateTime(0)), Normalize(r.GetDecimal(1)), Normalize(r.GetDecimal(2)), Normalize(r.GetDecimal(3)), Normalize(r.GetDecimal(4))));
        }

        return result;
    }

    private static void Validate(Bar b)
    {
        string at = b.TimestampUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        foreach (decimal p in (ReadOnlySpan<decimal>)[b.Open, b.High, b.Low, b.Close])
        {
            if (p <= 0m)
            {
                throw new ArgumentException($"{at}: prices must be positive.", nameof(b));
            }

            Validate(p, $"{at} price");
        }

        if (b.High < b.Low || b.Open > b.High || b.Open < b.Low || b.Close > b.High || b.Close < b.Low)
        {
            throw new ArgumentException($"{at}: open/close must lie within low..high.", nameof(b));
        }

        if (b.Volume < 0)
        {
            throw new ArgumentException($"{at}: negative volume.", nameof(b));
        }
    }
}
