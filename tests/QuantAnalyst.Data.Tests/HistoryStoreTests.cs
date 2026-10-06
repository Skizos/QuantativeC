using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Data.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private static readonly OrderbookId Eric = new("5240");
    private static readonly DataSourceInfo Source = new("test-source", PointInTime: false, SurvivorshipFree: false, "test data");
    private static readonly DateTimeOffset T1 = new(2026, 9, 25, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddDays(3);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private HistoryStore Open()
    {
        HistoryStore store = HistoryStore.Open(_dir.File("quant.duckdb"));
        store.RegisterSource(Source);
        return store;
    }

    private static DailyBar Bar(int day, decimal close, long volume = 1000) =>
        new(new DateOnly(2026, 9, day), close, close + 1m, close - 1m, close, volume);

    [Fact]
    public void Dividends_AreVersioned_ARestatedAmountIsANewRowNotAnOverwrite()
    {
        using HistoryStore store = Open();
        var spring = new DividendEvent(new DateOnly(2026, 3, 26), new DateOnly(2026, 3, 31), 1.45m, "SEK", "ORDINARY");
        var autumn = new DividendEvent(new DateOnly(2026, 10, 22), null, 1.45m, "SEK", "ORDINARY");
        Assert.Equal(new WriteCounts(2, 0, 0), store.UpsertDividends(Eric, [spring, autumn], Source, "v1", T1));

        // Later: the autumn payment date is announced, and after a 2:1 split the spring amount is restated per current share.
        DividendEvent[] later = [spring with { Amount = 0.725m }, autumn with { PaymentDate = new DateOnly(2026, 10, 27) }];
        Assert.Equal(new WriteCounts(0, 2, 0), store.UpsertDividends(Eric, later, Source, "v2", T2));
        Assert.Equal(new WriteCounts(0, 0, 2), store.UpsertDividends(Eric, later, Source, "v2", T2.AddHours(1)));

        Assert.Equal([1.45m, 1.45m], store.GetDividends(Eric, Source.Name, asOfUtc: T1).Select(d => d.Dividend.Amount));
        IReadOnlyList<StoredDividend> now = store.GetDividends(Eric, Source.Name);
        Assert.Equal((0.725m, "v2", T2), (now[0].Dividend.Amount, now[0].SourceVersion, now[0].KnownAtUtc));
        Assert.Equal(later[1], now[1].Dividend);
        Assert.Equal(autumn.ExDate, Assert.Single(store.GetDividends(Eric, Source.Name, from: new DateOnly(2026, 10, 1))).Dividend.ExDate);
        Assert.Empty(store.GetDividends(new OrderbookId("5239"), Source.Name));

        Assert.Throws<ArgumentException>(() => store.UpsertDividends(Eric, [spring with { Amount = -1m }], Source, "v3", T2));
        Assert.Throws<ArgumentException>(() => store.UpsertDividends(Eric, [spring, spring with { Amount = 2m }], Source, "v3", T2));
    }

    [Fact]
    public void ShareCounts_AreStoredOnlyWhenTheyChange_AndReadAsOfADate()
    {
        using HistoryStore store = Open();
        DateOnly d1 = new(2026, 9, 28), d2 = new(2026, 9, 29), d3 = new(2026, 9, 30);
        Assert.True(store.UpsertShareCount(Eric, d1, 3_334_151_735m, Source, "v1", T1));
        Assert.False(store.UpsertShareCount(Eric, d2, 3_334_151_735m, Source, "v1", T1.AddDays(1))); // unchanged: no row
        Assert.True(store.UpsertShareCount(Eric, d3, 6_668_303_470m, Source, "v1", T1.AddDays(2)));

        Assert.Null(store.LatestShareCount(Eric, Source.Name, d1.AddDays(-1)));
        StoredShareCount before = store.LatestShareCount(Eric, Source.Name, d2)!;
        Assert.Equal((d1, 3_334_151_735m), (before.AsOf, before.Shares));
        Assert.Equal(6_668_303_470m, store.LatestShareCount(Eric, Source.Name, d3)!.Shares);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.UpsertShareCount(Eric, d3, 0m, Source, "v1", T2));
    }

    [Fact]
    public void Restatement_IsNotVisibleBeforeItsKnownAt()
    {
        using HistoryStore store = Open();
        Assert.Equal(new WriteCounts(3, 0, 0), store.UpsertDailyBars(Eric, [Bar(22, 95.1m), Bar(23, 96.2m), Bar(24, 97.64m)], Source, "v1", T1));

        // Three days later the source restates 23 September (e.g. a corporate-action adjustment).
        Assert.Equal(new WriteCounts(0, 1, 2), store.UpsertDailyBars(Eric, [Bar(22, 95.1m), Bar(23, 48.1m), Bar(24, 97.64m)], Source, "v2", T2));

        Assert.Empty(store.GetDailyBars(Eric, Source.Name, asOfUtc: T1.AddTicks(-10)));
        StoredBar before = store.GetDailyBars(Eric, Source.Name, asOfUtc: T2.AddMicroseconds(-1)).Single(b => b.Bar.Date.Day == 23);
        StoredBar atRestatement = store.GetDailyBars(Eric, Source.Name, asOfUtc: T2).Single(b => b.Bar.Date.Day == 23);
        StoredBar latest = store.GetDailyBars(Eric, Source.Name).Single(b => b.Bar.Date.Day == 23);

        Assert.Equal(96.2m, before.Bar.Close);
        Assert.Equal(T1, before.KnownAtUtc);
        Assert.Equal("v1", before.SourceVersion);
        Assert.Equal(48.1m, atRestatement.Bar.Close);
        Assert.Equal(48.1m, latest.Bar.Close);
        Assert.Equal(T2, latest.KnownAtUtc);
        Assert.Equal([T1, T2], store.GetDailyBarVersions(Eric, Source.Name));

        // Unchanged neighbours keep their original known_at.
        Assert.Equal(T1, store.GetDailyBars(Eric, Source.Name).Single(b => b.Bar.Date.Day == 22).KnownAtUtc);
    }

    [Fact]
    public void ReImport_IsIdempotent_AndDecimalsComeBackExactly()
    {
        using HistoryStore store = Open();
        DailyBar[] bars = [Bar(24, 97.64m, 4_810_703), new(new DateOnly(2026, 9, 25), 94.96m, 95.82m, 94.54m, 95.123456m, 6_090_838)];
        Assert.Equal(new WriteCounts(2, 0, 0), store.UpsertDailyBars(Eric, bars, Source, "v1", T1));
        Assert.Equal(new WriteCounts(0, 0, 2), store.UpsertDailyBars(Eric, bars, Source, "v1", T2));

        IReadOnlyList<StoredBar> stored = store.GetDailyBars(Eric, Source.Name);
        Assert.Equal(bars, stored.Select(s => s.Bar));
        Assert.Equal("97.64", stored[0].Bar.Close.ToString(System.Globalization.CultureInfo.InvariantCulture)); // no trailing zeros
        Assert.Equal([T1], store.GetDailyBarVersions(Eric, Source.Name));
    }

    [Fact]
    public void RangeAndSourceFilters()
    {
        using HistoryStore store = Open();
        var other = new DataSourceInfo("vendor", true, true, "vendor");
        store.RegisterSource(other);
        store.UpsertDailyBars(Eric, [Bar(22, 1m + 1m), Bar(23, 3m), Bar(24, 4m)], Source, "v1", T1);
        store.UpsertDailyBars(Eric, [Bar(23, 30m)], other, "v1", T1);

        Assert.Equal([23], store.GetDailyBars(Eric, Source.Name, new DateOnly(2026, 9, 23), new DateOnly(2026, 9, 23)).Select(b => b.Bar.Date.Day));
        Assert.Equal(30m, store.GetDailyBars(Eric, "vendor").Single().Bar.Close);
        Assert.Empty(store.GetDailyBars(new OrderbookId("1"), Source.Name));
        Assert.Equal(other, store.GetSource("vendor"));
    }

    [Fact]
    public void InvalidBarsAreRefused_NeverRounded()
    {
        using HistoryStore store = Open();
        Assert.Throws<ArgumentException>(() => store.UpsertDailyBars(Eric, [Bar(22, 95.1234567m)], Source, "v1", T1)); // 7 decimals
        Assert.Throws<ArgumentException>(() => store.UpsertDailyBars(Eric, [new(new DateOnly(2026, 9, 22), 10m, 9m, 11m, 10m, 1)], Source, "v1", T1)); // high < low
        Assert.Throws<ArgumentException>(() => store.UpsertDailyBars(Eric, [Bar(22, 0.5m)], Source, "v1", T1)); // low ≤ 0
        Assert.Throws<ArgumentException>(() => store.UpsertDailyBars(Eric, [Bar(22, 10m), Bar(22, 11m)], Source, "v1", T1));
        Assert.Throws<InvalidOperationException>(() => store.UpsertDailyBars(Eric, [Bar(22, 10m)], Source with { Name = "unregistered" }, "v1", T1));
        Assert.Empty(store.GetDailyBars(Eric, Source.Name));
    }

    [Fact]
    public void InstrumentMaster_KeepsVersions_AndResolvesTickers()
    {
        using HistoryStore store = Open();
        InstrumentRecord eric = InstrumentRecord.FromTradingParams(Params("ERIC B", "SE0000108656", 0.01m, T1));
        Assert.Equal(TradingModel.Continuous, eric.TradingModel);
        Assert.Equal(new DateOnly(2026, 9, 25), eric.ValidFrom);
        Assert.Equal("""[{"min":0,"max":49.99,"tick":0.01},{"min":50,"max":99.98,"tick":0.02}]""", eric.TickTableJson);

        Assert.Equal(new WriteCounts(1, 0, 0), store.UpsertInstrument(eric, "avanza-orderbook", "orderbook/1", T1));
        Assert.Equal(new WriteCounts(0, 0, 1), store.UpsertInstrument(eric with { ValidFrom = new DateOnly(2026, 9, 28) }, "avanza-orderbook", "orderbook/1", T2));

        InstrumentRecord changed = InstrumentRecord.FromTradingParams(Params("ERIC B", "SE0000108656", 0.005m, T2));
        Assert.Equal(new WriteCounts(0, 1, 0), store.UpsertInstrument(changed, "avanza-orderbook", "orderbook/2", T2));

        Assert.Contains("0.01", store.GetInstrument(Eric, T1)!.Instrument.TickTableJson, StringComparison.Ordinal);
        Assert.Contains("0.005", store.GetInstrument(Eric)!.Instrument.TickTableJson, StringComparison.Ordinal);
        Assert.Null(store.GetInstrument(Eric, T1.AddSeconds(-1)));
        Assert.Equal(Eric, store.FindByTicker("eric-b")!.Instrument.OrderbookId);
        Assert.Null(store.FindByTicker("VOLV B"));
        Assert.Single(store.ListInstruments());

        InstrumentRecord firstNorth = InstrumentRecord.FromTradingParams(Params("XYZ", "SE0000000001", 0.01m, T1) with { MarketPlace = "FNSE", OrderbookId = new OrderbookId("999") });
        Assert.Equal(TradingModel.Unknown, firstNorth.TradingModel);
    }

    [Fact]
    public void FileRoundTrip_AndNewerSchemaIsRefused()
    {
        using (HistoryStore store = Open())
        {
            store.UpsertDailyBars(Eric, [Bar(22, 95.1m)], Source, "v1", T1);
        }

        using (HistoryStore reopened = HistoryStore.Open(_dir.File("quant.duckdb")))
        {
            Assert.Single(reopened.GetDailyBars(Eric, Source.Name));
            Assert.Equal(Source, reopened.GetSource(Source.Name));
        }

        using (var db = new DuckDB.NET.Data.DuckDBConnection($"Data Source={_dir.File("quant.duckdb")}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO schema_info VALUES (99)";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<HistoryStoreException>(() => HistoryStore.Open(_dir.File("quant.duckdb")));
        Assert.Contains("schema version 99", ex.Message, StringComparison.Ordinal);
    }

    private static InstrumentTradingParams Params(string ticker, string isin, decimal firstTick, DateTimeOffset knownAt) => new(
        Eric, "Ericsson B", ticker, isin, "SEK", "XSTO", "SE", "STOCK", null,
        new TickSizeTable([new TickSizeBand(0m, 49.99m, firstTick), new TickSizeBand(50m, 99.98m, 0.02m)]),
        1, 1, null, null, knownAt);
}
