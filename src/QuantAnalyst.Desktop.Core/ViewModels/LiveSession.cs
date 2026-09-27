using System.Collections.ObjectModel;
using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Desktop.Core.Charts;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Mvvm;
using QuantAnalyst.Desktop.Core.Presentation;
using QuantAnalyst.Trading.Observation;
using QuantAnalyst.Trading.Oms;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>One instrument of today's session as a tile: last price, today's change, a sparkline and your holding.</summary>
public sealed class InstrumentTile(OrderbookId id, string ticker, string name, decimal? previousClose) : ObservableObject
{
    private readonly List<ChartPoint> _prices = [];
    private string _lastText = "–";
    private string _changeText = string.Empty;
    private string _direction = "flat";
    private string _positionText = "No holding";
    private ChartData _spark = ChartData.Empty;

    public OrderbookId Id { get; } = id;

    public string Ticker { get; } = ticker;

    public string Name { get; } = name;

    /// <summary>Gets the close today's change is measured from (the last stored one), or null to use the first quote.</summary>
    public decimal? PreviousClose { get; } = previousClose;

    public IReadOnlyList<ChartPoint> Prices => _prices;

    public string LastText
    {
        get => _lastText;
        private set => Set(ref _lastText, value);
    }

    /// <summary>Gets today's change with its arrow, e.g. "▲ +0,85 %".</summary>
    public string ChangeText
    {
        get => _changeText;
        private set => Set(ref _changeText, value);
    }

    /// <summary>Gets "up", "down" or "flat" (the change's colour).</summary>
    public string Direction
    {
        get => _direction;
        private set => Set(ref _direction, value);
    }

    public string PositionText
    {
        get => _positionText;
        internal set => Set(ref _positionText, value);
    }

    public ChartData Spark
    {
        get => _spark;
        private set => Set(ref _spark, value);
    }

    internal double? Baseline => PreviousClose is { } p ? (double)p : _prices.Count > 0 ? _prices[0].Value : null;

    /// <summary>Adds a price (the last trade, else the mid); within <paramref name="spacing"/> of the last point it replaces it.</summary>
    internal void AddPrice(DateTimeOffset at, decimal price, TimeSpan spacing, (DateTimeOffset, DateTimeOffset)? window)
    {
        var point = new ChartPoint(at, (double)price);
        if (_prices.Count > 0 && at - _prices[^1].At < spacing)
        {
            _prices[^1] = point;
        }
        else
        {
            _prices.Add(point);
        }

        decimal reference = PreviousClose ?? (decimal)_prices[0].Value;
        decimal change = reference != 0 ? (price - reference) / reference : 0m;
        LastText = Fmt.Price(price);
        ChangeText = Fmt.Arrow(change);
        Direction = Tone.Direction(change);
        Spark = new ChartData { Main = [.. _prices], Baseline = Baseline, Axis = TimeAxis.Intraday, Window = window };
    }
}

/// <summary>One of today's orders as the order list shows it; updated in place as it works and fills.</summary>
public sealed class OrderRow(Guid id, string time, string side, string ticker, string volume, string limit) : ObservableObject
{
    private string _state = string.Empty;
    private string _stateText = string.Empty;
    private string _filled = string.Empty;
    private string _average = "–";

    public Guid Id { get; } = id;

    /// <summary>Gets when the order was created (Stockholm), e.g. "09:10:13".</summary>
    public string Time { get; } = time;

    public string Side { get; } = side;

    public string Ticker { get; } = ticker;

    public string Volume { get; } = volume;

    public string Limit { get; } = limit;

    /// <summary>Gets the order book's state word (Working, Filled, …), which picks the chip's colour.</summary>
    public string State
    {
        get => _state;
        internal set => Set(ref _state, value);
    }

    /// <summary>Gets the state as the chip reads, e.g. "Partly filled".</summary>
    public string StateText
    {
        get => _stateText;
        internal set => Set(ref _stateText, value);
    }

    /// <summary>Gets "6 / 6".</summary>
    public string Filled
    {
        get => _filled;
        internal set => Set(ref _filled, value);
    }

    public string Average
    {
        get => _average;
        internal set => Set(ref _average, value);
    }
}

