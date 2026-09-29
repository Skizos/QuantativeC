using System.Collections.ObjectModel;
using System.Globalization;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Desktop.Core.Charts;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Mvvm;
using QuantAnalyst.Desktop.Core.Presentation;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>One allowlisted instrument and how far its stored daily history reaches.</summary>
public sealed record InstrumentRow(string Ticker, string Name, string OrderbookId, string History);

/// <summary>
/// One share Avanza found: who it is ("ERIC B", "Ericsson B"), where it trades ("SE · Stockholmsbörsen"), its price
/// (a US or Canadian one also in SEK, "≈ 1 715 kr", at the latest stored fixing) and change today, its sector, and
/// whether it can be added or why not ("On your list", "Trades in EUR …").
/// </summary>
public sealed record SearchResultRow(
    string OrderbookId, string Ticker, string Name, string Market, string Price, string PriceSek, string Change, string Direction, string Sector,
    bool CanAdd, bool IsOnList, string Why);

/// <summary>
/// The allowlist (risk check R2). <b>Find a share</b> searches Avanza's market as you type, without a login
/// (docs/plans/15-share-search.md); each hit says whether it can be added or why not, and <b>Add</b> imports its daily
/// prices and allows it (one read-only login for every add until <b>Done</b>). <b>Remove</b> runs <c>qa universe remove</c> (or, while the search
/// is open, the same change in turn with its adds). The selected name's stored daily history is charted with ranges
/// (1M … All) and, when the saved strategy is ma-cross, its two moving averages (docs/plans/11-app-redesign.md).
/// </summary>
public sealed class InstrumentsViewModel : PageViewModel
{
    /// <summary>How many characters a search needs.</summary>
    public const int MinQueryLength = 2;

    /// <summary>How long typing pauses before the search runs (Enter searches at once).</summary>
    public static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(400);

    private readonly Workspace _workspace;
    private readonly Func<string> _login;
    private readonly IMarketSearch _search;
    private readonly TimeProvider _time;
    private string _searchText = string.Empty;
    private string _searchStatus = string.Empty;
    private bool _searchStatusIsError;
    private bool _isSearching;
    private string _searchHint = string.Empty;
    private IReadOnlyList<InstrumentSearchHit> _hits = [];
    private CancellationTokenSource? _typing;
    private int _searchSeq;
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

    public InstrumentsViewModel(Workspace workspace, QaEngine engine, Func<string> login, IMarketSearch search, TimeProvider time)
        : base(PageKind.Instruments, "Instruments", "The Swedish shares the strategy may trade (1–5 names).", engine)
    {
        _workspace = workspace;
        _login = login;
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        SearchCommand = new AsyncCommand(SearchNowAsync, () => SearchText.Trim().Length >= MinQueryLength && CanSearch, ex => SayInSearch(ex.Message, isError: true));
        AddResultCommand = new AsyncCommand(p => AddResultAsync(p as SearchResultRow), p => p is SearchResultRow { CanAdd: true } && CanUseLogin, ex => SayInSearch(ex.Message, isError: true));
        DoneCommand = new RelayCommand(CloseSearch, () => _search.IsOpen || Results.Count > 0 || SearchText.Length > 0);
        RemoveCommand = new AsyncCommand(p => RemoveAsync(p as InstrumentRow), p => p is InstrumentRow && CanUseLogin && !AddResultCommand.IsRunning, ex => Say(ex.Message, isError: true));
        _searchHint = Hint(OrderLimit());
    }

    public ObservableCollection<InstrumentRow> Rows { get; } = [];

