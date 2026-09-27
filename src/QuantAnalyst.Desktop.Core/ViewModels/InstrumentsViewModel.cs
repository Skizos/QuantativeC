using System.Collections.ObjectModel;
using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Desktop.Core.Charts;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Mvvm;
using QuantAnalyst.Desktop.Core.Presentation;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>One allowlisted instrument and how far its stored daily history reaches.</summary>
public sealed record InstrumentRow(string Ticker, string Name, string OrderbookId, string History);

/// <summary>
/// The allowlist (risk check R2). <b>Add</b> runs what you would type: <c>qa history import</c> (one read-only
/// Avanza login) and then <c>qa universe add</c>. <b>Remove</b> runs <c>qa universe remove</c>. The selected name's
/// stored daily history is charted with ranges (1M … All) and, when the saved strategy is ma-cross, its two moving
/// averages (docs/plans/11-app-redesign.md).
/// </summary>
public sealed class InstrumentsViewModel : PageViewModel
{
    private readonly Workspace _workspace;
    private readonly Func<string> _login;
    private string _newTicker = string.Empty;
    private InstrumentRow? _selectedRow;
    private ChartRange _range = ChartRange.OneYear;
    private IReadOnlyList<ChartPoint> _history = [];
    private string? _loadedFor;
    private ChartData _chart = ChartData.Empty;
    private string _chartTitle = string.Empty;
    private string _chartName = string.Empty;
    private string _lastClose = string.Empty;
    private string _lastCloseDate = string.Empty;
    private string _rangeChange = string.Empty;
    private string _rangeDirection = "flat";
    private string _chartNote = string.Empty;

    public InstrumentsViewModel(Workspace workspace, QaEngine engine, Func<string> login)
        : base(PageKind.Instruments, "Instruments", "The Swedish shares the strategy may trade (1–5 names).", engine)
    {
        _workspace = workspace;
        _login = login;
        AddCommand = new AsyncCommand(AddAsync, () => !IsBusy && NewTicker.Trim().Length > 0, ex => Say(ex.Message, isError: true));
        RemoveCommand = new AsyncCommand(p => RemoveAsync(p as InstrumentRow), p => !IsBusy && p is InstrumentRow, ex => Say(ex.Message, isError: true));
    }

    public ObservableCollection<InstrumentRow> Rows { get; } = [];

    /// <summary>Gets or sets the ticker to add, e.g. "ERIC-B" or "ERIC B".</summary>
    public string NewTicker
    {
        get => _newTicker;
        set
        {
            if (Set(ref _newTicker, value ?? string.Empty))
            {
                AddCommand.Refresh();
            }
        }
    }

    public AsyncCommand AddCommand { get; }

    public AsyncCommand RemoveCommand { get; }

