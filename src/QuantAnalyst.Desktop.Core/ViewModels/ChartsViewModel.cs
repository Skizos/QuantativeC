using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Desktop.Core.Charts;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Mvvm;
using QuantAnalyst.Desktop.Core.Presentation;
using QuantAnalyst.Trading.Reports;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>An instrument you can chart: one of your allowlist, or one the running session trades.</summary>
public sealed record ChartInstrument(string Ticker, string Name, OrderbookId Id);

/// <summary>A small number under the chart, e.g. ("High", "95,82").</summary>
public sealed record StatTile(string Label, string Value);

/// <summary>
/// The charts page and window (docs/plans/12-charts-window.md): candlesticks for your instruments.
/// <para><b>History:</b> the stored daily candles as Day, Week or Month candles over a range (1M … All), with volume,
/// the saved strategy's moving averages (else 20 and 50 days) and your trades from the daily reports.</para>
/// <para><b>Today:</b> while a Paper session runs, 1, 5 or 15 minute candles built from its quotes, with your fills,
/// working limits and yesterday's close; they grow as the session trades.</para>
/// Everything is read-only. The price store and the audit log are read while no command runs and kept in
/// <see cref="HistoryCandles"/>, shared with every charts window, so History still works while a session trades.
/// </summary>
public sealed class ChartsViewModel : PageViewModel, IDisposable
{
    public const string History = "History";
    public const string Today = "Today";

    private readonly Workspace _workspace;
    private readonly TimeProvider _time;
    private readonly LiveSession _live;
    private readonly HistoryCandles _history;
    private readonly Action<ChartsViewModel>? _openWindow;
    private IReadOnlyList<ChartInstrument> _allowlist = [];
    private OrderbookId? _wanted;
    private ChartInstrument? _selected;
    private string _source = History;
    private CandlePeriod _period = CandlePeriod.Day;
    private ChartRange _range = ChartRange.All[1]; // 3M: day candles wide enough to read
    private bool _showVolume = true;
    private bool _showAverages = true;
    private bool _showTrades = true;
    private bool _disposed;
    private CandleChartData _chart = CandleChartData.Empty;
    private string _ticker = string.Empty;
    private string _name = string.Empty;
    private string _lastText = string.Empty;
    private string _changeText = string.Empty;
    private string _direction = "flat";
    private string _asOfText = string.Empty;
    private string _statsTitle = string.Empty;
    private string _note = string.Empty;
    private string _emptyText = "Choose an instrument";