/// <summary>
/// The running Paper session as the Trading page shows it (docs/plans/11-app-redesign.md). It is the session's
/// <see cref="ISessionObserver"/>: events arrive on the session's threads and are applied on the UI thread, where they
/// become the value chart (against the start of the day), a tile per instrument with a sparkline, the selected
/// instrument's price chart with fills and working limits, today's orders and the decision's notes.
/// </summary>
public sealed class LiveSession : ObservableObject, ISessionObserver
{
    /// <summary>A new chart point at most this often; in between the last point moves with the value.</summary>
    public static readonly TimeSpan ValueSpacing = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan PriceSpacing = TimeSpan.FromSeconds(10);

    private readonly IUiDispatcher _ui;
    private readonly List<ChartPoint> _values = [];
    private readonly Dictionary<Guid, (OrderRow Row, long Filled, OrderTick Last)> _orders = [];
    private readonly Dictionary<OrderbookId, List<ChartMarker>> _fills = [];
    private (DateTimeOffset, DateTimeOffset)? _window;
    private decimal? _startOfDay;
    private InstrumentTile? _selected;
    private bool _hasData;
    private string _strategy = string.Empty;
    private ChartData _valueChart = ChartData.Empty;
    private ChartData _priceChart = ChartData.Empty;
    private string _valueText = "–";
    private string _dayChangeText = string.Empty;
    private string _dayDirection = "flat";
    private string _cashText = "–";
    private string _investedText = "–";
    private string _feesText = "–";
    private string _ordersText = "No orders yet";
    private string _decisionText = "The strategy decides at the decision time (09:10).";
    private DateTimeOffset? _decisionUtc;

    public LiveSession(IUiDispatcher ui)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    public ObservableCollection<InstrumentTile> Tiles { get; } = [];

    public ObservableCollection<OrderRow> Orders { get; } = [];

    public ObservableCollection<string> DecisionNotes { get; } = [];

    /// <summary>Gets a value indicating whether a session has reported anything since the last start.</summary>
    public bool HasData
    {
        get => _hasData;
        private set => Set(ref _hasData, value);
    }

    /// <summary>Gets the strategy the session trades, e.g. "ma-cross(fast=20, slow=100)".</summary>
    public string Strategy
    {
        get => _strategy;
        private set => Set(ref _strategy, value);
    }

    /// <summary>Gets when today's decision is due, if the session said.</summary>
    public DateTimeOffset? DecisionUtc
    {
        get => _decisionUtc;
        private set => Set(ref _decisionUtc, value);
    }

    public ChartData ValueChart
    {
        get => _valueChart;
        private set => Set(ref _valueChart, value);
    }

    public ChartData PriceChart
    {
        get => _priceChart;
        private set => Set(ref _priceChart, value);
    }

