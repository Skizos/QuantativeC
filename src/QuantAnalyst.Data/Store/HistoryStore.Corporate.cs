using System.Data.Common;
using DuckDB.NET.Data;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Data.Store;

/// <summary>A dividend as stored, with when it was known (plan 21).</summary>
public sealed record StoredDividend(OrderbookId OrderbookId, DividendEvent Dividend, DateTimeOffset KnownAtUtc, string Source, string SourceVersion);

/// <summary>A share count as stored (plan 21): the company's number of shares on <see cref="AsOf"/>.</summary>
public sealed record StoredShareCount(OrderbookId OrderbookId, DateOnly AsOf, decimal Shares, DateTimeOffset KnownAtUtc);

/// <summary>
/// Dividends and share counts (plan 21), known-at versioned like the bars: a changed amount (Avanza restates past
/// dividends after a split) is a new row, never an overwrite.
/// </summary>
public sealed partial class HistoryStore
{
    /// <summary>Stores <paramref name="dividends"/> of one share; a dividend whose amount, currency or payment date is unchanged is skipped.</summary>
    public WriteCounts UpsertDividends(OrderbookId id, IReadOnlyList<DividendEvent> dividends, DataSourceInfo source, string sourceVersion, DateTimeOffset knownAtUtc)
    {
        ArgumentNullException.ThrowIfNull(dividends);
        RequireRegistered(source);
        if (dividends.Count == 0)
        {
            return new WriteCounts(0, 0, 0);
        }

        foreach (DividendEvent d in dividends)
        {
            if (d.Amount < 0m)
            {
                throw new ArgumentException($"{id} {d.ExDate:yyyy-MM-dd}: a dividend cannot be negative.", nameof(dividends));
            }

            Validate(d.Amount, $"{id} {d.ExDate:yyyy-MM-dd} dividend");
        }

        if (dividends.Select(d => (d.ExDate, d.Type)).Distinct().Count() != dividends.Count)
        {
            throw new ArgumentException("The same ex-date and type appear twice in one write.", nameof(dividends));
        }

        DateTime knownAt = ToStoredTime(knownAtUtc);
        Dictionary<(DateOnly, string), DividendEvent> latest = GetDividends(id, source.Name).ToDictionary(s => (s.Dividend.ExDate, s.Dividend.Type), s => s.Dividend);
        int added = 0, restated = 0, unchanged = 0;
        using DbTransaction tx = _db.BeginTransaction();
        using (DuckDBAppender appender = _db.CreateAppender("dividend_events"))
        {
            foreach (DividendEvent d in dividends)
            {
                if (latest.TryGetValue((d.ExDate, d.Type), out DividendEvent? known))
                {
                    if (known.Amount == Normalize(d.Amount) && known.Currency == d.Currency && known.PaymentDate == d.PaymentDate)
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
                    .AppendValue(id.Value).AppendValue(d.ExDate).AppendValue(d.Type).AppendValue(d.Amount).AppendValue(d.Currency)
                    .AppendValue((DateOnly?)d.PaymentDate)
                    .AppendValue(knownAt).AppendValue(source.Name).AppendValue(sourceVersion)
                    .EndRow();
            }
        }

        tx.Commit();
        return new WriteCounts(added, restated, unchanged);
    }

    /// <summary>One share's dividends from one source as known at <paramref name="asOfUtc"/> (default: latest), by ex-date.</summary>
    public IReadOnlyList<StoredDividend> GetDividends(OrderbookId id, string source, DateOnly? from = null, DateOnly? to = null, DateTimeOffset? asOfUtc = null)
    {
        using DuckDBCommand cmd = Command(
            """
            SELECT ex_date, dividend_type, amount, currency, payment_date, known_at, source, source_version
            FROM dividend_events
            WHERE orderbook_id = $id AND source = $source AND ex_date BETWEEN $from AND $to AND known_at <= $asof
            QUALIFY row_number() OVER (PARTITION BY ex_date, dividend_type ORDER BY known_at DESC) = 1
            ORDER BY ex_date, dividend_type
            """,
            ("id", id.Value), ("source", source), ("from", from ?? DateOnly.MinValue), ("to", to ?? DateOnly.MaxValue), ("asof", AsOf(asOfUtc)));
        using DbDataReader r = cmd.ExecuteReader();
        var result = new List<StoredDividend>();
        while (r.Read())
        {
            DateOnly? paid = r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4);
            var d = new DividendEvent(r.GetFieldValue<DateOnly>(0), paid, Normalize(r.GetDecimal(2)), r.GetString(3), r.GetString(1));
            result.Add(new StoredDividend(id, d, FromStoredTime(r.GetDateTime(5)), r.GetString(6), r.GetString(7)));
        }

        return result;
    }

    /// <summary>Stores one share's count for <paramref name="asOf"/>, unless it equals the latest count known.</summary>
    /// <returns>True when a row was written.</returns>
    public bool UpsertShareCount(OrderbookId id, DateOnly asOf, decimal shares, DataSourceInfo source, string sourceVersion, DateTimeOffset knownAtUtc)
    {
        RequireRegistered(source);
        if (shares <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(shares), shares, "A share count must be positive.");
        }

        if (LatestShareCount(id, source.Name, asOf) is { } known && known.Shares == decimal.Round(shares, 2))
        {
            return false;
        }

        using DuckDBCommand cmd = Command(
            "INSERT INTO share_counts VALUES ($id, $asof, $shares, $known, $source, $version)",
            ("id", id.Value), ("asof", asOf), ("shares", decimal.Round(shares, 2)), ("known", ToStoredTime(knownAtUtc)), ("source", source.Name), ("version", sourceVersion));
        cmd.ExecuteNonQuery();
        return true;
    }

    /// <summary>The latest count dated on or before <paramref name="date"/> (as known now), or null when there is none.</summary>
    public StoredShareCount? LatestShareCount(OrderbookId id, string source, DateOnly date)
    {
        using DuckDBCommand cmd = Command(
            """
            SELECT as_of, shares, known_at FROM share_counts
            WHERE orderbook_id = $id AND source = $source AND as_of <= $date
            ORDER BY as_of DESC, known_at DESC
            LIMIT 1
            """,
            ("id", id.Value), ("source", source), ("date", date));
        using DbDataReader r = cmd.ExecuteReader();
        return r.Read() ? new StoredShareCount(id, r.GetFieldValue<DateOnly>(0), Normalize(r.GetDecimal(1)), FromStoredTime(r.GetDateTime(2))) : null;
    }
}
