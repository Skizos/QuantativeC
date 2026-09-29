using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Fx;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// ADR 0005 / plan 16 step 4: a US share's bars reach the backtest (and the Paper decision) in SEK, each day at the
/// latest fixing on or before it; the tick table is scaled at the last one; a hole in the FX history is refused.
/// </summary>
public sealed class ForeignPanelTests : IDisposable
{
    private static readonly DateTimeOffset Known = new(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-foreign-panel", Guid.NewGuid().ToString("N"));

    public ForeignPanelTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private HistoryStore Store(IReadOnlyList<FxRate> rates)
    {
        HistoryStore store = HistoryStore.Open(Path.Combine(_root, "q.duckdb"));
        store.RegisterSource(AvanzaChartImporter.AvanzaPriceChart);
        store.RegisterSource(RiksbankFxSource.Riksbank);
        var ticks = new TickSizeTable([new TickSizeBand(0m, 0.9999m, 0.0001m), new TickSizeBand(1m, 100_000m, 0.01m)]);
        var aapl = new InstrumentTradingParams(new OrderbookId("4478"), "Apple Inc", "AAPL", "US0378331005", "USD", "XNAS", "US", "STOCK", null, ticks, 1, 1, null, null, Known);
        store.UpsertInstrument(InstrumentRecord.FromTradingParams(aapl), "test", "1", Known);
        DailyBar[] bars =
        [
            new(new DateOnly(2026, 9, 21), 250m, 252m, 249m, 251m, 1000), // Monday
            new(new DateOnly(2026, 9, 22), 251m, 253m, 250m, 252m, 1000),
            new(new DateOnly(2026, 9, 25), 252m, 254m, 251m, 253m, 1000), // Friday; no fixing that day
        ];
        store.UpsertDailyBars(new OrderbookId("4478"), bars, AvanzaChartImporter.AvanzaPriceChart, "test", Known);
        store.UpsertFxRates("USD", rates, RiksbankFxSource.Riksbank, "test", Known);
        return store;
    }

    [Fact]
    public void EachBar_IsInSekAtTheLatestFixingOnOrBeforeIt_AndTheTickTableAtTheLast()
    {
        using HistoryStore store = Store(
        [
            new(new DateOnly(2026, 9, 18), 9.0m), // Friday before: the Monday bar has none of its own
            new(new DateOnly(2026, 9, 22), 9.5m),
            new(new DateOnly(2026, 9, 24), 10.0m),
        ]);

        MarketPanel panel = BacktestCommands.LoadStorePanel(store, ["AAPL"], null, null);

        PanelInstrument i = Assert.Single(panel.Instruments);
        Assert.True(i.ForeignCurrency);
        Assert.Equal(("USD", 10.0m), (i.Currency, i.LastSekPerUnit!.Value));
        Assert.Equal(251 * 9.0, panel.Bar(0, 0).Close, 9);   // 21 Sep at Friday's 9.00
        Assert.Equal(252 * 9.5, panel.Bar(1, 0).Close, 9);   // 22 Sep at its own 9.50
        Assert.Equal(253 * 10.0, panel.Bar(2, 0).Close, 9);  // 25 Sep at the 24th's 10.00
        Assert.Equal(254 * 10.0, panel.Bar(2, 0).High, 9);
        Assert.Equal(0.1m, i.TickSizes.TickAt(2530m)); // 1 cent at 10 SEK
    }

    [Fact]
    public void NoFixingForTheFirstBar_OrAHoleOfMoreThanAWeek_IsRefused()
    {
        using (HistoryStore store = Store([new(new DateOnly(2026, 9, 22), 9.5m)]))
        {
            var ex = Assert.Throws<ArgumentException>(() => BacktestCommands.LoadStorePanel(store, ["AAPL"], null, null));
            Assert.Contains("no USD/SEK fixing is stored for its first bar 2026-09-21", ex.Message, StringComparison.Ordinal);
            Assert.Contains("qa fx import USD --from 2026-09-07", ex.Message, StringComparison.Ordinal);
        }

        File.Delete(Path.Combine(_root, "q.duckdb"));
        using (HistoryStore store = Store([new(new DateOnly(2026, 9, 11), 9.0m), new(new DateOnly(2026, 9, 14), 9.1m)]))
        {
            var ex = Assert.Throws<ArgumentException>(() => BacktestCommands.LoadStorePanel(store, ["AAPL"], null, null));
            Assert.Contains("the USD/SEK fixing for 2026-09-22 is from 2026-09-14, more than 7 days old", ex.Message, StringComparison.Ordinal);
        }
    }
}
