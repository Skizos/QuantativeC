using QuantAnalyst.Core;
using QuantAnalyst.Desktop.Core.Charts;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.ViewModels;
using QuantAnalyst.Trading.Observation;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Paper;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>The Trading page's live model: what a running session reports becomes KPIs, charts, tiles and orders.</summary>
public sealed class LiveSessionTests
{
    private const string Nbsp = " ";

    // Monday 2026-09-28, Stockholm (CEST): open 09:00, decision 09:10, close 17:30.
    private static readonly DateTimeOffset Open = new(2026, 9, 28, 7, 0, 0, TimeSpan.Zero);
    private static readonly OrderbookId Eric = new("5240");
    private static readonly OrderbookId Volv = new("5269");

    private static DateTimeOffset At(int minutes, int seconds = 0) => Open.AddMinutes(minutes).AddSeconds(seconds);

    private static LiveSession Started()
    {
        var live = new LiveSession(new ImmediateDispatcher());
        ((ISessionObserver)live).Started(new SessionStarted(At(0), Open, Open.AddHours(8.5), At(10), "ma-cross(fast=20, slow=100)",
        [
            new ObservedInstrument(Eric, "ERIC B", "Ericsson B", 70.00m),
            new ObservedInstrument(Volv, "VOLV B", "Volvo B", null),
        ]));
        return live;
    }

    [Fact]
    public void TheStart_SetsUpATilePerInstrument_AndTheDecisionTime()
    {
        LiveSession live = Started();
        Assert.True(live.HasData);
        Assert.Equal(["ERIC B", "VOLV B"], live.Tiles.Select(t => t.Ticker));
        Assert.Same(live.Tiles[0], live.SelectedTile);
        Assert.Equal("Decides at 09:10", live.DecisionText);
        Assert.Equal("ma-cross(fast=20, slow=100)", live.Strategy);
        Assert.Equal(At(10), live.DecisionUtc);
    }

    [Fact]
    public void Quotes_MoveTheTiles_AgainstYesterdaysClose_OrTheFirstQuote()
    {
        LiveSession live = Started();
        ISessionObserver feed = live;
        feed.Quote(new QuoteTick(At(10), Eric, 69.98m, 70.02m, 70.00m));
        feed.Quote(new QuoteTick(At(10, 5), Eric, 70.68m, 70.72m, 70.70m)); // within 10 s: moves the last point
        feed.Quote(new QuoteTick(At(10, 20), Eric, 71.38m, 71.42m, 71.40m));
        feed.Quote(new QuoteTick(At(10), Volv, 250m, 251m, null)); // no trade yet: the mid
        feed.Quote(new QuoteTick(At(11), Volv, 247.5m, 248.5m, null));

        InstrumentTile eric = live.Tiles[0];
        Assert.Equal([70.70, 71.40], eric.Prices.Select(p => p.Value));
        Assert.Equal("71,40", eric.LastText);
        Assert.Equal($"▲ +2,00{Nbsp}%", eric.ChangeText); // against Friday's 70,00
        Assert.Equal("up", eric.Direction);
        Assert.Equal(TimeAxis.Intraday, eric.Spark.Axis);
        Assert.Equal((Open, Open.AddHours(8.5)), eric.Spark.Window);

        InstrumentTile volv = live.Tiles[1];
        Assert.Equal("248,00", volv.LastText);
        Assert.Equal("down", volv.Direction); // against its first quote, 250,50
        Assert.Equal(2, live.PriceChart.Main.Count); // ERIC B is selected
        Assert.Equal(70.0, live.PriceChart.Baseline);
    }

    [Fact]
    public void TheAccount_BecomesTheValueChart_AndTheKpis()
    {
        LiveSession live = Started();
        ISessionObserver feed = live;
        feed.Account(new AccountTick(At(0), 5000m, 5000m, 5000m, 0m, []));
        feed.Account(new AccountTick(At(12), 5010m, 4509m, 5000m, 0m, [new ObservedPosition(Eric, "ERIC B", 7, 496.02m, 501m)]));

        Assert.Equal($"5{Nbsp}010,00 kr", live.ValueText);
        Assert.Equal($"+10,00 kr · +0,20{Nbsp}%", live.DayChangeText);
        Assert.Equal("up", live.DayDirection);
        Assert.Equal($"4{Nbsp}509,00 kr", live.CashText);
        Assert.Equal($"10,0{Nbsp}%", live.InvestedText);
        Assert.Equal("0,00 kr", live.FeesText);
        Assert.Equal([5000.0, 5010.0], live.ValueChart.Main.Select(p => p.Value));
        Assert.Equal(5000, live.ValueChart.Baseline);
        Assert.Equal("7 shares · 501,00 kr", live.Tiles[0].PositionText);
        Assert.Equal("No holding", live.Tiles[1].PositionText);
    }

