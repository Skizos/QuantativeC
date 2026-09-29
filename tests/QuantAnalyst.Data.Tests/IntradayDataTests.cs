using DuckDB.NET.Data;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Intraday;
using QuantAnalyst.Data.Live;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Data.Tests;

/// <summary>
/// Plan 17 step A2: intraday bars and spread samples in the store, the importer's strictness (the resolution asked, the
/// bar grid, no half bars), the research list and the once-a-minute spread sampler.
/// </summary>
public sealed class IntradayDataTests : IDisposable
{
    private static readonly OrderbookId Eric = new("5240");

    // Tuesday 2026-09-29, 09:00 Stockholm (CEST) = 07:00 UTC.
    private static readonly DateTimeOffset Open = new(2026, 9, 29, 7, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Bar B(DateTimeOffset start, decimal close = 95m, long volume = 1000) => new(start, 94.9m, Math.Max(95.1m, close), 94.8m, close, volume);

    private static Bar[] Minutes(DateTimeOffset from, int count, TimeSpan step) => [.. Enumerable.Range(0, count).Select(i => B(from + (i * step)))];

    private HistoryStore Store()
    {
        HistoryStore store = HistoryStore.Open(_dir.File("q.duckdb"));
        store.RegisterSource(AvanzaChartImporter.AvanzaPriceChart);
        return store;
    }

    [Fact]
    public void IntradayBars_AreKeptPerResolution_AReImportIsIdempotent_AndARestatementIsInvisibleBeforeItWasKnown()
    {
        using HistoryStore store = Store();
        DataSourceInfo src = AvanzaChartImporter.AvanzaPriceChart;
        DateTimeOffset t1 = Open.AddHours(11), t2 = t1.AddDays(1);

        Assert.Equal(new WriteCounts(5, 0, 0), store.UpsertIntradayBars(Eric, ChartResolution.Minute, Minutes(Open, 5, TimeSpan.FromMinutes(1)), src, "v1", t1));
        Assert.Equal(new WriteCounts(2, 0, 0), store.UpsertIntradayBars(Eric, ChartResolution.FiveMinutes, Minutes(Open, 2, TimeSpan.FromMinutes(5)), src, "v1", t1));
        Assert.Equal(new WriteCounts(0, 0, 5), store.UpsertIntradayBars(Eric, ChartResolution.Minute, Minutes(Open, 5, TimeSpan.FromMinutes(1)), src, "v1", t2));

        Bar corrected = B(Open.AddMinutes(2), close: 95.3m);
        Assert.Equal(new WriteCounts(0, 1, 0), store.UpsertIntradayBars(Eric, ChartResolution.Minute, [corrected], src, "v2", t2));

        Assert.Equal(5, store.GetIntradayBars(Eric, ChartResolution.Minute, src.Name).Count);
        Assert.Equal(2, store.GetIntradayBars(Eric, ChartResolution.FiveMinutes, src.Name).Count);
        Assert.Equal(95.3m, store.GetIntradayBars(Eric, ChartResolution.Minute, src.Name)[2].Bar.Close);
        Assert.Equal(95m, store.GetIntradayBars(Eric, ChartResolution.Minute, src.Name, asOfUtc: t1)[2].Bar.Close); // as known then
        Assert.Equal([Open.AddMinutes(1), Open.AddMinutes(2)],
            store.GetIntradayBars(Eric, ChartResolution.Minute, src.Name, Open.AddMinutes(1), Open.AddMinutes(2)).Select(b => b.Bar.TimestampUtc));
        Assert.Equal([new DateOnly(2026, 9, 29)], store.GetIntradayDays(Eric, ChartResolution.Minute, src.Name));
    }

    [Theory]
    [InlineData(97, 96, 94, 95, "open/close must lie within low..high")]
    [InlineData(95, 96, 94, 95.1234567, "does not fit DECIMAL(18,6)")]
    [InlineData(95, 96, 94, -1, "prices must be positive")]
    public void ABadBar_IsRefused_AndNothingIsStored(double open, double high, double low, double close, string expected)
    {
        using HistoryStore store = Store();
        var bad = new Bar(Open, (decimal)open, (decimal)high, (decimal)low, (decimal)close, 10);
        var ex = Assert.Throws<ArgumentException>(() =>
            store.UpsertIntradayBars(Eric, ChartResolution.Minute, [B(Open.AddMinutes(1)), bad], AvanzaChartImporter.AvanzaPriceChart, "v", Open));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Empty(store.GetIntradayBars(Eric, ChartResolution.Minute, AvanzaChartImporter.AvanzaPriceChart.Name));
    }

    [Fact]
    public void SpreadSamples_AreObservations_KeptOnce()
    {
        using HistoryStore store = Store();
        store.RegisterSource(SpreadSampler.Source);
        SpreadSample[] samples = [new(Eric, Open, 94.96m, 94.98m, 600m, 185m), new(Eric, Open.AddMinutes(1), 94.98m, 95.00m, 300m, 200m)];

        Assert.Equal(2, store.AddSpreadSamples(samples, SpreadSampler.Source));
        Assert.Equal(0, store.AddSpreadSamples([samples[0] with { Ask = 95.5m }], SpreadSampler.Source)); // the same moment: kept as it was

        IReadOnlyList<SpreadSample> read = store.GetSpreadSamples(Eric, SpreadSampler.Source.Name);
        Assert.Equal(samples, read);
        Assert.Equal(0.02m / 94.97m, read[0].RelativeSpread);
        Assert.Single(store.GetSpreadSamples(Eric, SpreadSampler.Source.Name, Open.AddSeconds(30)));

        var ex = Assert.Throws<ArgumentException>(() => store.AddSpreadSamples([new(Eric, Open.AddMinutes(2), 95m, 94m, 1m, 1m)], SpreadSampler.Source));
        Assert.Contains("is not a quote", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoreFromBeforeTheIntradayTables_OpensAndGainsThem()
    {
        string path = _dir.File("old.duckdb");
        using (HistoryStore.Open(path))
        {
        }

        using (var db = new DuckDBConnection($"Data Source={path}"))
        {
            db.Open();
            using DuckDBCommand drop = db.CreateCommand();
            drop.CommandText = "DROP TABLE intraday_bars; DROP TABLE spread_samples;";
            drop.ExecuteNonQuery();
        }

        using HistoryStore reopened = HistoryStore.Open(path);
        Assert.Empty(reopened.GetIntradayBars(Eric, ChartResolution.Minute, "any"));
        Assert.Empty(reopened.GetSpreadSamples(Eric, "any"));
        Assert.Equal(1, HistoryStore.SchemaVersion);
    }

    // ---- the importer ----

    private static PriceHistory Chart(ChartResolution resolution, params Bar[] bars) => new(bars, resolution, 94.96m);

    [Fact]
    public async Task TheImporter_StoresTheClosedBars_AndLeavesTheOpenOneOut()
    {
        var time = new FakeTimeProvider(Open.AddMinutes(12).AddSeconds(30)); // 09:12:30: the 09:10 five-minute bar is still open
        var gateway = new FakeGateway { Chart = (_, _, r) => Chart(r!.Value, Minutes(Open, 3, TimeSpan.FromMinutes(5))) };
        using HistoryStore store = HistoryStore.Open(_dir.File("q.duckdb"));

        IntradayImportReport report = await IntradayImporter.ImportAsync(store, gateway, Eric, ChartPeriod.Today, ChartResolution.FiveMinutes, "v", time, Ct);

        Assert.Equal((ChartPeriod.Today, (ChartResolution?)ChartResolution.FiveMinutes), Assert.Single(gateway.ChartRequests));
        Assert.Equal((new WriteCounts(2, 0, 0), 1, 1), (report.Bars, report.InProgress, report.Days));
        Assert.Equal((Open, Open.AddMinutes(5)), (report.FirstUtc!.Value, report.LastUtc!.Value));
        Assert.Equal(2, store.GetIntradayBars(Eric, ChartResolution.FiveMinutes, AvanzaChartImporter.AvanzaPriceChart.Name).Count);
    }

    public static TheoryData<string, int, string> Refusals => new()
    {
        { "another resolution", 0, "Avanza answered with TenMinutes bars for Today, not FiveMinutes; nothing was stored" },
        { "off the grid", 1, "starts at 2026-09-29 09:02:00 Stockholm time, off the 5-minute grid" },
        { "out of order", 2, "out of order or twice at 2026-09-29 09:00" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task AnAnswerThatIsNotWhatWasAsked_IsRefused_AndNothingIsStored(string what, int kind, string expected)
    {
        Assert.NotEmpty(what);
        PriceHistory answer = kind switch
        {
            0 => Chart(ChartResolution.TenMinutes, B(Open)),
            1 => Chart(ChartResolution.FiveMinutes, B(Open), B(Open.AddMinutes(2))),
            _ => Chart(ChartResolution.FiveMinutes, B(Open.AddMinutes(5)), B(Open)),
        };
        var gateway = new FakeGateway { Chart = (_, _, _) => answer };
        using HistoryStore store = HistoryStore.Open(_dir.File("q.duckdb"));

        var ex = await Assert.ThrowsAsync<HistoryImportException>(() =>
            IntradayImporter.ImportAsync(store, gateway, Eric, ChartPeriod.Today, ChartResolution.FiveMinutes, "v", new FakeTimeProvider(Open.AddHours(12)), Ct));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Empty(store.GetIntradayBars(Eric, ChartResolution.FiveMinutes, AvanzaChartImporter.AvanzaPriceChart.Name));
    }

    [Fact]
    public async Task OnlyOneAndFiveMinuteBars_ForShortPeriods_AreImported()
    {
        using HistoryStore store = HistoryStore.Open(_dir.File("q.duckdb"));
        var gateway = new FakeGateway();
        var time = new FakeTimeProvider(Open);
        await Assert.ThrowsAsync<ArgumentException>(() => IntradayImporter.ImportAsync(store, gateway, Eric, ChartPeriod.Today, ChartResolution.Hour, "v", time, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => IntradayImporter.ImportAsync(store, gateway, Eric, ChartPeriod.OneYear, ChartResolution.Minute, "v", time, Ct));
        Assert.Empty(gateway.ChartRequests);
    }

    // ---- the research list ----

    [Fact]
    public void TheResearchList_RoundTrips_AndRefusesDuplicatesAndMoreThanThirty()
    {
        string path = _dir.File(ResearchList.FileName);
        Assert.Empty(ResearchList.Load(path).Entries); // no file: empty

        ResearchList list = ResearchList.Empty.With(new ResearchEntry(new OrderbookId("5269"), "VOLV B", "Volvo B")).With(new ResearchEntry(Eric, "ERIC B", "Ericsson B"));
        list.Save(path);
        ResearchList read = ResearchList.Load(path);
        Assert.Equal(["ERIC B", "VOLV B"], read.Entries.Select(e => e.Ticker));
        Assert.Contains("nothing here is traded", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(["VOLV B"], read.Without(Eric).Entries.Select(e => e.Ticker));

        Assert.Throws<ResearchListException>(() => new ResearchList([new ResearchEntry(Eric, "ERIC B", "a"), new ResearchEntry(Eric, "ERIC B", "b")]));
        var tooMany = Assert.Throws<ResearchListException>(() => new ResearchList(
            Enumerable.Range(1, 31).Select(i => new ResearchEntry(new OrderbookId(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), $"T{i}", "x"))));
        Assert.Contains("at most 30", tooMany.Message, StringComparison.Ordinal);

        File.WriteAllText(path, """{ "format": "qa-universe/1", "instruments": [] }""");
        Assert.Contains("format must be qa-research-universe/1", Assert.Throws<ResearchListException>(() => ResearchList.Load(path)).Message, StringComparison.Ordinal);
    }

    // ---- the spread sampler ----

    private static Quote Q(DateTimeOffset at, decimal? bid = 94.96m, decimal? ask = 94.98m, bool stale = false, OrderbookId? id = null) => new(
        id ?? Eric, bid, 600m, ask, 185m, 94.97m, at, 1000m, [], QuoteSource.Stream, at, null, at, at, stale, stale ? "stream down" : null);

    [Fact]
    public void TheSampler_TakesTheFirstFreshTwoSidedQuote_OfEachMinute_PerInstrument()
    {
        var sampler = new SpreadSampler();
        Assert.True(sampler.Offer(Q(Open.AddSeconds(3))));
        Assert.False(sampler.Offer(Q(Open.AddSeconds(40)))); // the same minute
        Assert.True(sampler.Offer(Q(Open.AddSeconds(41), id: new OrderbookId("5269")))); // another instrument
        Assert.False(sampler.Offer(Q(Open.AddMinutes(1), stale: true)));
        Assert.False(sampler.Offer(Q(Open.AddMinutes(1), ask: null)));
        Assert.False(sampler.Offer(Q(Open.AddMinutes(1), bid: 95m, ask: 94m)));
        Assert.True(sampler.Offer(Q(Open.AddMinutes(1).AddSeconds(5))));

        IReadOnlyList<SpreadSample> taken = sampler.Drain();
        Assert.Equal(3, taken.Count);
        Assert.Equal((94.96m, 94.98m, 600m, 185m), (taken[0].Bid, taken[0].Ask, taken[0].BidVolume, taken[0].AskVolume));
        Assert.Equal(0, sampler.Count);
        Assert.False(sampler.Offer(Q(Open.AddMinutes(1).AddSeconds(30)))); // still that minute after a drain
    }
}
