using System.Data.Common;
using DuckDB.NET.Data;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Data.Store;

/// <summary>The store file is newer than this build or damaged.</summary>
public sealed class HistoryStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Local history store on DuckDB with <b>known-at</b> versioning (master plan Phase 4):
/// <list type="bullet">
/// <item>every row carries <c>valid_from</c> (the date it describes), <c>known_at</c> (UTC, when we learned it), <c>source</c>
/// and <c>source_version</c></item>
/// <item>writes are append-only: a row is added only when it is new or differs from the latest known version, so a
/// re-import is idempotent and a changed value is a <b>restatement</b> with its own <c>known_at</c></item>
/// <item>reads take an optional "as known at" time and return, per key, the newest version known by then; a
/// restatement is invisible before its <c>known_at</c></item>
/// </list>
/// Prices are <c>DECIMAL(18,6)</c> (values with more decimals are refused, never rounded); volumes <c>BIGINT</c>.
/// One writer per file: open, work, dispose (the CLI does this per command).
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public const int SchemaVersion = 1;
    public const int PriceScale = 6;

    private static readonly string[] Schema =
    [
        "CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL)",
        """
        CREATE TABLE IF NOT EXISTS sources (
            name VARCHAR PRIMARY KEY, point_in_time BOOLEAN NOT NULL, survivorship_free BOOLEAN NOT NULL, notes VARCHAR NOT NULL)
        """,
        """
        CREATE TABLE IF NOT EXISTS instruments (
            orderbook_id VARCHAR NOT NULL, isin VARCHAR, ticker VARCHAR NOT NULL, name VARCHAR NOT NULL, currency VARCHAR NOT NULL,
            market_place VARCHAR NOT NULL, instrument_type VARCHAR NOT NULL, trading_model VARCHAR NOT NULL,
            volume_factor DECIMAL(18,6) NOT NULL, tick_table VARCHAR NOT NULL,
            valid_from DATE NOT NULL, known_at TIMESTAMP NOT NULL, source VARCHAR NOT NULL, source_version VARCHAR NOT NULL,
            PRIMARY KEY (orderbook_id, known_at))
        """,
        """
        CREATE TABLE IF NOT EXISTS daily_bars (
            orderbook_id VARCHAR NOT NULL, valid_from DATE NOT NULL,
            open DECIMAL(18,6) NOT NULL, high DECIMAL(18,6) NOT NULL, low DECIMAL(18,6) NOT NULL, close DECIMAL(18,6) NOT NULL,
            volume BIGINT NOT NULL, known_at TIMESTAMP NOT NULL, source VARCHAR NOT NULL, source_version VARCHAR NOT NULL,
            PRIMARY KEY (orderbook_id, source, valid_from, known_at))
        """,
        """
        CREATE TABLE IF NOT EXISTS fx_rates (
            currency VARCHAR NOT NULL, valid_from DATE NOT NULL, sek_per_unit DECIMAL(18,6) NOT NULL,
            known_at TIMESTAMP NOT NULL, source VARCHAR NOT NULL, source_version VARCHAR NOT NULL,
            PRIMARY KEY (currency, source, valid_from, known_at))
        """,
    ];

    private readonly DuckDBConnection _db;

    private HistoryStore(DuckDBConnection db, string path)
    {
        _db = db;
        Path = path;
    }

    public string Path { get; }

    /// <summary>Opens (or creates) the store file and its schema.</summary>
    public static HistoryStore Open(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (System.IO.Path.GetDirectoryName(full) is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
        }

        var db = new DuckDBConnection($"Data Source={full}");
        try
        {
            db.Open();
            var store = new HistoryStore(db, full);
            store.EnsureSchema();
            return store;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public void Dispose() => _db.Dispose();

    // ---- sources ----

    public void RegisterSource(DataSourceInfo source)
    {
        using DuckDBCommand cmd = Command(
            """
            INSERT INTO sources VALUES ($name, $pit, $sf, $notes)
            ON CONFLICT (name) DO UPDATE SET point_in_time = excluded.point_in_time, survivorship_free = excluded.survivorship_free, notes = excluded.notes
            """,
            ("name", source.Name), ("pit", source.PointInTime), ("sf", source.SurvivorshipFree), ("notes", source.Notes));
        cmd.ExecuteNonQuery();
    }

    public DataSourceInfo? GetSource(string name)
    {
        using DuckDBCommand cmd = Command("SELECT name, point_in_time, survivorship_free, notes FROM sources WHERE name = $name", ("name", name));
        using DbDataReader r = cmd.ExecuteReader();
        return r.Read() ? new DataSourceInfo(r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetString(3)) : null;
    }

    // ---- daily bars ----

    /// <summary>Appends the bars that are new or changed (known at <paramref name="knownAtUtc"/>) in one transaction.</summary>
    public WriteCounts UpsertDailyBars(OrderbookId id, IReadOnlyList<DailyBar> bars, DataSourceInfo source, string sourceVersion, DateTimeOffset knownAtUtc)
    {
        ArgumentNullException.ThrowIfNull(bars);
        RequireRegistered(source);
        if (bars.Count == 0)
        {
            return new WriteCounts(0, 0, 0);
        }

        foreach (DailyBar b in bars)
        {
            Validate(b);
        }

        if (bars.Select(b => b.Date).Distinct().Count() != bars.Count)
        {
            throw new ArgumentException("The same date appears twice in one write.", nameof(bars));
        }

        DateTime knownAt = ToStoredTime(knownAtUtc);
        Dictionary<DateOnly, DailyBar> latest = GetDailyBars(id, source.Name, bars.Min(b => b.Date), bars.Max(b => b.Date))
            .ToDictionary(s => s.Bar.Date, s => s.Bar);

        int added = 0, restated = 0, unchanged = 0;
        using DbTransaction tx = _db.BeginTransaction();
        using (DuckDBAppender appender = _db.CreateAppender("daily_bars"))
        {
            foreach (DailyBar b in bars)
            {
                if (latest.TryGetValue(b.Date, out DailyBar known))
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
                    .AppendValue(id.Value).AppendValue(b.Date)
                    .AppendValue(b.Open).AppendValue(b.High).AppendValue(b.Low).AppendValue(b.Close)
                    .AppendValue(b.Volume).AppendValue(knownAt).AppendValue(source.Name).AppendValue(sourceVersion)
                    .EndRow();
            }
        }

        tx.Commit();
        return new WriteCounts(added, restated, unchanged);
    }

    /// <summary>Daily bars of one source, as known at <paramref name="asOfUtc"/> (default: latest), ordered by date.</summary>
    public IReadOnlyList<StoredBar> GetDailyBars(OrderbookId id, string source, DateOnly? from = null, DateOnly? to = null, DateTimeOffset? asOfUtc = null)
    {
        using DuckDBCommand cmd = Command(
            """
            SELECT valid_from, open, high, low, close, volume, known_at, source, source_version
            FROM daily_bars
            WHERE orderbook_id = $id AND source = $source AND valid_from BETWEEN $from AND $to AND known_at <= $asof
            QUALIFY row_number() OVER (PARTITION BY valid_from ORDER BY known_at DESC) = 1
            ORDER BY valid_from
            """,
            ("id", id.Value), ("source", source), ("from", from ?? DateOnly.MinValue), ("to", to ?? DateOnly.MaxValue), ("asof", AsOf(asOfUtc)));
        using DbDataReader r = cmd.ExecuteReader();
        var result = new List<StoredBar>();
        while (r.Read())
        {
            var bar = new DailyBar(
                r.GetFieldValue<DateOnly>(0), Normalize(r.GetDecimal(1)), Normalize(r.GetDecimal(2)), Normalize(r.GetDecimal(3)), Normalize(r.GetDecimal(4)), r.GetInt64(5));
            result.Add(new StoredBar(bar, FromStoredTime(r.GetDateTime(6)), r.GetString(7), r.GetString(8)));
        }

        return result;
    }

    /// <summary>Every <c>known_at</c> at which this series changed (for audit and "what did we know when").</summary>
    public IReadOnlyList<DateTimeOffset> GetDailyBarVersions(OrderbookId id, string source)
    {
        using DuckDBCommand cmd = Command(
            "SELECT DISTINCT known_at FROM daily_bars WHERE orderbook_id = $id AND source = $source ORDER BY known_at",
            ("id", id.Value), ("source", source));
        using DbDataReader r = cmd.ExecuteReader();
        var result = new List<DateTimeOffset>();
        while (r.Read())
        {
            result.Add(FromStoredTime(r.GetDateTime(0)));
        }

        return result;
    }

    // ---- FX rates (ADR 0005) ----

    /// <summary>
    /// Appends the rates that are new or changed (known at <paramref name="knownAtUtc"/>) in one transaction, like
    /// <see cref="UpsertDailyBars"/>. The table was added in place (schema version 1 still): an older build ignores it.
    /// </summary>
    public WriteCounts UpsertFxRates(string currency, IReadOnlyList<FxRate> rates, DataSourceInfo source, string sourceVersion, DateTimeOffset knownAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentNullException.ThrowIfNull(rates);
        RequireRegistered(source);
        if (rates.Count == 0)
        {
            return new WriteCounts(0, 0, 0);
        }

        foreach (FxRate r in rates)
        {
            if (r.SekPerUnit <= 0m)
            {
                throw new ArgumentException($"{currency} {r.Date:yyyy-MM-dd}: the rate must be positive.", nameof(rates));
            }

            Validate(r.SekPerUnit, $"{currency} {r.Date:yyyy-MM-dd} rate");
        }

        if (rates.Select(r => r.Date).Distinct().Count() != rates.Count)
        {
            throw new ArgumentException("The same date appears twice in one write.", nameof(rates));
        }

        DateTime knownAt = ToStoredTime(knownAtUtc);
        Dictionary<DateOnly, decimal> latest = GetFxRates(currency, source.Name, rates.Min(r => r.Date), rates.Max(r => r.Date))
            .ToDictionary(s => s.Rate.Date, s => s.Rate.SekPerUnit);
        int added = 0, restated = 0, unchanged = 0;
        using DbTransaction tx = _db.BeginTransaction();
        using (DuckDBAppender appender = _db.CreateAppender("fx_rates"))
        {
            foreach (FxRate r in rates)
            {
                if (latest.TryGetValue(r.Date, out decimal known))
                {
                    if (known == Normalize(r.SekPerUnit))
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
                    .AppendValue(currency).AppendValue(r.Date).AppendValue(r.SekPerUnit)
                    .AppendValue(knownAt).AppendValue(source.Name).AppendValue(sourceVersion)
                    .EndRow();
            }
        }

        tx.Commit();
        return new WriteCounts(added, restated, unchanged);
    }

    /// <summary>One currency's rates from one source, as known at <paramref name="asOfUtc"/> (default: latest), ordered by date.</summary>
    public IReadOnlyList<StoredFxRate> GetFxRates(string currency, string source, DateOnly? from = null, DateOnly? to = null, DateTimeOffset? asOfUtc = null)
    {
        using DuckDBCommand cmd = Command(
            """
            SELECT valid_from, sek_per_unit, known_at, source, source_version
            FROM fx_rates
            WHERE currency = $ccy AND source = $source AND valid_from BETWEEN $from AND $to AND known_at <= $asof
            QUALIFY row_number() OVER (PARTITION BY valid_from ORDER BY known_at DESC) = 1
            ORDER BY valid_from
            """,
            ("ccy", currency), ("source", source), ("from", from ?? DateOnly.MinValue), ("to", to ?? DateOnly.MaxValue), ("asof", AsOf(asOfUtc)));
        using DbDataReader r = cmd.ExecuteReader();
        var result = new List<StoredFxRate>();
        while (r.Read())
        {
            result.Add(new StoredFxRate(currency, new FxRate(r.GetFieldValue<DateOnly>(0), Normalize(r.GetDecimal(1))), FromStoredTime(r.GetDateTime(2)), r.GetString(3), r.GetString(4)));
        }

        return result;
    }

    /// <summary>The latest rate dated on or before <paramref name="date"/>, or null when there is none.</summary>
    public StoredFxRate? LatestFxRate(string currency, string source, DateOnly date, DateTimeOffset? asOfUtc = null)
    {
        using DuckDBCommand cmd = Command(
            """
            SELECT valid_from, sek_per_unit, known_at, source, source_version
            FROM fx_rates
            WHERE currency = $ccy AND source = $source AND valid_from <= $date AND known_at <= $asof
            QUALIFY row_number() OVER (PARTITION BY valid_from ORDER BY known_at DESC) = 1
            ORDER BY valid_from DESC
            LIMIT 1
            """,
            ("ccy", currency), ("source", source), ("date", date), ("asof", AsOf(asOfUtc)));
        using DbDataReader r = cmd.ExecuteReader();
        return r.Read()
            ? new StoredFxRate(currency, new FxRate(r.GetFieldValue<DateOnly>(0), Normalize(r.GetDecimal(1))), FromStoredTime(r.GetDateTime(2)), r.GetString(3), r.GetString(4))
            : null;
    }

    // ---- instrument master ----

    /// <summary>Stores a new instrument version when the attributes changed (or the instrument is new).</summary>
    public WriteCounts UpsertInstrument(InstrumentRecord record, string source, string sourceVersion, DateTimeOffset knownAtUtc)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record.VolumeFactor, "volume factor");
        StoredInstrument? known = GetInstrument(record.OrderbookId);
        if (known is not null && known.Instrument.SameAttributes(record))
        {
            return new WriteCounts(0, 0, 1);
        }

        using DuckDBCommand cmd = Command(
            "INSERT INTO instruments VALUES ($id, $isin, $ticker, $name, $ccy, $mp, $type, $model, $vf, $tick, $vfrom, $known, $source, $sv)",
            ("id", record.OrderbookId.Value), ("isin", record.Isin), ("ticker", record.Ticker), ("name", record.Name), ("ccy", record.Currency),
            ("mp", record.MarketPlace), ("type", record.InstrumentType), ("model", record.TradingModel.ToString()), ("vf", record.VolumeFactor),
            ("tick", record.TickTableJson), ("vfrom", record.ValidFrom), ("known", ToStoredTime(knownAtUtc)), ("source", source), ("sv", sourceVersion));
        cmd.ExecuteNonQuery();
        return known is null ? new WriteCounts(1, 0, 0) : new WriteCounts(0, 1, 0);
    }

    public StoredInstrument? GetInstrument(OrderbookId id, DateTimeOffset? asOfUtc = null) =>
        QueryInstruments("orderbook_id = $id", asOfUtc, ("id", id.Value)).SingleOrDefault();

    /// <summary>Exact ticker match (case-insensitive; "ERIC-B" also matches "ERIC B").</summary>
    public StoredInstrument? FindByTicker(string ticker, DateTimeOffset? asOfUtc = null)
    {
        string wanted = NormalizeTicker(ticker);
        return ListInstruments(asOfUtc).FirstOrDefault(i => NormalizeTicker(i.Instrument.Ticker) == wanted);
    }

    public IReadOnlyList<StoredInstrument> ListInstruments(DateTimeOffset? asOfUtc = null) => QueryInstruments("TRUE", asOfUtc);

    internal static string NormalizeTicker(string ticker) =>
        ticker.Trim().Replace('-', ' ').Replace('_', ' ').ToUpperInvariant();

    private List<StoredInstrument> QueryInstruments(string where, DateTimeOffset? asOfUtc, params (string Name, object? Value)[] parameters)
    {
        using DuckDBCommand cmd = Command(
            $"""
            SELECT orderbook_id, isin, ticker, name, currency, market_place, instrument_type, trading_model, volume_factor, tick_table,
                   valid_from, known_at, source, source_version
            FROM instruments
            WHERE {where} AND known_at <= $asof
            QUALIFY row_number() OVER (PARTITION BY orderbook_id ORDER BY known_at DESC) = 1
            ORDER BY ticker
            """,
            [.. parameters, ("asof", AsOf(asOfUtc))]);
        using DbDataReader r = cmd.ExecuteReader();
        var result = new List<StoredInstrument>();
        while (r.Read())
        {
            var record = new InstrumentRecord(
                new OrderbookId(r.GetString(0)),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.GetString(5),
                r.GetString(6),
                Enum.Parse<TradingModel>(r.GetString(7)),
                Normalize(r.GetDecimal(8)),
                r.GetString(9),
                r.GetFieldValue<DateOnly>(10));
            result.Add(new StoredInstrument(record, FromStoredTime(r.GetDateTime(11)), r.GetString(12), r.GetString(13)));
        }

        return result;
    }

    // ---- helpers ----

    private void EnsureSchema()
    {
        foreach (string ddl in Schema)
        {
            using DuckDBCommand cmd = Command(ddl);
            cmd.ExecuteNonQuery();
        }

        using DuckDBCommand read = Command("SELECT max(version) FROM schema_info");
        object? version = read.ExecuteScalar();
        if (version is null or DBNull)
        {
            using DuckDBCommand insert = Command("INSERT INTO schema_info VALUES ($v)", ("v", SchemaVersion));
            insert.ExecuteNonQuery();
        }
        else if (Convert.ToInt32(version, System.Globalization.CultureInfo.InvariantCulture) > SchemaVersion)
        {
            throw new HistoryStoreException($"'{Path}' has schema version {version}; this build understands up to {SchemaVersion}. Update qa.");
        }
    }

    private void RequireRegistered(DataSourceInfo source)
    {
        if (GetSource(source.Name) is not { } stored || stored != source)
        {
            throw new InvalidOperationException($"Register source '{source.Name}' (with its current traits) before writing rows for it.");
        }
    }

    private DuckDBCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        DuckDBCommand cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            cmd.Parameters.Add(new DuckDBParameter(name, value ?? DBNull.Value));
        }

        return cmd;
    }

    private static void Validate(DailyBar b)
    {
        foreach (decimal p in (ReadOnlySpan<decimal>)[b.Open, b.High, b.Low, b.Close])
        {
            if (p <= 0m)
            {
                throw new ArgumentException($"{b.Date:yyyy-MM-dd}: prices must be positive.", nameof(b));
            }

            Validate(p, $"{b.Date:yyyy-MM-dd} price");
        }

        if (b.High < b.Low || b.Open > b.High || b.Open < b.Low || b.Close > b.High || b.Close < b.Low)
        {
            throw new ArgumentException($"{b.Date:yyyy-MM-dd}: open/close must lie within low..high.", nameof(b));
        }

        if (b.Volume < 0)
        {
            throw new ArgumentException($"{b.Date:yyyy-MM-dd}: negative volume.", nameof(b));
        }
    }

    private static void Validate(decimal value, string what)
    {
        if (Normalize(value).Scale > PriceScale || Math.Abs(value) >= 1_000_000_000_000m)
        {
            throw new ArgumentException($"{what} {value} does not fit DECIMAL(18,{PriceScale}); refusing to round it.", nameof(value));
        }
    }

    /// <summary>Drops trailing zeros (DECIMAL(18,6) reads back as 97.640000).</summary>
    private static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;

    /// <summary>Stored as naive UTC with microsecond precision (DuckDB <c>TIMESTAMP</c>).</summary>
    private static DateTime ToStoredTime(DateTimeOffset utc)
    {
        DateTime t = utc.UtcDateTime;
        return new DateTime(t.Ticks - (t.Ticks % 10), DateTimeKind.Unspecified);
    }

    private static DateTimeOffset FromStoredTime(DateTime stored) => new(DateTime.SpecifyKind(stored, DateTimeKind.Utc));

    private static DateTime AsOf(DateTimeOffset? asOfUtc) => asOfUtc is { } t ? ToStoredTime(t) : DateTime.MaxValue;
}