    [Fact]
    public void Orders_FillTheList_AndTheChart_WithLimitsWhileWorking_AndDotsWhenFilled()
    {
        LiveSession live = Started();
        ISessionObserver feed = live;
        feed.Quote(new QuoteTick(At(10), Eric, 70.84m, 70.86m, 70.86m));
        var id = Guid.NewGuid();
        OrderTick Tick(int second, OmsState state, long filled, decimal? avg) =>
            new(At(10, second), id, Eric, "ERIC B", OrderSide.Buy, 7, 70.90m, state, filled, avg, At(10));

        feed.Order(Tick(0, OmsState.Sent, 0, null));
        OrderRow row = Assert.Single(live.Orders);
        Assert.Equal(("09:10:00", "Buy", "ERIC B", "7", "70,90", "Sending"), (row.Time, row.Side, row.Ticker, row.Volume, row.Limit, row.StateText));

        feed.Order(Tick(1, OmsState.Working, 0, null));
        Assert.Equal("Working", row.State);
        ChartLevel limit = Assert.Single(live.PriceChart.Levels);
        Assert.Equal((70.90, "Buy 7 @ 70,90"), (limit.Value, limit.Label));
        Assert.Equal("1 order · 0 with fills", live.OrdersText);

        feed.Order(Tick(30, OmsState.Filled, 7, 70.86m));
        Assert.Equal(("Filled", "7 / 7", "70,86"), (row.State, row.Filled, row.Average));
        ChartMarker fill = Assert.Single(live.PriceChart.Markers);
        Assert.Equal((MarkerKind.Buy, 70.86, "Bought 7 @ 70,86"), (fill.Kind, fill.Value, fill.Label));
        Assert.Empty(live.PriceChart.Levels); // no longer working
        Assert.Equal("1 order · 1 with fills", live.OrdersText);
    }

    [Fact]
    public void TheDecision_ShowsItsNotes_AndAResetForgetsTheDay()
    {
        LiveSession live = Started();
        ((ISessionObserver)live).Decision(new DecisionTick(At(10), 1, ["ERIC B: Buy 7 @ ~70.9", "VOLV B: hold (no target today)"]));
        Assert.Equal("09:10:00 · 1 order", live.DecisionText);
        Assert.Equal(["ERIC B: Buy 7 @ ~70.9", "VOLV B: hold (no target today)"], live.DecisionNotes);

        live.Reset();
        Assert.False(live.HasData);
        Assert.Empty(live.Tiles);
        Assert.Empty(live.Orders);
        Assert.True(live.ValueChart.IsEmpty);
        Assert.Null(live.SelectedTile);
    }

    [Fact]
    public async Task StartingASession_ReportsToTheTradingPage()
    {
        using var ws = new TempWorkspace();
        var runner = new ScriptedRunner().Answer(0, ["Session over: 0 order(s)"]);
        var shell = new ShellViewModel(ws.Workspace, new QaEngine(new ImmediateDispatcher(), runner.Run, null), ws.Time);

        await shell.Session.StartCommand.ExecuteAsync();
        Assert.Same(shell.Session.Live, runner.LastServices!.SessionObserver);

        await shell.Session.RefreshAsync();
        Assert.Equal(("idle", "Not running"), (shell.Session.Phase, shell.Session.PhaseText));
    }
}

/// <summary>Plan 23: the Trading page's Buy or sell by hand card places the same requests as 'qa paper manual'.</summary>
public sealed class ManualOrderCardTests
{
    [Fact]
    public async Task TheCard_PlacesARequest_ListsIt_AndOnlyTakesACompleteOrder()
    {
        using var ws = new TempWorkspace();
        ws.AllowEricB();
        var shell = new ShellViewModel(ws.Workspace, new QaEngine(new ImmediateDispatcher(), new ScriptedRunner().Run, null), ws.Time);
        SessionViewModel page = shell.Session;
        await shell.Refreshing;
        await page.RefreshAsync();

        Assert.Equal(["ERIC B"], page.ManualTickers);
        Assert.Equal("ERIC B", page.ManualTicker);
        Assert.False(page.PlaceManualCommand.CanExecute(null)); // no quantity yet
        page.ManualQuantity = "abc";
        Assert.False(page.PlaceManualCommand.CanExecute(null));
        page.ManualQuantity = "7";
        page.ManualLimit = "-1";
        Assert.False(page.PlaceManualCommand.CanExecute(null));
        page.ManualLimit = "70,5"; // a Swedish decimal comma is accepted
        page.ManualSide = "Sell";
        Assert.True(page.PlaceManualCommand.CanExecute(null));

        await page.PlaceManualCommand.ExecuteAsync();

        Assert.Contains("Sell 7 ERIC B (limit 70.5)", page.Message, StringComparison.Ordinal);
        Assert.Contains("No Paper session is running: the next one sends it", page.Message, StringComparison.Ordinal);
        Assert.Equal((string.Empty, string.Empty), (page.ManualQuantity, page.ManualLimit));
        Assert.Contains(page.ManualLines, l => l == "Waiting (1):");
        Assert.Contains(page.ManualLines, l => l.Contains("Sell 7 ERIC B (limit 70.5)", StringComparison.Ordinal));
        ManualOrderRequest request = Assert.Single(new ManualOrderInbox(Path.Combine(ws.Workspace.StateDir, "paper"), ws.Time).Pending());
        Assert.Equal(("app", ManualAction.Sell, 70.5m), (request.Source, request.Action, request.Limit!.Value));
        Assert.Empty(page.ManualShares);
    }
}
