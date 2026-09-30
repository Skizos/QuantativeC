using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Calendar;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Data.Tests;

public sealed class HistoryImportTests : IDisposable
{
    private static readonly OrderbookId Eric = new("5240");
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 16, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Dispose();

    /// <summary>A daily bar stamped the way Avanza does: Stockholm midnight of the trading date.</summary>
    private static Bar AvanzaBar(DateOnly date, decimal close)
    {
        Assert.True(MarketTime.TryStockholmToUtc(date.ToDateTime(TimeOnly.MinValue), out DateTimeOffset utc));
        return new Bar(utc, close, close + 1m, close - 1m, close, 1000);
    }

    [Fact]
    public void AvanzaDailyBarTimestamp_IsStockholmMidnight()
    {
        // Recorded 2026-09-25 (recordings/fixtures/avanza/2026-09-25/035-price-chart.json): first bar of one_month/day.
        Assert.Equal(new DateOnly(2026, 8, 25), AvanzaChartImporter.TradingDate(DateTimeOffset.FromUnixTimeMilliseconds(1787608800000)));
        Assert.Equal(new DateOnly(2026, 1, 5), AvanzaChartImporter.TradingDate(new DateTimeOffset(2026, 1, 4, 23, 0, 0, TimeSpan.Zero))); // CET
        Assert.Throws<HistoryImportException>(() => AvanzaChartImporter.TradingDate(new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("2026-09-20", ChartPeriod.OneWeek)]
    [InlineData("2026-09-01", ChartPeriod.OneMonth)]
    [InlineData("2026-07-01", ChartPeriod.ThreeMonths)]
    [InlineData("2025-09-25", ChartPeriod.OneYear)]
    [InlineData("2024-01-01", ChartPeriod.ThreeYears)]
    [InlineData("2016-01-01", ChartPeriod.FiveYears)]
    public void PeriodCoversTheRequestedStart(string from, ChartPeriod expected) =>
        Assert.Equal(expected, AvanzaChartImporter.PeriodCovering(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), new DateOnly(2026, 9, 25)));

    [Fact]
    public void DividendCheck_ReadsTheOvernightGapOnEachExDate()
    {
        static DailyBar Day(int month, int day, decimal open, decimal close) => new(new DateOnly(2026, month, day), open, Math.Max(open, close), Math.Min(open, close), close, 1000);
        DailyBar[] bars =
        [
            Day(3, 25, 100m, 100m), Day(3, 26, 98.1m, 98.5m), // ex 2.00: opens 1.9 % lower (price-only)
            Day(9, 24, 100m, 100m), Day(9, 25, 100.2m, 99m), // ex 2.00: opens 0.2 % higher (adjusted)
            Day(9, 28, 100m, 100m), Day(9, 29, 99.9m, 99m), // ex 0.10: too small to see
        ];
        DividendEvent Ex(int month, int day, decimal amount, string currency = "SEK") => new(new DateOnly(2026, month, day), null, amount, currency, "ORDINARY");

        (IReadOnlyList<DividendGap> gaps, DividendVerdict verdict) = DividendCheck.Check(
            [Ex(9, 25, 2m), Ex(3, 26, 2m), Ex(9, 29, 0.1m), Ex(1, 15, 2m), Ex(12, 1, 2m), Ex(9, 29, 1m, "USD")], bars, "SEK");

        Assert.Equal(["no bars around it", "price-only", "adjusted", "too small to see", "paid in USD, the share trades in SEK", "not yet in the history"], gaps.Select(g => g.Reading));
        Assert.Equal((0.02m, -0.019m), (gaps[1].Yield!.Value, gaps[1].Gap!.Value));
        Assert.Equal(DividendVerdict.Unclear, verdict); // one each way

        Assert.Equal(DividendVerdict.PriceOnly, DividendCheck.Check([Ex(3, 26, 2m)], bars, "SEK").Verdict);
        Assert.Equal(DividendVerdict.Adjusted, DividendCheck.Check([Ex(9, 25, 2m)], bars, "SEK").Verdict);
        Assert.Equal(DividendVerdict.NoData, DividendCheck.Check([Ex(9, 29, 0.1m)], bars, "SEK").Verdict);
        Assert.Equal(DividendVerdict.NoData, DividendCheck.Check([Ex(3, 26, 2m)], [], "SEK").Verdict);
    }

    [Fact]
    public async Task CorporateImport_StoresDividendsAndTheCount_AndReturnsTheCountStoredBeforeToday()
    {
        decimal shares = 3_334_151_735m;
        var dividend = new DividendEvent(new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 1), 1.45m, "SEK", "ORDINARY");
        var gateway = new FakeGateway { Corporate = id => new CorporateData(id, [dividend], shares, _time.GetUtcNow()) };
        using HistoryStore store = HistoryStore.Open(_dir.File("quant.duckdb"));

        // The first time there is no earlier count; a second fetch the same day compares with the same earlier count.
        CorporateSnapshot first = await CorporateDataImporter.ImportAsync(store, gateway, Eric, "stock-details/test", _time, TestContext.Current.CancellationToken);
        Assert.Null(first.PreviousShares);
        Assert.Equal(dividend, Assert.Single(store.GetDividends(Eric, CorporateDataImporter.AvanzaStockDetails.Name)).Dividend);
        Assert.Null((await CorporateDataImporter.ImportAsync(store, gateway, Eric, "stock-details/test", _time, TestContext.Current.CancellationToken)).PreviousShares);

        // The next day the count has doubled (a 2:1 split): the snapshot carries yesterday's count for the split check.
        _time.Advance(TimeSpan.FromDays(1));
        shares *= 2;
        CorporateSnapshot next = await CorporateDataImporter.ImportAsync(store, gateway, Eric, "stock-details/test", _time, TestContext.Current.CancellationToken);
        Assert.Equal((3_334_151_735m, 6_668_303_470m), (next.PreviousShares!.Value, next.Data.SharesOutstanding!.Value));
        Assert.Equal(6_668_303_470m, store.LatestShareCount(Eric, CorporateDataImporter.AvanzaStockDetails.Name, new DateOnly(2026, 9, 26))!.Shares);
    }