    public ChartsViewModel(Workspace workspace, QaEngine engine, TimeProvider time, LiveSession live, HistoryCandles history, Action<ChartsViewModel>? openWindow = null)
        : base(PageKind.Charts, "Charts", "Candlesticks for your instruments: the stored history, and today's session as it trades.", engine)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _live = live ?? throw new ArgumentNullException(nameof(live));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _openWindow = openWindow;
        OpenWindowCommand = new RelayCommand(() => _openWindow?.Invoke(this), () => _openWindow is not null);
        _live.InstrumentChanged += OnLiveInstrumentChanged;
        _live.Tiles.CollectionChanged += OnLiveTilesChanged;
    }

    public ObservableCollection<ChartInstrument> Instruments { get; } = [];

    public ChartInstrument? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                Build();
            }
        }
    }

    public IReadOnlyList<string> Sources { get; } = [History, Today];

    /// <summary>Gets or sets "History" (the stored daily candles) or "Today" (the running session's candles).</summary>
    public string SelectedSource
    {
        get => _source;
        set
        {
            if (value is (History or Today) && Set(ref _source, value))
            {
                _period = IsHistory ? CandlePeriod.Day : CandlePeriod.FiveMinutes;
                OnPropertyChanged(nameof(IsHistory));
                OnPropertyChanged(nameof(Periods));
                OnPropertyChanged(nameof(SelectedPeriod));
                Build();
            }
        }
    }

    public bool IsHistory => _source == History;

    /// <summary>Gets the candle lengths for the source: Day, Week, Month or 1, 5, 15 minutes.</summary>
    public IReadOnlyList<CandlePeriod> Periods => IsHistory ? CandlePeriod.Daily : CandlePeriod.Intraday;

    public CandlePeriod SelectedPeriod
    {
        get => _period;
        set
        {
            if (value is not null && Periods.Contains(value) && Set(ref _period, value))
            {
                Build();
            }
        }
    }

    public IReadOnlyList<ChartRange> Ranges { get; } = ChartRange.All;

    /// <summary>Gets or sets how far back History shows at first (zoom out or drag for older candles).</summary>
    public ChartRange SelectedRange
    {
        get => _range;
        set
        {
            if (value is not null && Set(ref _range, value))
            {
                Build();
            }
        }
    }

    public bool ShowVolume
    {
        get => _showVolume;
        set
        {
            if (Set(ref _showVolume, value))
            {
                Build();
            }
        }
    }

    /// <summary>Gets or sets whether the moving averages are drawn (History).</summary>
    public bool ShowAverages
    {
        get => _showAverages;
        set
        {
            if (Set(ref _showAverages, value))
            {
                Build();
            }
        }
    }

    /// <summary>Gets or sets whether your trades are drawn (▲ bought, ▼ sold).</summary>
    public bool ShowTrades
    {
        get => _showTrades;
        set
        {
            if (Set(ref _showTrades, value))
            {
                Build();
            }
        }
    }

    public CandleChartData Chart
    {
        get => _chart;
        private set => Set(ref _chart, value);
    }

    public string Ticker
    {
        get => _ticker;
        private set => Set(ref _ticker, value);
    }

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    /// <summary>Gets the last price or close, e.g. "94,96 kr".</summary>
    public string LastText
    {
        get => _lastText;
        private set => Set(ref _lastText, value);
    }

    /// <summary>Gets the change over the range (History) or since yesterday's close (Today), e.g. "▲ +4,20 % · 6M".</summary>
    public string ChangeText
    {
        get => _changeText;
        private set => Set(ref _changeText, value);
    }

    public string Direction
    {
        get => _direction;
        private set => Set(ref _direction, value);
    }

    /// <summary>Gets which close or moment the price is, e.g. "Close Fri 25 Sep 2026" or "Live · 10:15".</summary>
    public string AsOfText
    {
        get => _asOfText;
        private set => Set(ref _asOfText, value);
    }

    /// <summary>Gets what the numbers under the chart are about, e.g. "Latest week · from 21 Sep".</summary>
    public string StatsTitle
    {
        get => _statsTitle;
        private set => Set(ref _statsTitle, value);
    }

    public ObservableCollection<StatTile> Stats { get; } = [];

    /// <summary>Gets what the chart shows and how to move it.</summary>
    public string Note
    {
        get => _note;
        private set => Set(ref _note, value);
    }

    /// <summary>Gets what the chart says while it has no candles.</summary>
    public string EmptyText
    {
        get => _emptyText;
        private set => Set(ref _emptyText, value);
    }

    /// <summary>Gets the button that opens another charts window (you can have several, each on its own instrument).</summary>
    public RelayCommand OpenWindowCommand { get; }

    /// <summary>Starts like <paramref name="other"/>: its instrument, source, candle length, range and switches.</summary>
    public void CopySettings(ChartsViewModel other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _wanted = other._selected?.Id;
        _source = other._source;
        _period = other._period;
        _range = other._range;
        _showVolume = other._showVolume;
        _showAverages = other._showAverages;
        _showTrades = other._showTrades;
    }

    public override async Task RefreshAsync()
    {
        try
        {
            _allowlist = await Task.Run(LoadAllowlist);
        }
        catch (Exception ex) when (ex is TradingConfigException or IOException or ArgumentException)
        {
            Say(ex.Message, isError: true);
        }

        FillInstruments();
        if (!IsBusy)
        {
            await LoadHistoryAsync();
        }

        Build();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _live.InstrumentChanged -= OnLiveInstrumentChanged;
        _live.Tiles.CollectionChanged -= OnLiveTilesChanged;
        Detach();
    }

    protected override void OnBusyChanged()
    {
        // A command (e.g. the session, which brings the history up to date) just finished: read the store again.
        if (!IsBusy && !_disposed)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>Reads every instrument's daily candles and your trades (only while no command runs), for every charts window.</summary>
    internal async Task LoadHistoryAsync()
    {
        OrderbookId[] ids = [.. Instruments.Select(i => i.Id)];
        string[] tickers = [.. Instruments.Select(i => i.Ticker)];
        try
        {
            (Dictionary<OrderbookId, IReadOnlyList<Candle>> candles, Dictionary<string, IReadOnlyList<ChartMarker>> trades) = await Task.Run(() =>
            {
                Dictionary<OrderbookId, IReadOnlyList<Candle>> c = ids.Distinct().ToDictionary(id => id, id => ChartSources.DailyCandles(_workspace.Store, id));
                IReadOnlyList<EodReport> reports = ChartSources.Reports(_workspace, _time);
                Dictionary<string, IReadOnlyList<ChartMarker>> t = tickers.Distinct(StringComparer.Ordinal)
                    .ToDictionary(k => k, k => ChartSources.TradeMarkers(reports, k), StringComparer.Ordinal);
                return (c, t);
            });
            _history.Put(candles, trades, _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is HistoryStoreException or IOException or JsonException or InvalidDataException || Cli.Commands.DataCommands.IsStoreFailure(ex))
        {
            Say("The price store or the audit log is busy; the charts come back on the next refresh.", isError: false);
        }
    }

    private List<ChartInstrument> LoadAllowlist()
    {
        Universe universe = Universe.Load(Path.Combine(_workspace.ConfigDir, Universe.FileName));
        return [.. universe.Entries.Select(e => new ChartInstrument(e.Ticker, e.Name, e.OrderbookId))];
    }

    /// <summary>The allowlist, then any instrument the running session trades that isn't on it; the selection is kept.</summary>
    private void FillInstruments()
    {
        var all = new List<ChartInstrument>(_allowlist);
        foreach (InstrumentTile tile in _live.Tiles)
        {
            if (all.All(i => i.Id != tile.Id))
            {
                all.Add(new ChartInstrument(tile.Ticker, tile.Name, tile.Id));
            }
        }

        OrderbookId? keep = _selected?.Id ?? _wanted;
        if (!all.SequenceEqual(Instruments))
        {
            Instruments.Clear();
            foreach (ChartInstrument i in all)
            {
                Instruments.Add(i);
            }
        }

        _wanted = null;
        ChartInstrument? next = Instruments.FirstOrDefault(i => i.Id == keep) ?? Instruments.FirstOrDefault();
        if (!Equals(next, _selected))
        {
            Selected = next;
        }
    }

    private void OnLiveTilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        FillInstruments();
        if (!IsHistory)
        {
            Build();
        }
    }

    private void OnLiveInstrumentChanged(OrderbookId id)
    {
        if (!IsHistory && _selected?.Id == id)
        {
            Build();
        }
    }

    private void Build()
    {
        Stats.Clear();
        ChartInstrument? instrument = _selected;
        Ticker = instrument?.Ticker ?? string.Empty;
        Name = instrument?.Name ?? string.Empty;
        if (instrument is null)
        {
            Clear(Instruments.Count == 0 ? "No instruments yet: add one on the Instruments page." : "Choose an instrument to see its candles");
            return;
        }

        if (IsHistory)
        {
            BuildHistory(instrument);
        }
        else
        {
            BuildToday(instrument);
        }
    }

    private void Clear(string why)
    {
        Chart = CandleChartData.Empty;
        LastText = ChangeText = AsOfText = StatsTitle = Note = string.Empty;
        Direction = "flat";
        EmptyText = why;
    }

    private void BuildHistory(ChartInstrument instrument)
    {
        IReadOnlyList<Candle> days = _history.CandlesOf(instrument.Id);
        if (days.Count == 0)
        {
            Clear(_history.LoadedAt is null
                ? IsBusy ? "The history loads when nothing else runs (the price store is in use)." : "Loading the stored prices …"
                : "No stored prices yet: they are imported when the name is added on the Instruments page.");
            return;
        }

        IReadOnlyList<Candle> candles = Candles.Aggregate(days, _period);
        (int Fast, int Slow)? saved = ChartSources.SavedAverages(_workspace);
        (int fast, int slow) = saved ?? (20, 50);
        IReadOnlyList<ChartPoint> closes = Candles.Closes(days);
        Chart = new CandleChartData
        {
            Candles = candles,
            Period = _period,
            Overlays = _showAverages
                ? [.. new[] { fast, slow }.Select(n => new ChartOverlay(string.Create(CultureInfo.InvariantCulture, $"{n}-day average"), Indicators.Sma(closes, n)))]
                : [],
            Markers = _showTrades ? _history.TradesOf(instrument.Ticker) : [],
            ShowVolume = _showVolume,
            InitialCount = InitialCount(candles, days),
            Key = string.Create(CultureInfo.InvariantCulture, $"{instrument.Id.Value}|{History}|{_period.Label}|{_range.Label}"),
            FormatValue = v => Fmt.Price((decimal)v),
        };

        Candle lastDay = days[^1];
        IReadOnlyList<ChartPoint> inRange = _range.Take(closes);
        double first = inRange.Count > 0 ? inRange[0].Value : lastDay.Close;
        decimal change = first != 0 ? (decimal)((lastDay.Close - first) / first) : 0m;
        LastText = Fmt.Price((decimal)lastDay.Close) + " kr";
        ChangeText = Fmt.Arrow(change) + " · " + _range.Label;
        Direction = Tone.Direction(change);
        AsOfText = "Close " + Local(lastDay.At, "ddd d MMM yyyy");

        Candle latest = candles[^1];
        StatsTitle = _period.Unit switch
        {
            CandleUnit.Week => "Latest week · from " + Local(latest.At, "d MMM"),
            CandleUnit.Month => "Latest month · " + Local(latest.At, "MMMM yyyy"),
            _ => "Latest day · " + Local(latest.At, "ddd d MMM"),
        };
        AddOhlc(latest);
        Stats.Add(new StatTile("Volume", latest.Volume is { } v ? Fmt.Count(v) : "–"));
        IReadOnlyList<Candle> rangeDays = [.. days.Where(d => inRange.Count > 0 && d.At >= inRange[0].At)];
        if (rangeDays.Count > 0)
        {
            Stats.Add(new StatTile(_range.Label + " high", Fmt.Price((decimal)rangeDays.Max(d => d.High))));
            Stats.Add(new StatTile(_range.Label + " low", Fmt.Price((decimal)rangeDays.Min(d => d.Low))));
        }

        Note = "Daily prices as Avanza reports them (adjusted, not point-in-time)."
               + (_showAverages
                   ? string.Create(CultureInfo.InvariantCulture, $" Blue and orange lines: the {fast}- and {slow}-day averages{(saved is null ? string.Empty : " the saved ma-cross compares")}.")
                   : string.Empty)
               + (_showTrades ? " ▲ ▼ your trades, from the daily reports." : string.Empty)
               + " Wheel to zoom, drag to move, double-click to reset.";
        EmptyText = string.Empty;
    }

    private void BuildToday(ChartInstrument instrument)
    {
        InstrumentTile? tile = _live.Tiles.FirstOrDefault(t => t.Id == instrument.Id);
        if (!_live.HasData)
        {
            Clear("Today's candles appear while a Paper session runs: start it on the Trading page.");
            return;
        }

        if (tile is null)
        {
            Clear($"The running session doesn't trade {instrument.Ticker}.");
            return;
        }

        if (tile.Candles.Count == 0)
        {
            Clear("Waiting for the first quote …");
            return;
        }

        IReadOnlyList<Candle> candles = Candles.Aggregate(tile.Candles, _period);
        var levels = new List<ChartLevel>(_live.WorkingLimitsOf(instrument.Id));
        if (tile.PreviousClose is { } previous)
        {
            levels.Add(new ChartLevel((double)previous, "Yesterday's close " + Fmt.Price(previous)));
        }

        Chart = new CandleChartData
        {
            Candles = candles,
            Period = _period,
            Markers = _showTrades ? _live.FillsOf(instrument.Id) : [],
            Levels = levels,
            ShowVolume = false,
            Key = string.Create(CultureInfo.InvariantCulture, $"{instrument.Id.Value}|{Today}|{_period.Label}"),
            FormatValue = v => Fmt.Price((decimal)v),
        };

        Candle last = candles[^1];
        double reference = tile.PreviousClose is { } p ? (double)p : candles[0].Open;
        decimal change = reference != 0 ? (decimal)((last.Close - reference) / reference) : 0m;
        LastText = Fmt.Price((decimal)last.Close) + " kr";
        ChangeText = Fmt.Arrow(change) + " · today";
        Direction = Tone.Direction(change);
        AsOfText = "Live · latest candle " + Local(last.At, "HH:mm");

        StatsTitle = "Today";
        Stats.Add(new StatTile("Open", Fmt.Price((decimal)candles[0].Open)));
        Stats.Add(new StatTile("High", Fmt.Price((decimal)candles.Max(c => c.High))));
        Stats.Add(new StatTile("Low", Fmt.Price((decimal)candles.Min(c => c.Low))));
        Stats.Add(new StatTile("Last", Fmt.Price((decimal)last.Close)));
        if (tile.PreviousClose is { } yesterday)
        {
            Stats.Add(new StatTile("Yesterday's close", Fmt.Price(yesterday)));
        }

        Note = "Built from the session's quotes (the last trade, else the middle of bid and ask), about one a second; no volume."
               + (_showTrades ? " ▲ ▼ your fills today." : string.Empty)
               + " Dashed lines: working limits and yesterday's close. Wheel to zoom, drag to move, double-click to reset.";
        EmptyText = string.Empty;
    }

    private void AddOhlc(Candle c)
    {
        Stats.Add(new StatTile("Open", Fmt.Price((decimal)c.Open)));
        Stats.Add(new StatTile("High", Fmt.Price((decimal)c.High)));
        Stats.Add(new StatTile("Low", Fmt.Price((decimal)c.Low)));
        Stats.Add(new StatTile("Close", Fmt.Price((decimal)c.Close)));
    }

    /// <summary>How many candles the range covers (null for All): those from the range's first day on.</summary>
    private int? InitialCount(IReadOnlyList<Candle> candles, IReadOnlyList<Candle> days)
    {
        if (_range.Months is not { } months)
        {
            return null;
        }

        DateOnly last = DateOnly.FromDateTime(MarketTime.ToStockholm(days[^1].At).DateTime);
        DateTimeOffset from = _period.StartOf(CandlePeriod.Midnight(last.AddMonths(-months)));
        return candles.Count(c => c.At >= from);
    }

    private static string Local(DateTimeOffset utc, string format) =>
        MarketTime.ToStockholm(utc).ToString(format, CultureInfo.InvariantCulture);
}
