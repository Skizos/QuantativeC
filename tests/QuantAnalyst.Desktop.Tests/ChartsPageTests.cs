using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Desktop.Core.Charts;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.ViewModels;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Observation;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Paper;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>
/// The charts page and window (docs/plans/12-charts-window.md) against a throw-away repository folder with a real
/// price store: History from the stored candles, Today from a session's quotes, and windows of their own.
/// </summary>
public sealed class ChartsPageTests : IDisposable
{
    private const string Nbsp = " ";
    private static readonly OrderbookId Eric = new("5240");
    private static readonly OrderbookId Volv = new("5269");

    // Monday 2026-09-28, Stockholm (CEST): open 09:00.
    private static readonly DateTimeOffset Open = new(2026, 9, 28, 7, 0, 0, TimeSpan.Zero);

    private readonly TempWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private static DateTimeOffset At(int minutes, int seconds = 0) => Open.AddMinutes(minutes).AddSeconds(seconds);

    private ShellViewModel Shell() => new(_ws.Workspace, new QaEngine(new ImmediateDispatcher()), _ws.Time, null, new FakeEnvironment());

    /// <summary>120 weekdays of ERIC B from Mon 6 Apr 2026: rising, every other day down, with volume.</summary>
    private List<DailyBar> StoreDays()
    {
        var days = new List<DailyBar>();
        for (DateOnly d = new(2026, 4, 6); days.Count < 120; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                decimal open = 90m + (days.Count * 0.1m);
                decimal close = days.Count % 2 == 0 ? open + 0.4m : open - 0.2m;
                days.Add(new DailyBar(d, open, open + 0.6m, open - 0.5m, close, 1_000_000 + days.Count));
            }
        }