    /// <summary>Gets or sets the instrument whose chart is shown.</summary>
    public InstrumentRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value))
            {
                _ = LoadChartAsync();
            }
        }
    }

    public IReadOnlyList<ChartRange> Ranges { get; } = ChartRange.All;

    public ChartRange SelectedRange
    {
        get => _range;
        set
        {
            if (value is not null && Set(ref _range, value))
            {
                BuildChart();
            }
        }
    }

    /// <summary>Gets the selected name's daily closes over the range, with the strategy's averages.</summary>
    public ChartData Chart
    {
        get => _chart;
        private set => Set(ref _chart, value);
    }

    /// <summary>Gets the ticker over the chart, e.g. "ERIC B".</summary>
    public string ChartTitle
    {
        get => _chartTitle;
        private set => Set(ref _chartTitle, value);
    }

    /// <summary>Gets the instrument's name under the ticker, e.g. "Ericsson B".</summary>
    public string ChartName
    {
        get => _chartName;
        private set => Set(ref _chartName, value);
    }

    /// <summary>Gets the last stored close, e.g. "70,85 kr".</summary>
    public string LastClose
    {
        get => _lastClose;
        private set => Set(ref _lastClose, value);
    }

    /// <summary>Gets which day that close is from, e.g. "Close Fri 25 Sep 2026".</summary>
    public string LastCloseDate
    {
        get => _lastCloseDate;
        private set => Set(ref _lastCloseDate, value);
    }

    /// <summary>Gets the change over the range with its arrow, e.g. "▲ +4,20 %".</summary>
    public string RangeChange
    {
        get => _rangeChange;
        private set => Set(ref _rangeChange, value);
    }

    /// <summary>Gets "up", "down" or "flat" for the range's change (its colour).</summary>
    public string RangeDirection
    {
        get => _rangeDirection;
        private set => Set(ref _rangeDirection, value);
    }

    /// <summary>Gets what the extra lines are, or why there is no chart.</summary>
    public string ChartNote
    {
        get => _chartNote;
        private set => Set(ref _chartNote, value);
    }

    public override async Task RefreshAsync()
    {
        try
        {
            IReadOnlyList<InstrumentRow> rows = await Task.Run(Load);
            string? selected = _selectedRow?.OrderbookId;
            Rows.Clear();
            foreach (InstrumentRow row in rows)
            {
                Rows.Add(row);
            }

            SelectedRow = Rows.FirstOrDefault(r => r.OrderbookId == selected) ?? Rows.FirstOrDefault();

            if (!MessageIsError && Message.Length == 0 && Rows.Count == 0)
            {
                Say("No instruments yet: every order would be rejected. Type a ticker, e.g. ERIC-B, and press Add.");
            }
        }
        catch (Exception ex) when (ex is TradingConfigException or IOException or ArgumentException)
        {
            Say(ex.Message, isError: true);
        }
    }

    protected override void OnBusyChanged()
    {
        AddCommand.Refresh();
        RemoveCommand.Refresh();
    }

    /// <summary>Loads the selected name's closes once (the store is read only while no command runs), then draws.</summary>
    internal async Task LoadChartAsync()
    {
        InstrumentRow? row = _selectedRow;
        if (row is null)
        {
            _history = [];
            _loadedFor = null;
            BuildChart();
            return;
        }

        string key = row.OrderbookId + "|" + row.History;
        if (key == _loadedFor)
        {
            BuildChart();
            return;
        }

        if (IsBusy)
        {
            ChartNote = "The chart loads when nothing else runs (the price store is in use).";
            return;
        }

        try
        {
            _history = await Task.Run(() => ChartSources.DailyCloses(_workspace.Store, new OrderbookId(row.OrderbookId)));
            _loadedFor = key;
        }
        catch (Exception ex) when (ex is HistoryStoreException or IOException || Cli.Commands.DataCommands.IsStoreFailure(ex))
        {
            _history = [];
            _loadedFor = null;
            ChartNote = "The price store is busy; the chart comes back on the next refresh.";
        }

        BuildChart();
    }

    private void BuildChart()
    {
        InstrumentRow? row = _selectedRow;
        ChartTitle = row?.Ticker ?? string.Empty;
        ChartName = row?.Name ?? string.Empty;
        IReadOnlyList<ChartPoint> shown = _range.Take(_history);
        if (row is null || shown.Count == 0)
        {
            Chart = ChartData.Empty;
            LastClose = LastCloseDate = RangeChange = string.Empty;
            RangeDirection = "flat";
            if (row is not null && _loadedFor is not null)
            {
                ChartNote = "No stored prices yet: they are imported when the name is added, and brought up to date by each session.";
            }

            return;
        }

        var overlays = new List<ChartOverlay>();
        if (ChartSources.SavedAverages(_workspace) is { } ma)
        {
            foreach (int length in new[] { ma.Fast, ma.Slow })
            {
                overlays.Add(new ChartOverlay(
                    string.Create(CultureInfo.InvariantCulture, $"{length}-day average"),
                    [.. Indicators.Sma(_history, length).Where(p => p.At >= shown[0].At)]));
            }

            ChartNote = string.Create(CultureInfo.InvariantCulture, $"Thin lines: the {ma.Fast}- and {ma.Slow}-day averages the saved ma-cross compares. It holds the name while the {ma.Fast}-day line is above the {ma.Slow}-day line.");
        }
        else
        {
            ChartNote = "Daily closes as Avanza reports them (adjusted, not point-in-time).";
        }

        double first = shown[0].Value;
        double last = shown[^1].Value;
        decimal change = first != 0 ? (decimal)((last - first) / first) : 0m;
        Chart = new ChartData
        {
            Main = shown,
            Overlays = overlays,
            Baseline = first,
            Axis = TimeAxis.Daily,
            FormatValue = v => Fmt.Price((decimal)v),
        };
        LastClose = Fmt.Price((decimal)last) + " kr";
        LastCloseDate = "Close " + MarketTime.ToStockholm(shown[^1].At).ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
        RangeChange = Fmt.Arrow(change) + " · " + _range.Label;
        RangeDirection = Tone.Direction(change);
    }

    private async Task AddAsync()
    {
        string ticker = NewTicker.Trim();
        Say($"Importing the history of {ticker} (Avanza login) …");
        CommandResult import = await Engine.RunAsync($"Import {ticker}", CommandLines.HistoryImport(_workspace, ticker, _login()));
        if (!import.Succeeded)
        {
            Say($"Could not import {ticker}: {Why(import)}", isError: true);
            return;
        }

        CommandResult add = await Engine.RunAsync($"Allow {ticker}", CommandLines.UniverseAdd(_workspace, ticker));
        if (!add.Succeeded)
        {
            Say($"Could not add {ticker}: {Why(add)}", isError: true);
            return;
        }

        NewTicker = string.Empty;
        Say(add.Lines.FirstOrDefault(l => l.Text.StartsWith("added", StringComparison.Ordinal))?.Text ?? $"{ticker} added.");
        await RefreshAsync();
    }

    private async Task RemoveAsync(InstrumentRow? row)
    {
        if (row is null)
        {
            return;
        }

        CommandResult result = await Engine.RunAsync($"Remove {row.Ticker}", CommandLines.UniverseRemove(_workspace, row.Ticker));
        Say(result.Succeeded ? $"{row.Ticker} removed from the allowlist." : $"Could not remove {row.Ticker}: {Why(result)}", !result.Succeeded);
        await RefreshAsync();
    }

    private IReadOnlyList<InstrumentRow> Load()
    {
        Universe universe = Universe.Load(Path.Combine(_workspace.ConfigDir, Universe.FileName));
        Dictionary<string, string> history = HistoryOf(universe);
        return [.. universe.Entries.Select(e => new InstrumentRow(e.Ticker, e.Name, e.OrderbookId.Value, history.GetValueOrDefault(e.OrderbookId.Value, "none yet")))];
    }

    /// <summary>"251 bars to 2026-09-25" per orderbook id. The store may be busy (a session updating it): then it says so.</summary>
    private Dictionary<string, string> HistoryOf(Universe universe)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(_workspace.Store) || universe.Entries.Count == 0 || IsBusy)
        {
            string text = IsBusy ? "(shown when idle)" : "none yet";
            return universe.Entries.ToDictionary(e => e.OrderbookId.Value, _ => text, StringComparer.Ordinal);
        }

        try
        {
            using HistoryStore store = HistoryStore.Open(_workspace.Store);
            string source = AvanzaChartImporter.AvanzaPriceChart.Name;
            bool hasSource = store.GetSource(source) is not null;
            foreach (UniverseEntry e in universe.Entries)
            {
                IReadOnlyList<StoredBar> bars = hasSource ? store.GetDailyBars(e.OrderbookId, source) : [];
                result[e.OrderbookId.Value] = bars.Count == 0
                    ? "none yet"
                    : string.Create(CultureInfo.InvariantCulture, $"{bars.Count} bars to {bars[^1].Bar.Date:yyyy-MM-dd}");
            }
        }
        catch (Exception ex) when (ex is HistoryStoreException or IOException || Cli.Commands.DataCommands.IsStoreFailure(ex))
        {
            foreach (UniverseEntry e in universe.Entries)
            {
                result[e.OrderbookId.Value] = "(store busy)";
            }
        }

        return result;
    }
}