    /// <summary>Gets or sets what to look for at Avanza: a name or a ticker, e.g. "ericsson" or "ERIC B".</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty))
            {
                SearchCommand.Refresh();
                DoneCommand.Refresh();
                _ = SearchAfterPauseAsync();
            }
        }
    }

    /// <summary>Gets the shares Avanza found for <see cref="SearchText"/>, each with whether it can be added.</summary>
    public ObservableCollection<SearchResultRow> Results { get; } = [];

    /// <summary>Gets what the search is doing: "Searching …", "2 shares", "No shares match …", or what went wrong.</summary>
    public string SearchStatus
    {
        get => _searchStatus;
        private set => Set(ref _searchStatus, value);
    }

    public bool SearchStatusIsError
    {
        get => _searchStatusIsError;
        private set => Set(ref _searchStatusIsError, value);
    }

    /// <summary>Gets a value indicating whether a search is on its way to Avanza.</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set => Set(ref _isSearching, value);
    }

    /// <summary>Gets a value indicating whether the search's Avanza login is open (Done lets it go).</summary>
    public bool IsSearchOpen => _search.IsOpen;

    /// <summary>Gets the line under "Find a share": what Add does, and the most one order may cost.</summary>
    public string SearchHint
    {
        get => _searchHint;
        private set => Set(ref _searchHint, value);
    }

    /// <summary>Gets the search box's Enter: search now instead of after the typing pause.</summary>
    public AsyncCommand SearchCommand { get; }

    /// <summary>Gets a hit's <b>Add</b>: import its daily prices and put it on the allowlist.</summary>
    public AsyncCommand AddResultCommand { get; }

    /// <summary>Gets <b>Done</b>: let the login go and clear the search.</summary>
    public RelayCommand DoneCommand { get; }

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
                Say("No instruments yet: every order would be rejected. Find a share above, e.g. Ericsson, and press Add.");
            }

            SearchHint = Hint(OrderLimit());
            ShowHits();
        }
        catch (Exception ex) when (ex is TradingConfigException or IOException or ArgumentException)
        {
            Say(ex.Message, isError: true);
        }
    }

    /// <summary>Lets the search's login go and clears the search (Done, or the page is no longer shown anywhere).</summary>
    public void CloseSearch()
    {
        CancelTyping();
        _search.Close();
        _searchSeq++; // an answer still on its way is not shown
        _hits = [];
        Results.Clear();
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        SayInSearch(string.Empty);
        IsSearching = false;
        RefreshSearchCommands();
    }

    protected override void OnBusyChanged()
    {
        OnPropertyChanged(nameof(IsSearchOpen));
        RefreshSearchCommands();
    }

    /// <summary>Searches for <see cref="SearchText"/> now; an answer to an older search that comes later is dropped.</summary>
    internal async Task SearchNowAsync()
    {
        CancelTyping();
        string query = SearchText.Trim();
        if (query.Length < MinQueryLength)
        {
            _searchSeq++;
            _hits = [];
            Results.Clear();
            IsSearching = false;
            SayInSearch(query.Length == 0 ? string.Empty : $"Type at least {MinQueryLength} characters.");
            return;
        }

        if (!CanSearch)
        {
            SayInSearch($"'{Engine.CurrentCommand}' is running; search when it has finished (Avanza wants a login to search).", isError: true);
            return;
        }

        int seq = ++_searchSeq;
        IsSearching = true;
        SayInSearch(_search.SearchNeedsLogin
            ? $"Searching for “{query}” … Avanza wants a login to search: {(_login() == "bankid" ? "approve it in BankID" : "logging in")} (read-only; one login until you press Done)."
            : $"Searching for “{query}” …");
        try
        {
            IReadOnlyList<InstrumentSearchHit> hits = await _search.SearchAsync(query);
            if (seq != _searchSeq)
            {
                return;
            }

            _hits = hits;
            ShowHits();
            SayInSearch(hits.Count switch
            {
                0 => $"No shares match “{query}”.",
                1 => "1 share.",
                _ => string.Create(CultureInfo.InvariantCulture, $"{hits.Count} shares."),
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (seq == _searchSeq)
            {
                SayInSearch($"The search failed: {ex.Message}", isError: true);
            }
        }
        finally
        {
            if (seq == _searchSeq)
            {
                IsSearching = false;
            }

            OnPropertyChanged(nameof(IsSearchOpen));
            RefreshSearchCommands();
        }
    }

    /// <summary>
    /// Whether <paramref name="hit"/> may join <paramref name="universe"/>, or why not: the rules the allowlist and the
    /// Paper session enforce (R2, SEK only, 5 names, R6's limit per order), shown before you press Add.
    /// </summary>
    /// <param name="sekPerUnit">The latest FX fixing for a USD or CAD share (ADR 0005), or null when none is stored yet (then Add decides).</param>
    internal static (bool CanAdd, bool OnList, string Why) Verdict(InstrumentSearchHit hit, Universe universe, decimal? orderLimit, decimal? sekPerUnit = null)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(universe);
        if (universe.Contains(hit.OrderbookId))
        {
            return (false, true, "On your list");
        }

        if (!hit.Tradeable)
        {
            return (false, false, "Not tradable at Avanza");
        }

        if (hit.Currency is { } currency && Markets.ForCurrency(currency) is null)
        {
            return (false, false, $"Trades in {currency}: the program trades shares in {Markets.CurrencyList}");
        }

        if (hit.Ticker is null)
        {
            return (false, false, "Avanza shows no ticker for it");
        }

        if (universe.Entries.Count >= Allowlist.MaxNames)
        {
            return (false, false, string.Create(CultureInfo.InvariantCulture, $"Your list is full ({Allowlist.MaxNames} names): remove one first"));
        }

        if (orderLimit is { } limit && SekPrice(hit, sekPerUnit) is { } price && price > limit)
        {
            return (false, false, $"One share costs more than an order may ({Fmt.Sek(limit)})");
        }

        return (true, false, string.Empty);
    }

    /// <summary>A hit's last price in SEK: as it is for a SEK share, at <paramref name="sekPerUnit"/> for a foreign one (null when unknown).</summary>
    internal static decimal? SekPrice(InstrumentSearchHit hit, decimal? sekPerUnit) =>
        hit.LastPrice is not { } price ? null
        : hit.Currency is null || !Markets.IsForeign(hit.Currency) ? price
        : sekPerUnit is { } fx ? price * fx
        : null;

    /// <summary>The hint under "Find a share", with R6's limit when the settings can be read.</summary>
    private static string Hint(decimal? orderLimit) =>
        string.Create(CultureInfo.InvariantCulture, $"Search Avanza by name or ticker; searching needs no login. Swedish, US and Canadian shares can be added (US and Canadian ones trade on paper, and the session then runs to 22:02). Add imports {InstrumentImport.AppYears} years of daily prices and allows the share: the first add logs in to Avanza (read-only) and that login serves every add until you press Done.")
        + (orderLimit is { } limit ? $" One order may be at most {Fmt.Sek(limit)}, so a share priced above that can't be bought." : string.Empty);

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

        if (!StoreFree)
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

    /// <summary>The search may be used: nothing else runs, or what runs is the search's own login.</summary>
    private bool CanUseLogin => !IsBusy || _search.IsOpen;

    /// <summary>A search needs no login and runs alongside anything, unless Avanza wanted a login for it this run.</summary>
    private bool CanSearch => !_search.SearchNeedsLogin || CanUseLogin;

    /// <summary>The price store may be read: nothing runs, or only the search's login, with no add writing to it.</summary>
    private bool StoreFree => !IsBusy || (_search.IsOpen && !AddResultCommand.IsRunning);

    private async Task SearchAfterPauseAsync()
    {
        CancelTyping();
        var typing = new CancellationTokenSource();
        _typing = typing;
        try
        {
            await Task.Delay(TypingPause, _time, typing.Token);
        }
        catch (OperationCanceledException)
        {
            return; // more was typed, or Enter searched already
        }

        await SearchNowAsync();
    }

    /// <summary>Drops the search waiting for the typing pause (its token is only cancelled here, then disposed).</summary>
    private void CancelTyping()
    {
        CancellationTokenSource? typing = _typing;
        _typing = null;
        typing?.Cancel();
        typing?.Dispose();
    }

    private async Task AddResultAsync(SearchResultRow? row)
    {
        if (row is null || !row.CanAdd)
        {
            return;
        }

        RemoveCommand.Refresh();
        SayInSearch(string.Create(CultureInfo.InvariantCulture, $"Adding {row.Ticker}: importing {InstrumentImport.AppYears} years of daily prices …"));
        ShareAdded added;
        try
        {
            added = await _search.AddAsync(new OrderbookId(row.OrderbookId));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SayInSearch($"Could not add {row.Ticker}: {ex.Message}", isError: true);
            return;
        }
        finally
        {
            OnPropertyChanged(nameof(IsSearchOpen));
            RemoveCommand.Refresh();
        }

        string to = added.LastDate is { } last ? string.Create(CultureInfo.InvariantCulture, $" to {last:yyyy-MM-dd}") : string.Empty;
        SayInSearch(string.Create(CultureInfo.InvariantCulture, $"{added.Ticker} added ({added.NewBars} new daily prices{to}). The next session trades it.")
            + (added.CalendarNote is { } note ? " " + note : string.Empty));
        Say(string.Empty);
        await RefreshAsync();
        SelectedRow = Rows.FirstOrDefault(r => r.Ticker == added.Ticker) ?? SelectedRow;
    }

    private async Task RemoveAsync(InstrumentRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (_search.IsOpen)
        {
            await _search.RemoveAsync(row.Ticker); // in turn with the search's adds, no second login
            Say($"{row.Ticker} removed from the allowlist.");
        }
        else
        {
            CommandResult result = await Engine.RunAsync($"Remove {row.Ticker}", CommandLines.UniverseRemove(_workspace, row.Ticker));
            Say(result.Succeeded ? $"{row.Ticker} removed from the allowlist." : $"Could not remove {row.Ticker}: {Why(result)}", !result.Succeeded);
        }

        await RefreshAsync();
    }

    /// <summary>Rebuilds the hits' add-or-why from the allowlist as it is now (after an add or a remove, too).</summary>
    private void ShowHits()
    {
        if (_hits.Count == 0)
        {
            Results.Clear();
            return;
        }

        Universe universe;
        try
        {
            universe = Universe.Load(Path.Combine(_workspace.ConfigDir, Universe.FileName));
        }
        catch (TradingConfigException ex)
        {
            SayInSearch(ex.Message, isError: true);
            return;
        }

        decimal? limit = OrderLimit();
        Dictionary<string, decimal> fx = LatestFx([.. _hits.Select(h => h.Currency).OfType<string>().Where(c => Markets.ForCurrency(c) is not null && Markets.IsForeign(c)).Distinct()]);
        Results.Clear();
        foreach (InstrumentSearchHit hit in _hits)
        {
            decimal? sekPerUnit = hit.Currency is { } ccy && fx.TryGetValue(ccy, out decimal rate) ? rate : null;
            (bool canAdd, bool onList, string why) = Verdict(hit, universe, limit, sekPerUnit);
            decimal? change = hit.TodayChangePercent / 100m;
            bool foreign = hit.Currency is { } cur && Markets.IsForeign(cur);
            Results.Add(new SearchResultRow(
                hit.OrderbookId.Value,
                hit.Ticker ?? string.Empty,
                hit.Name,
                string.Join(" · ", new[] { hit.FlagCode, hit.MarketPlaceName }.Where(p => !string.IsNullOrWhiteSpace(p))),
                hit.LastPrice is { } price ? Fmt.Price(price) + " " + (hit.Currency == "SEK" ? "kr" : hit.Currency ?? string.Empty) : "–",
                foreign && SekPrice(hit, sekPerUnit) is { } sek ? "≈ " + Fmt.Sek(decimal.Round(sek, 0)).Replace(",00 kr", " kr", StringComparison.Ordinal) : string.Empty,
                change is { } c ? Fmt.Arrow(c) : string.Empty,
                Tone.Direction(change ?? 0m),
                hit.Sector ?? string.Empty,
                canAdd,
                onList,
                why));
        }

        AddResultCommand.Refresh();
    }

    /// <summary>
    /// The latest stored FX fixing (ADR 0005) of each currency, read only while the price store is free. A currency with
    /// no fixing stored yet (no share in it added) is missing: its hits show no SEK value and Add decides.
    /// </summary>
    private Dictionary<string, decimal> LatestFx(IReadOnlyList<string> currencies)
    {
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (currencies.Count == 0 || !StoreFree || !File.Exists(_workspace.Store))
        {
            return result;
        }

        try
        {
            using HistoryStore store = HistoryStore.Open(_workspace.Store);
            DateOnly today = DateOnly.FromDateTime(MarketTime.ToStockholm(_time.GetUtcNow()).DateTime);
            foreach (string currency in currencies)
            {
                if (store.LatestFxRate(currency, Data.Fx.RiksbankFxSource.Riksbank.Name, today) is { } stored)
                {
                    result[currency] = stored.Rate.SekPerUnit;
                }
            }
        }
        catch (Exception ex) when (ex is HistoryStoreException or IOException || Cli.Commands.DataCommands.IsStoreFailure(ex))
        {
            // The store is busy: the hits show no SEK value this time.
        }

        return result;
    }

    /// <summary>R6's limit for one order at the Paper account's size, or null when the settings can't be read.</summary>
    private decimal? OrderLimit()
    {
        try
        {
            RiskLimits limits = RiskLimits.Load(Path.Combine(_workspace.ConfigDir, RiskLimits.FileName));
            PaperConfig paper = PaperConfig.Load(Path.Combine(_workspace.ConfigDir, PaperConfig.FileName));
            return limits.MaxOrderValue(paper.Cash);
        }
        catch (Exception ex) when (ex is TradingConfigException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private void SayInSearch(string text, bool isError = false)
    {
        SearchStatus = text;
        SearchStatusIsError = isError;
    }

    private void RefreshSearchCommands()
    {
        SearchCommand.Refresh();
        AddResultCommand.Refresh();
        DoneCommand.Refresh();
        RemoveCommand.Refresh();
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
        if (!File.Exists(_workspace.Store) || universe.Entries.Count == 0 || !StoreFree)
        {
            string text = !StoreFree ? "(shown when idle)" : "none yet";
            return universe.Entries.ToDictionary(e => e.OrderbookId.Value, _ => text, StringComparer.Ordinal);
        }

        try
        {
            using HistoryStore store = HistoryStore.Open(_workspace.Store);
            string source = AvanzaChartImporter.AvanzaPriceChart.Name;
            bool hasSource = store.GetSource(source) is not null;
            foreach (UniverseEntry e in universe.Entries)
            {
                IReadOnlyList<StoredBar> stored = hasSource ? store.GetDailyBars(e.OrderbookId, source) : [];
                string bars = stored.Count == 0
                    ? "none yet"
                    : string.Create(CultureInfo.InvariantCulture, $"{stored.Count} bars to {stored[^1].Bar.Date:yyyy-MM-dd}");

                // ADR 0005: a US or Canadian share says so, and that it trades on paper only.
                result[e.OrderbookId.Value] = Markets.ForCurrency(store.GetInstrument(e.OrderbookId)?.Instrument.Currency) is { } market && Markets.IsForeign(market.Currency)
                    ? $"{market.Currency} · {market.Name}, paper only · {bars}"
                    : bars;
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