        using HistoryStore store = HistoryStore.Open(_ws.Workspace.Store);
        store.RegisterSource(AvanzaChartImporter.AvanzaPriceChart);
        store.UpsertDailyBars(Eric, days, AvanzaChartImporter.AvanzaPriceChart, "test", _ws.Time.GetUtcNow());
        return days;
    }

    private static ISessionObserver StartSession(ShellViewModel shell)
    {
        ISessionObserver feed = shell.Session.Live;
        feed.Started(new SessionStarted(At(0), Open, Open.AddHours(8.5), At(10), "ma-cross(fast=20, slow=100)",
        [
            new ObservedInstrument(Eric, "ERIC B", "Ericsson B", 94.96m),
            new ObservedInstrument(Volv, "VOLV B", "Volvo B", null),
        ]));
        return feed;
    }

    [Fact]
    public async Task History_ShowsTheStoredDailyCandles_WithVolume_Averages_AndTheRange()
    {
        _ws.AllowEricB();
        List<DailyBar> days = StoreDays();
        ChartsViewModel charts = Shell().Charts;
        Assert.Equal("Charts", charts.Title);
        await charts.RefreshAsync();

        Assert.Equal(["ERIC B"], charts.Instruments.Select(i => i.Ticker));
        Assert.Equal(Eric, charts.Selected!.Id);
        Assert.True(charts.IsHistory);
        Assert.Equal(CandlePeriod.Daily, charts.Periods);
        Assert.Same(CandlePeriod.Day, charts.SelectedPeriod);
        Assert.Equal("3M", charts.SelectedRange.Label); // day candles wide enough to read

        CandleChartData chart = charts.Chart;
        Assert.Equal(120, chart.Candles.Count); // all of them: zoom out for the older ones
        DailyBar last = days[^1];
        Assert.Equal(days.Count(d => d.Date >= last.Date.AddMonths(-3)), chart.InitialCount);
        Assert.Equal(new Candle(CandlePeriod.Midnight(last.Date), (double)last.Open, (double)last.High, (double)last.Low, (double)last.Close, last.Volume), chart.Candles[^1]);
        Assert.True(chart.HasVolume);
        Assert.Equal(["20-day average", "50-day average"], chart.Overlays.Select(o => o.Name));
        Assert.Equal(120 - 19, chart.Overlays[0].Points.Count); // complete from the first full window, not cut at the range
        Assert.Empty(chart.Markers);

        Assert.Equal(("ERIC B", "Ericsson B"), (charts.Ticker, charts.Name));
        Assert.Equal(Presentation(last.Close) + " kr", charts.LastText);
        Assert.Equal("Close " + last.Date.ToString("ddd d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture), charts.AsOfText);
        Assert.EndsWith("· 3M", charts.ChangeText, StringComparison.Ordinal);
        Assert.Equal("up", charts.Direction);
        Assert.StartsWith("Latest day · ", charts.StatsTitle, StringComparison.Ordinal);
        Assert.Equal(["Open", "High", "Low", "Close", "Volume", "3M high", "3M low"], charts.Stats.Select(s => s.Label));
        Assert.Equal(Presentation(last.Open), charts.Stats[0].Value);
        Assert.Contains("Wheel to zoom", charts.Note, StringComparison.Ordinal);

        string dayKey = chart.Key;
        charts.SelectedPeriod = CandlePeriod.Week;
        Assert.Equal(CandleUnit.Week, charts.Chart.Period.Unit);
        Assert.Equal(24, charts.Chart.Candles.Count); // 120 weekdays from a Monday
        Assert.NotEqual(dayKey, charts.Chart.Key); // a new key: the chart starts again from the range
        Assert.StartsWith("Latest week · from ", charts.StatsTitle, StringComparison.Ordinal);

        charts.SelectedRange = ChartRange.All[^1];
        Assert.Null(charts.Chart.InitialCount);

        string weekKey = charts.Chart.Key;
        charts.ShowAverages = false;
        charts.ShowVolume = false;
        Assert.Empty(charts.Chart.Overlays);
        Assert.False(charts.Chart.HasVolume);
        Assert.Equal(weekKey, charts.Chart.Key); // switches keep the zoom

        // A saved ma-cross: its own two averages.
        PaperConfig.SaveStrategy(Path.Combine(_ws.Workspace.ConfigDir, PaperConfig.FileName),
            new PaperStrategy("ma-cross", new Dictionary<string, string> { ["fast"] = "5", ["slow"] = "30" }));
        charts.ShowAverages = true;
        Assert.Equal(["5-day average", "30-day average"], charts.Chart.Overlays.Select(o => o.Name));
        Assert.Contains("the saved ma-cross compares", charts.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_ShowsYourPaperTrades_FromTheDailyReports()
    {
        _ws.AllowEricB();
        List<DailyBar> days = StoreDays();
        DateOnly tradeDay = days[^3].Date;
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(CandlePeriod.Midnight(tradeDay).AddHours(10));
        var audit = new AuditLog(_ws.Workspace.AuditDir, clock);
        audit.Append("session-start", new { mode = "Paper" });
        audit.Append("sim-fill", new { ticker = "ERIC B", side = "Buy", volume = 5L, price = days[^3].Low + 0.1m, limit = days[^3].High, how = "touch" });

        ChartsViewModel charts = Shell().Charts;
        await charts.RefreshAsync();
        ChartMarker trade = Assert.Single(charts.Chart.Markers);
        Assert.Equal(MarkerKind.Buy, trade.Kind);
        Assert.StartsWith("Paper: bought 5 @ ", trade.Label, StringComparison.Ordinal);
        Assert.Equal(days.Count - 3, charts.Chart.IndexOf(trade.At)); // on its day's candle

        charts.ShowTrades = false;
        Assert.Empty(charts.Chart.Markers);
    }

    [Fact]
    public async Task WithoutInstrumentsOrPrices_ItSaysWhatToDo()
    {
        ChartsViewModel charts = Shell().Charts;
        await charts.RefreshAsync();
        Assert.Empty(charts.Instruments);
        Assert.True(charts.Chart.IsEmpty);
        Assert.Equal("No instruments yet: add one on the Instruments page.", charts.EmptyText);

        _ws.AllowEricB();
        await charts.RefreshAsync();
        Assert.True(charts.Chart.IsEmpty);
        Assert.StartsWith("No stored prices yet", charts.EmptyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Today_BuildsCandlesFromTheSessionsQuotes_WithFills_Limits_AndYesterdaysClose()
    {
        _ws.AllowEricB();
        ShellViewModel shell = Shell();
        ChartsViewModel charts = shell.Charts;
        await charts.RefreshAsync();
        charts.SelectedSource = ChartsViewModel.Today;
        Assert.False(charts.IsHistory);
        Assert.Equal(CandlePeriod.Intraday, charts.Periods);
        Assert.Same(CandlePeriod.FiveMinutes, charts.SelectedPeriod);
        Assert.True(charts.Chart.IsEmpty);
        Assert.StartsWith("Today's candles appear while a Paper session runs", charts.EmptyText, StringComparison.Ordinal);

        ISessionObserver feed = StartSession(shell);
        Assert.Equal(["ERIC B", "VOLV B"], charts.Instruments.Select(i => i.Ticker)); // VOLV B: traded by the session, not allowlisted
        Assert.Equal(Eric, charts.Selected!.Id);
        Assert.Equal("Waiting for the first quote …", charts.EmptyText);

        feed.Quote(new QuoteTick(At(0, 5), Eric, 94.98m, 95.02m, 95.00m));
        feed.Quote(new QuoteTick(At(2), Eric, 95.48m, 95.52m, 95.50m));
        feed.Quote(new QuoteTick(At(4, 30), Eric, 94.78m, 94.82m, 94.80m));
        feed.Quote(new QuoteTick(At(6), Eric, 95.18m, 95.22m, 95.20m));
        Assert.Equal(
            [new Candle(At(0), 95.00, 95.50, 94.80, 94.80, null), new Candle(At(5), 95.20, 95.20, 95.20, 95.20, null)],
            charts.Chart.Candles);
        Assert.False(charts.Chart.HasVolume);
        Assert.Equal(["Yesterday's close 94,96"], charts.Chart.Levels.Select(l => l.Label));
        Assert.Equal("95,20 kr", charts.LastText);
        Assert.Equal($"▲ +0,25{Nbsp}% · today", charts.ChangeText);
        Assert.Equal("Live · latest candle 09:05", charts.AsOfText);
        Assert.Equal(
            [("Open", "95,00"), ("High", "95,50"), ("Low", "94,80"), ("Last", "95,20"), ("Yesterday's close", "94,96")],
            charts.Stats.Select(s => (s.Label, s.Value)));

        var order = Guid.NewGuid();
        feed.Order(new OrderTick(At(10, 2), order, Eric, "ERIC B", OrderSide.Buy, 5, 95.10m, OmsState.Working, 0, null, At(10, 1)));
        Assert.Contains("Buy 5 @ 95,10", charts.Chart.Levels.Select(l => l.Label));
        feed.Order(new OrderTick(At(10, 40), order, Eric, "ERIC B", OrderSide.Buy, 5, 95.10m, OmsState.Filled, 5, 95.05m, At(10, 1)));
        Assert.DoesNotContain("Buy 5 @ 95,10", charts.Chart.Levels.Select(l => l.Label)); // filled: no longer working
        ChartMarker fill = Assert.Single(charts.Chart.Markers);
        Assert.Equal((MarkerKind.Buy, 95.05), (fill.Kind, fill.Value));

        charts.SelectedPeriod = CandlePeriod.OneMinute;
        Assert.Equal([At(0), At(2), At(4), At(6)], charts.Chart.Candles.Select(c => c.At));
        charts.Selected = charts.Instruments[1];
        Assert.Equal("Waiting for the first quote …", charts.EmptyText);
    }

    [Fact]
    public async Task OpenInNewWindow_GivesAnIndependentPage_ThatStartsLikeThisOne_AndLetsGoWhenClosed()
    {
        _ws.AllowEricB();
        StoreDays();
        ShellViewModel shell = Shell();
        ChartsViewModel charts = shell.Charts;
        await charts.RefreshAsync();
        charts.SelectedPeriod = CandlePeriod.Week;
        charts.ShowTrades = false;

        ChartsViewModel? opened = null;
        shell.ChartsWindowRequested += c => opened = c;
        Assert.True(charts.OpenWindowCommand.CanExecute(null));
        charts.OpenWindowCommand.Execute(null);
        Assert.NotNull(opened);
        Assert.NotSame(charts, opened);
        await opened!.RefreshAsync(); // what its window does when it opens

        Assert.Equal(charts.Selected, opened.Selected);
        Assert.Same(CandlePeriod.Week, opened.SelectedPeriod);
        Assert.False(opened.ShowTrades);
        Assert.Equal(charts.Chart.Candles, opened.Chart.Candles);

        opened.SelectedPeriod = CandlePeriod.Month; // each window on its own
        Assert.Same(CandlePeriod.Week, charts.SelectedPeriod);

        // Both follow the session; a closed window no longer does.
        charts.SelectedSource = opened.SelectedSource = ChartsViewModel.Today;
        opened.SelectedPeriod = charts.SelectedPeriod = CandlePeriod.OneMinute;
        ISessionObserver feed = StartSession(shell);
        feed.Quote(new QuoteTick(At(0, 5), Eric, 94.98m, 95.02m, 95.00m));
        Assert.Single(opened.Chart.Candles);
        opened.Dispose();
        feed.Quote(new QuoteTick(At(1, 5), Eric, 95.08m, 95.12m, 95.10m));
        Assert.Equal(2, charts.Chart.Candles.Count);
        Assert.Single(opened.Chart.Candles);

        Assert.False(new ChartsViewModel(_ws.Workspace, new QaEngine(new ImmediateDispatcher()), _ws.Time, shell.Session.Live, new HistoryCandles())
            .OpenWindowCommand.CanExecute(null)); // no window to open without a shell
    }

    private static string Presentation(decimal price) => QuantAnalyst.Desktop.Core.Presentation.Fmt.Price(price);
}
