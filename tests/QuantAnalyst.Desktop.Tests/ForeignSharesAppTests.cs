using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.ViewModels;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>
/// ADR 0005 / plan 16 step 7: with a US share on the allowlist the app shows the session's decision per market and its
/// end (22:02), on the Overview and the Trading page, and the Instruments list says the share's currency and market.
/// </summary>
public sealed class ForeignSharesAppTests : IDisposable
{
    private readonly TempWorkspace _ws = new();
    private readonly FakeEnvironment _env = new();

    public ForeignSharesAppTests()
    {
        File.WriteAllText(Path.Combine(_ws.Workspace.ConfigDir, "universe.json"),
            """{ "format": "qa-universe/1", "instruments": [ { "orderbook_id": "5240", "ticker": "ERIC B", "name": "Ericsson B" }, { "orderbook_id": "4478", "ticker": "AAPL", "name": "Apple Inc" } ] }""");
        using HistoryStore store = HistoryStore.Open(_ws.Workspace.Store);
        var ticks = InstrumentRecord.CanonicalTickTable(new TickSizeTable([new TickSizeBand(0m, 99_999m, 0.01m)]));
        foreach ((string id, string ticker, string currency, string market) in new[] { ("5240", "ERIC B", "SEK", "XSTO"), ("4478", "AAPL", "USD", "XNAS") })
        {
            store.UpsertInstrument(
                new InstrumentRecord(new OrderbookId(id), null, ticker, ticker, currency, market, "STOCK", TradingModel.Continuous, 1m, ticks, new DateOnly(2026, 9, 25)),
                AvanzaChartImporter.AvanzaPriceChart.Name, "test", _ws.Time.GetUtcNow());
        }
    }

    public void Dispose() => _ws.Dispose();

    private ShellViewModel Shell()
    {
        var engine = new QaEngine(new ImmediateDispatcher());
        return new(_ws.Workspace, engine, _ws.Time, new EngineAccountSource(engine, _ws.Workspace), _env);
    }

    [Fact]
    public async Task TheOverview_ShowsEachMarketsDecision_AndTheEnd()
    {
        StatusViewModel status = Shell().Status;
        await status.RefreshAsync();
        Assert.Equal("Mon 28 Sep · decides at 09:10 (XSTO), 15:40 (XNYS) · ends 22:02", status.NextSessionText); // Saturday 12:00 now
        Assert.Equal("in 1 d 21 h", status.NextSessionIn); // to the first decision
    }

    [Fact]
    public async Task TheTradingPage_SaysWhenEachMarketDecides_AndWhenTheSessionEnds()
    {
        _ws.Time.SetUtcNow(new DateTimeOffset(2026, 9, 28, 6, 30, 0, TimeSpan.Zero)); // Monday 08:30 Stockholm
        ShellViewModel shell = Shell();
        await shell.Session.RefreshAsync();
        Assert.Equal(
            "Start today's session before 09:10: press Start. It decides per market (XSTO at 09:10, XNYS at 15:40), trades, and ends after the 22:00 close; keep its window open.",
            shell.Session.Schedule);
    }

    [Fact]
    public async Task TheInstrumentsList_SaysAUsSharesCurrencyAndMarket()
    {
        ShellViewModel shell = Shell();
        await shell.Instruments.RefreshAsync();
        Assert.Null(shell.Instruments.StoreError); // fails now and then in a full solution run (plan 23): this says why
        Assert.Equal("none yet", shell.Instruments.Rows.Single(r => r.Ticker == "ERIC B").History);
        Assert.Equal("USD · US (NYSE, Nasdaq), paper only · none yet", shell.Instruments.Rows.Single(r => r.Ticker == "AAPL").History);
    }
}
