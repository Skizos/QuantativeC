using System.Collections.ObjectModel;
using System.Globalization;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Mvvm;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>One allowlisted instrument and how far its stored daily history reaches.</summary>
public sealed record InstrumentRow(string Ticker, string Name, string OrderbookId, string History);

/// <summary>
/// The allowlist (risk check R2). <b>Add</b> runs what you would type: <c>qa history import</c> (one read-only
/// Avanza login) and then <c>qa universe add</c>. <b>Remove</b> runs <c>qa universe remove</c>.
/// </summary>
public sealed class InstrumentsViewModel : PageViewModel
{
    private readonly Workspace _workspace;
    private readonly Func<string> _login;
    private string _newTicker = string.Empty;

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

    public override async Task RefreshAsync()
    {
        try
        {
            IReadOnlyList<InstrumentRow> rows = await Task.Run(Load);
            Rows.Clear();
            foreach (InstrumentRow row in rows)
            {
                Rows.Add(row);
            }

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
