using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Fx;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Data.Tests;

/// <summary>
/// ADR 0005: the Riksbank's daily fixing (read strictly, over a fake HTTP handler in the documented
/// <c>[{date, value}]</c> shape; no network), and the store's FX table with known-at versioning.
/// </summary>
public sealed class FxRateTests : IDisposable
{
    private static readonly DateTimeOffset T1 = new(2026, 9, 25, 16, 30, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (RiksbankFxSource Source, FakeHandler Handler) Riksbank(HttpStatusCode status, string body = "")
    {
        var handler = new FakeHandler(status, body);
        return (new RiksbankFxSource(new HttpClient(handler) { BaseAddress = RiksbankFxSource.BaseAddress }), handler);
    }

    [Fact]
    public async Task Observations_AreReadAsSekPerUnit_FromTheSeriesAndDatesAsked()
    {
        (RiksbankFxSource source, FakeHandler handler) = Riksbank(HttpStatusCode.OK,
            """[{"date":"2026-09-25","value":9.3987},{"date":"2026-09-24","value":9.4123},{"date":"2026-09-23","value":null}]""");

        IReadOnlyList<FxRate> rates = await source.GetDailyAsync("USD", new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 25), Ct);

        Assert.Equal([new FxRate(new DateOnly(2026, 9, 24), 9.4123m), new FxRate(new DateOnly(2026, 9, 25), 9.3987m)], rates); // sorted; the empty day left out
        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.riksbank.se/swea/v1/Observations/SEKUSDPMI/2026-09-21/2026-09-25", request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.False(request.Headers.Contains("Ocp-Apim-Subscription-Key")); // keyless
        Assert.Equal("SEKCADPMI", RiksbankFxSource.SeriesId("CAD"));
        Assert.Throws<ArgumentException>(() => RiksbankFxSource.SeriesId("usd"));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "", "limits calls without a key")]
    [InlineData(HttpStatusCode.InternalServerError, "", "HTTP 500 for SEKUSDPMI")]
    [InlineData(HttpStatusCode.OK, "<html>maintenance</html>", "not JSON")]
    [InlineData(HttpStatusCode.OK, """{"date":"2026-09-25","value":9.4}""", "not a list")]
    [InlineData(HttpStatusCode.OK, """[{"date":"2026-09-25"}]""", "no date or value")]
    [InlineData(HttpStatusCode.OK, """[{"date":"25/09/2026","value":9.4}]""", "not a yyyy-MM-dd date")]
    [InlineData(HttpStatusCode.OK, """[{"date":"2026-09-25","value":"9,4"}]""", "not a number")]
    [InlineData(HttpStatusCode.OK, """[{"date":"2026-09-25","value":940.12}]""", "not a per-unit fixing")]
    [InlineData(HttpStatusCode.OK, """[{"date":"2026-09-25","value":9.4},{"date":"2026-09-25","value":9.5}]""", "a date appears twice")]
    public async Task AnythingUnexpected_IsRefused_NeverGuessed(HttpStatusCode status, string body, string expected)
    {
        (RiksbankFxSource source, _) = Riksbank(status, body);
        var ex = await Assert.ThrowsAsync<FxUnavailableException>(() => source.GetDailyAsync("USD", new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 25), Ct));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoContent_MeansNoFixing_AndALongSilentRangeMeansAWrongSeries()
    {
        (RiksbankFxSource source, _) = Riksbank(HttpStatusCode.NoContent);
        Assert.Empty(await source.GetDailyAsync("CAD", new DateOnly(2026, 9, 26), new DateOnly(2026, 9, 27), Ct)); // a weekend

        using HistoryStore store = HistoryStore.Open(_dir.File("q.duckdb"));
        var clock = new FakeTimeProvider(T1);
        FxImportReport weekend = await FxImporter.ImportAsync(store, source, "CAD", new DateOnly(2026, 9, 26), new DateOnly(2026, 9, 27), clock, Ct);
        Assert.Null(weekend.First);
        var ex = await Assert.ThrowsAsync<FxUnavailableException>(() => FxImporter.ImportAsync(store, source, "CAD", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 27), clock, Ct));
        Assert.Contains("is the series right", ex.Message, StringComparison.Ordinal);
        Assert.Empty(store.GetFxRates("CAD", RiksbankFxSource.Riksbank.Name));
    }

    [Theory]
    [InlineData("SEK")]
    [InlineData("EUR")]
    public async Task OnlyTheForeignShareCurrenciesAreImported(string currency)
    {
        using HistoryStore store = HistoryStore.Open(_dir.File("q.duckdb"));
        (RiksbankFxSource source, FakeHandler handler) = Riksbank(HttpStatusCode.OK, "[]");
        await Assert.ThrowsAsync<ArgumentException>(() => FxImporter.ImportAsync(store, source, currency, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 25), TimeProvider.System, Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheStoreKeepsEveryVersion_AReImportIsIdempotent_AndARestatementIsInvisibleBeforeItWasKnown()
    {
        using HistoryStore store = HistoryStore.Open(_dir.File("q.duckdb"));
        var clock = new FakeTimeProvider(T1);
        (RiksbankFxSource first, _) = Riksbank(HttpStatusCode.OK, """[{"date":"2026-09-24","value":9.4123},{"date":"2026-09-25","value":9.3987}]""");
        FxImportReport report = await FxImporter.ImportAsync(store, first, "USD", new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 25), clock, Ct);
        Assert.Equal(new WriteCounts(2, 0, 0), report.Rates);
        Assert.Equal((new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 25), 9.3987m), (report.First!.Value, report.Last!.Value, report.LatestSekPerUnit!.Value));

        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new WriteCounts(0, 0, 2), (await FxImporter.ImportAsync(store, first, "USD", new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 25), clock, Ct)).Rates);

        (RiksbankFxSource restated, _) = Riksbank(HttpStatusCode.OK, """[{"date":"2026-09-24","value":9.4123},{"date":"2026-09-25","value":9.4001}]""");
        Assert.Equal(new WriteCounts(0, 1, 1), (await FxImporter.ImportAsync(store, restated, "USD", new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 25), clock, Ct)).Rates);

        Assert.Equal(9.4001m, store.GetFxRates("USD", RiksbankFxSource.Riksbank.Name)[^1].Rate.SekPerUnit);
        Assert.Equal(9.3987m, store.GetFxRates("USD", RiksbankFxSource.Riksbank.Name, asOfUtc: T1.AddHours(1))[^1].Rate.SekPerUnit);

        // The latest fixing on or before a date: a Saturday uses Friday's.
        StoredFxRate? saturday = store.LatestFxRate("USD", RiksbankFxSource.Riksbank.Name, new DateOnly(2026, 9, 26));
        Assert.Equal(new DateOnly(2026, 9, 25), saturday!.Rate.Date);
        Assert.Null(store.LatestFxRate("USD", RiksbankFxSource.Riksbank.Name, new DateOnly(2026, 9, 23)));
        Assert.Null(store.LatestFxRate("CAD", RiksbankFxSource.Riksbank.Name, new DateOnly(2026, 9, 26)));
    }

    [Fact]
    public void AStoreFromBeforeTheFxTable_OpensAndGainsIt()
    {
        string path = _dir.File("old.duckdb");
        using (HistoryStore store = HistoryStore.Open(path))
        {
            store.RegisterSource(RiksbankFxSource.Riksbank);
        }

        using HistoryStore reopened = HistoryStore.Open(path); // CREATE TABLE IF NOT EXISTS: idempotent, schema version unchanged
        Assert.Empty(reopened.GetFxRates("USD", RiksbankFxSource.Riksbank.Name));
        Assert.Equal(1, HistoryStore.SchemaVersion);
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