    [Fact]
    public async Task Import_WritesLabelledBars_AndReimportIsUnchanged()
    {
        var gateway = new FakeGateway
        {
            Chart = (_, _, _) => new PriceHistory(
                [AvanzaBar(new DateOnly(2026, 9, 23), 96m), AvanzaBar(new DateOnly(2026, 9, 24), 97m), AvanzaBar(new DateOnly(2026, 9, 25), 95m)],
                ChartResolution.Day, 94m),
        };
        var provider = new AvanzaChartImporter(gateway, _time, "price-chart/test");
        using HistoryStore store = HistoryStore.Open(_dir.File("q.duckdb"));
        var importer = new HistoryImporter(store, _time, MarketCalendarLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "config")));

        ImportReport report = await importer.ImportAsync(provider, Eric, new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 25), TestContext.Current.CancellationToken);
        Assert.Equal(new WriteCounts(2, 0, 0), report.Bars);
        Assert.Equal(new DateOnly(2026, 9, 24), report.FirstDate);
        Assert.Empty(report.Warnings);
        Assert.Equal([(ChartPeriod.OneWeek, (ChartResolution?)ChartResolution.Day)], gateway.ChartRequests);
        Assert.Equal(AvanzaChartImporter.AvanzaPriceChart, store.GetSource("avanza-price-chart"));
        Assert.Equal("avanza-price-chart — NOT survivorship-free, NOT point-in-time", AvanzaChartImporter.AvanzaPriceChart.Label);
        Assert.All(store.GetDailyBars(Eric, "avanza-price-chart"), b => Assert.Equal("price-chart/test", b.SourceVersion));

        _time.Advance(TimeSpan.FromDays(1));
        report = await importer.ImportAsync(provider, Eric, new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 25), TestContext.Current.CancellationToken);
        Assert.Equal(new WriteCounts(0, 0, 2), report.Bars);
    }

    [Fact]
    public async Task Import_RefusesNonDailyResolution()
    {
        var gateway = new FakeGateway { Chart = (_, _, _) => new PriceHistory([AvanzaBar(new DateOnly(2026, 9, 21), 96m)], ChartResolution.Week, null) };
        var provider = new AvanzaChartImporter(gateway, _time, "v");
        var ex = await Assert.ThrowsAsync<HistoryImportException>(() =>
            provider.GetDailyBarsAsync(Eric, new DateOnly(2021, 1, 1), new DateOnly(2026, 9, 25), TestContext.Current.CancellationToken));
        Assert.Contains("Week", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_WarnsAboutBarsOnClosedDays_AndLateStarts_AndRestatements()
    {
        decimal close = 96m;
        var gateway = new FakeGateway
        {
            Chart = (_, _, _) => new PriceHistory([AvanzaBar(new DateOnly(2026, 6, 19), close), AvanzaBar(new DateOnly(2026, 6, 22), 97m)], ChartResolution.Day, null),
        };
        using HistoryStore store = HistoryStore.Open(_dir.File("q.duckdb"));
        var importer = new HistoryImporter(store, _time, MarketCalendarLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "config")));
        var provider = new AvanzaChartImporter(gateway, _time, "v");

        ImportReport report = await importer.ImportAsync(provider, Eric, new DateOnly(2026, 1, 1), new DateOnly(2026, 9, 25), TestContext.Current.CancellationToken);
        Assert.Contains(report.Warnings, w => w.Contains("2026-06-19", StringComparison.Ordinal) && w.Contains("Midsummer Eve", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("later than the requested", StringComparison.Ordinal));

        close = 48m;
        _time.Advance(TimeSpan.FromHours(1));
        report = await importer.ImportAsync(provider, Eric, new DateOnly(2026, 1, 1), new DateOnly(2026, 9, 25), TestContext.Current.CancellationToken);
        Assert.Equal(new WriteCounts(0, 1, 1), report.Bars);
        Assert.Contains(report.Warnings, w => w.Contains("restatements", StringComparison.Ordinal));
    }

    [Fact]
    public void DataProjectDoesNotReferenceTheAvanzaGateway() =>
        Assert.DoesNotContain(
            typeof(HistoryStore).Assembly.GetReferencedAssemblies(),
            a => a.Name!.StartsWith("QuantAnalyst.Avanza", StringComparison.Ordinal));
}