    /// <summary>Gets or sets the tile whose price chart is shown.</summary>
    public InstrumentTile? SelectedTile
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                BuildPriceChart();
            }
        }
    }

    public string ValueText
    {
        get => _valueText;
        private set => Set(ref _valueText, value);
    }

    /// <summary>Gets today's change, e.g. "+12,40 kr · +0,25 %".</summary>
    public string DayChangeText
    {
        get => _dayChangeText;
        private set => Set(ref _dayChangeText, value);
    }

    public string DayDirection
    {
        get => _dayDirection;
        private set => Set(ref _dayDirection, value);
    }

    public string CashText
    {
        get => _cashText;
        private set => Set(ref _cashText, value);
    }

    /// <summary>Gets the share of the account in shares, e.g. "18,4 %".</summary>
    public string InvestedText
    {
        get => _investedText;
        private set => Set(ref _investedText, value);
    }

    public string FeesText
    {
        get => _feesText;
        private set => Set(ref _feesText, value);
    }

    /// <summary>Gets "3 orders · 2 filled".</summary>
    public string OrdersText
    {
        get => _ordersText;
        private set => Set(ref _ordersText, value);
    }

    /// <summary>Gets what the strategy decided, e.g. "10:12:00 · 2 orders".</summary>
    public string DecisionText
    {
        get => _decisionText;
        private set => Set(ref _decisionText, value);
    }

    // ---- ISessionObserver: on the session's threads, applied on the UI thread ------------------------------

    void ISessionObserver.Started(SessionStarted e) => _ui.Post(() => Apply(e));

    void ISessionObserver.Quote(QuoteTick e) => _ui.Post(() => Apply(e));

    void ISessionObserver.Account(AccountTick e) => _ui.Post(() => Apply(e));

    void ISessionObserver.Order(OrderTick e) => _ui.Post(() => Apply(e));

    void ISessionObserver.Decision(DecisionTick e) => _ui.Post(() => Apply(e));

    /// <summary>Forgets the last session, before a new one starts.</summary>
    public void Reset()
    {
        _values.Clear();
        _orders.Clear();
        _fills.Clear();
        _window = null;
        _startOfDay = null;
        Tiles.Clear();
        Orders.Clear();
        DecisionNotes.Clear();
        SelectedTile = null;
        HasData = false;
        Strategy = string.Empty;
        DecisionUtc = null;
        ValueChart = PriceChart = ChartData.Empty;
        ValueText = CashText = InvestedText = FeesText = "–";
        DayChangeText = string.Empty;
        DayDirection = "flat";
        OrdersText = "No orders yet";
        DecisionText = "The strategy decides at the decision time (09:10).";
    }

    internal void Apply(SessionStarted e)
    {
        Reset();
        HasData = true;
        Strategy = e.Strategy;
        DecisionUtc = e.DecisionUtc;
        _window = e.OpenUtc is { } open && e.CloseUtc is { } close && close > open ? (open, close) : null;
        foreach (ObservedInstrument i in e.Instruments)
        {
            Tiles.Add(new InstrumentTile(i.OrderbookId, i.Ticker, i.Name, i.PreviousClose));
        }

        if (e.DecisionUtc is { } d)
        {
            DecisionText = "Decides at " + Local(d, "HH:mm");
        }

        SelectedTile = Tiles.FirstOrDefault();
    }

    internal void Apply(QuoteTick e)
    {
        if (Tiles.FirstOrDefault(t => t.Id == e.OrderbookId) is not { } tile || Reference(e) is not { } price)
        {
            return;
        }

        tile.AddPrice(e.AtUtc, price, PriceSpacing, _window);
        if (ReferenceEquals(tile, _selected))
        {
            BuildPriceChart();
        }
    }

    internal void Apply(AccountTick e)
    {
        HasData = true;
        _startOfDay ??= e.StartOfDayValue > 0 ? e.StartOfDayValue : e.Value;
        var point = new ChartPoint(e.AtUtc, (double)e.Value);
        if (_values.Count > 0 && e.AtUtc - _values[^1].At < ValueSpacing)
        {
            _values[^1] = point;
        }
        else
        {
            _values.Add(point);
        }

        decimal start = _startOfDay.Value;
        decimal change = e.Value - start;
        ValueText = Fmt.Sek(e.Value);
        DayChangeText = Fmt.ChangeSek(change) + " · " + Fmt.ChangePct(start != 0 ? change / start : 0m);
        DayDirection = Tone.Direction(change);
        CashText = Fmt.Sek(e.Cash);
        decimal inShares = e.Positions.Sum(p => p.Value);
        InvestedText = e.Value > 0 ? Fmt.Pct(inShares / e.Value) : "–";
        FeesText = Fmt.Sek(e.FeesPaid);
        ValueChart = new ChartData
        {
            Main = [.. _values],
            Baseline = (double)start,
            Axis = TimeAxis.Intraday,
            Window = _window,
            FormatValue = v => Fmt.Sek((decimal)v),
        };

        foreach (InstrumentTile tile in Tiles)
        {
            ObservedPosition? held = e.Positions.FirstOrDefault(p => p.OrderbookId == tile.Id);
            tile.PositionText = held is { Quantity: > 0 }
                ? string.Create(CultureInfo.InvariantCulture, $"{Fmt.Count(held.Quantity)} shares · {Fmt.Sek(held.Value)}")
                : "No holding";
        }
    }

    internal void Apply(OrderTick e)
    {
        HasData = true;
        if (!_orders.TryGetValue(e.ClientOrderId, out (OrderRow Row, long Filled, OrderTick Last) known))
        {
            var row = new OrderRow(e.ClientOrderId, Local(e.CreatedUtc, "HH:mm:ss"), e.Side == OrderSide.Buy ? "Buy" : "Sell", e.Ticker,
                Fmt.Count(e.Volume), e.Limit is { } l ? Fmt.Price(l) : "–");
            Orders.Insert(0, row);
            known = (row, 0, e);
        }

        if (e.Filled > known.Filled && (e.AveragePrice ?? e.Limit) is { } price)
        {
            long added = e.Filled - known.Filled;
            if (!_fills.TryGetValue(e.OrderbookId, out List<ChartMarker>? markers))
            {
                _fills[e.OrderbookId] = markers = [];
            }

            string verb = e.Side == OrderSide.Buy ? "Bought" : "Sold";
            markers.Add(new ChartMarker(e.AtUtc, (double)price, e.Side == OrderSide.Buy ? MarkerKind.Buy : MarkerKind.Sell,
                string.Create(CultureInfo.InvariantCulture, $"{verb} {Fmt.Count(added)} @ {Fmt.Price(price)}")));
        }

        known.Row.State = e.State.ToString();
        known.Row.StateText = e.State switch
        {
            OmsState.PartiallyFilled => "Partly filled",
            OmsState.New or OmsState.Sent => "Sending",
            _ => e.State.ToString(),
        };
        known.Row.Filled = string.Create(CultureInfo.InvariantCulture, $"{Fmt.Count(e.Filled)} / {Fmt.Count(e.Volume)}");
        known.Row.Average = e.AveragePrice is { } avg ? Fmt.Price(avg) : "–";
        _orders[e.ClientOrderId] = (known.Row, Math.Max(known.Filled, e.Filled), e);

        int filled = _orders.Values.Count(o => o.Filled > 0);
        OrdersText = string.Create(CultureInfo.InvariantCulture, $"{_orders.Count} order{(_orders.Count == 1 ? string.Empty : "s")} · {filled} with fills");
        if (_selected is { } tile && tile.Id == e.OrderbookId)
        {
            BuildPriceChart();
        }
    }

    internal void Apply(DecisionTick e)
    {
        HasData = true;
        DecisionText = string.Create(CultureInfo.InvariantCulture,
            $"{Local(e.AtUtc, "HH:mm:ss")} · {e.Orders} order{(e.Orders == 1 ? string.Empty : "s")}");
        DecisionNotes.Clear();
        foreach (string note in e.Notes)
        {
            DecisionNotes.Add(note);
        }
    }

    private static decimal? Reference(QuoteTick q) =>
        q.Last ?? (q is { Bid: { } b, Ask: { } a } ? (b + a) / 2 : null);

    private static string Local(DateTimeOffset utc, string format) =>
        MarketTime.ToStockholm(utc).ToString(format, CultureInfo.InvariantCulture);

    private void BuildPriceChart()
    {
        if (_selected is not { } tile || tile.Prices.Count == 0)
        {
            PriceChart = ChartData.Empty;
            return;
        }

        PriceChart = new ChartData
        {
            Main = [.. tile.Prices],
            Baseline = tile.Baseline,
            Axis = TimeAxis.Intraday,
            Window = _window,
            Markers = _fills.TryGetValue(tile.Id, out List<ChartMarker>? m) ? [.. m] : [],
            Levels =
            [
                .. _orders.Values
                    .Where(o => o.Last.OrderbookId == tile.Id && o.Last.Limit is not null
                                && o.Last.State is OmsState.New or OmsState.Sent or OmsState.Working or OmsState.PartiallyFilled)
                    .Select(o => new ChartLevel((double)o.Last.Limit!.Value,
                        string.Create(CultureInfo.InvariantCulture, $"{(o.Last.Side == OrderSide.Buy ? "Buy" : "Sell")} {Fmt.Count(o.Last.Volume - o.Last.Filled)} @ {Fmt.Price(o.Last.Limit.Value)}"))),
            ],
            FormatValue = v => Fmt.Price((decimal)v),
        };
    }
}
