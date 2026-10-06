using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Backtesting;

/// <summary>An instrument of a backtest universe.</summary>
/// <param name="Symbol">Ticker or synthetic id.</param>
/// <param name="LotSize">Shares per lot (1 for Nasdaq Stockholm equities).</param>
/// <param name="ForeignCurrency">True when the instrument does not trade in SEK (the FX fee applies).</param>
/// <param name="TickSizes">Valid limit prices; limits are rounded to it passively.</param>
public sealed record PanelInstrument(string Symbol, long LotSize, bool ForeignCurrency, TickSizeTable TickSizes)
{
    /// <summary>Gets the currency the share trades in; its prices in the panel are SEK either way (ADR 0005).</summary>
    public string Currency { get; init; } = Markets.Stockholm.Currency;

    /// <summary>
    /// Gets the last FX fixing used for a foreign share's prices (SEK per unit): its courtage minimum and tick table are
    /// converted at it. Null for a SEK share.
    /// </summary>
    public decimal? LastSekPerUnit { get; init; }

    /// <summary>
    /// Gets the share's own half-spread in bps for market-type fills, measured from a session's bid/ask samples
    /// (plan 17, ABI 1.4); null uses the cost model's.
    /// </summary>
    public double? HalfSpreadBps { get; init; }

    /// <summary>
    /// Gets a Swedish share's marketplace (Avanza's code, e.g. "XSTO", "FNSE"): one other than the main market pays its own
    /// courtage (plan 22). Null is the main market.
    /// </summary>
    public string? MarketPlace { get; init; }
}

/// <summary>
/// Bars of a universe on one time axis (T periods × N instruments): daily bars, or intraday bars (plan 17) with a
/// start time each (<see cref="BarStartsUtc"/>; <see cref="Dates"/> then gives each bar's Stockholm trading date). A missing bar (holiday for that
/// instrument, halt, not listed yet) is stored as NaN and is invalid: nothing fills on it. Storage is per instrument
/// in time order, so <see cref="BarWindow"/> can hand out spans that end at the current bar. <see cref="Truncate"/>
/// and <see cref="Between"/> are views over the same arrays that cannot reach bars outside their range.
/// </summary>
public sealed class MarketPanel
{
    private readonly int _stride; // periods of the underlying arrays
    private readonly int _offset; // first underlying period of this view
    private readonly double[] _open;
    private readonly double[] _high;
    private readonly double[] _low;
    private readonly double[] _close;
    private readonly double[] _volume;

    /// <param name="open">Column-major: <c>open[i * T + t]</c>. Same for the other arrays; NaN close = no bar.</param>
    /// <param name="barStartsUtc">
    /// Intraday bars only: each bar's start, strictly increasing; <paramref name="dates"/> must be each start's Stockholm
    /// date (so several bars share a date). Null for daily bars, whose dates are strictly increasing.
    /// </param>
    public MarketPanel(
        IReadOnlyList<DateOnly> dates,
        IReadOnlyList<PanelInstrument> instruments,
        double[] open,
        double[] high,
        double[] low,
        double[] close,
        double[] volume,
        DataSourceInfo source,
        IReadOnlyList<DateTimeOffset>? barStartsUtc = null)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(source);
        if (dates.Count == 0 || instruments.Count == 0)
        {
            throw new ArgumentException("A panel needs at least one date and one instrument.");
        }

        if (barStartsUtc is null)
        {
            for (int t = 1; t < dates.Count; t++)
            {
                if (dates[t] <= dates[t - 1])
                {
                    throw new ArgumentException($"Dates must be strictly increasing ({dates[t - 1]} then {dates[t]}).", nameof(dates));
                }
            }
        }
        else
        {
            if (barStartsUtc.Count != dates.Count)
            {
                throw new ArgumentException($"barStartsUtc must have one start per date ({dates.Count}), got {barStartsUtc.Count}.", nameof(barStartsUtc));
            }

            for (int t = 0; t < dates.Count; t++)
            {
                if (t > 0 && barStartsUtc[t] <= barStartsUtc[t - 1])
                {
                    throw new ArgumentException($"Bar starts must be strictly increasing ({barStartsUtc[t - 1]:O} then {barStartsUtc[t]:O}).", nameof(barStartsUtc));
                }

                if (dates[t] != DateOnly.FromDateTime(MarketTime.ToStockholm(barStartsUtc[t]).DateTime))
                {
                    throw new ArgumentException($"Bar {barStartsUtc[t]:O} is dated {dates[t]:yyyy-MM-dd}, not its Stockholm date.", nameof(dates));
                }
            }
        }

        int cells = dates.Count * instruments.Count;
        foreach ((double[] a, string name) in new[] { (open, nameof(open)), (high, nameof(high)), (low, nameof(low)), (close, nameof(close)), (volume, nameof(volume)) })
        {
            ArgumentNullException.ThrowIfNull(a, name);
            if (a.Length != cells)
            {
                throw new ArgumentException($"{name} must have {cells} elements (T × N), got {a.Length}.", name);
            }
        }

        Dates = dates;
        BarStartsUtc = barStartsUtc;
        Instruments = instruments;
        Source = source;
        Periods = dates.Count;
        _stride = Periods;
        _offset = 0;
        _open = open;
        _high = high;
        _low = low;
        _close = close;
        _volume = volume;
        for (int i = 0; i < instruments.Count; i++)
        {
            if (instruments[i].LotSize < 1)
            {
                throw new ArgumentException($"{instruments[i].Symbol}: lot size must be >= 1.", nameof(instruments));
            }

            for (int t = 0; t < Periods; t++)
            {
                int k = (i * Periods) + t;
                if (double.IsNaN(close[k]))
                {
                    continue;
                }

                bool ok = Positive(open[k]) && Positive(high[k]) && Positive(low[k]) && Positive(close[k])
                    && low[k] <= Math.Min(open[k], close[k]) && high[k] >= Math.Max(open[k], close[k])
                    && double.IsFinite(volume[k]) && volume[k] >= 0;
                if (!ok)
                {
                    throw new ArgumentException(
                        $"{instruments[i].Symbol} {dates[t]}: inconsistent bar (open {open[k]}, high {high[k]}, low {low[k]}, close {close[k]}, volume {volume[k]}).");
                }
            }
        }
    }

    private MarketPanel(MarketPanel parent, int start, int count)
    {
        Dates = [.. parent.Dates.Skip(start).Take(count)];
        BarStartsUtc = parent.BarStartsUtc is { } starts ? [.. starts.Skip(start).Take(count)] : null;
        Instruments = parent.Instruments;
        Source = parent.Source;
        Periods = count;
        _stride = parent._stride;
        _offset = parent._offset + start;
        _open = parent._open;
        _high = parent._high;
        _low = parent._low;
        _close = parent._close;
        _volume = parent._volume;
    }

    public IReadOnlyList<DateOnly> Dates { get; }

    /// <summary>Gets each bar's start for intraday bars (plan 17), or null for daily bars.</summary>
    public IReadOnlyList<DateTimeOffset>? BarStartsUtc { get; }

    /// <summary>Gets a value indicating whether the bars are intraday (several per trading day).</summary>
    public bool IsIntraday => BarStartsUtc is not null;

    /// <summary>True when bar <paramref name="t"/> is the last of its trading day in this panel.</summary>
    public bool IsLastOfDay(int t) => t == Periods - 1 || Dates[t + 1] != Dates[t];

    /// <summary>"2026-09-29" for a daily bar, "2026-09-29 09:05" (Stockholm) for an intraday one: for messages.</summary>
    public string Label(int t) =>
        BarStartsUtc is { } starts
            ? MarketTime.ToStockholm(starts[t]).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : Dates[t].ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public IReadOnlyList<PanelInstrument> Instruments { get; }

    /// <summary>Where the bars came from and whether they are point-in-time and survivorship-free.</summary>
    public DataSourceInfo Source { get; }

    /// <summary>Gets T.</summary>
    public int Periods { get; }

    /// <summary>Gets N.</summary>
    public int InstrumentCount => Instruments.Count;

    public bool IsValid(int t, int instrument) => !double.IsNaN(_close[Index(t, instrument)]);

    /// <summary>The bar for the native engine (<see cref="BacktestBar.NoTrading"/> when missing).</summary>
    public BacktestBar Bar(int t, int instrument)
    {
        int k = Index(t, instrument);
        return double.IsNaN(_close[k]) ? BacktestBar.NoTrading : new BacktestBar(_open[k], _high[k], _low[k], _close[k], _volume[k]);
    }

    /// <summary>The first <paramref name="periods"/> bars (a view; used by the leakage check).</summary>
    public MarketPanel Truncate(int periods)
    {
        if (periods < 1 || periods > Periods)
        {
            throw new ArgumentOutOfRangeException(nameof(periods), periods, $"Must be in [1, {Periods}].");
        }

        return new MarketPanel(this, 0, periods);
    }

    /// <summary>The bars dated in [<paramref name="from"/>, <paramref name="to"/>] (a view).</summary>
    public MarketPanel Between(DateOnly from, DateOnly to)
    {
        int start = 0;
        while (start < Periods && Dates[start] < from)
        {
            start++;
        }

        int end = start;
        while (end < Periods && Dates[end] <= to)
        {
            end++;
        }

        if (end == start)
        {
            throw new ArgumentException($"No bars between {from:yyyy-MM-dd} and {to:yyyy-MM-dd} (data covers {Dates[0]:yyyy-MM-dd}..{Dates[^1]:yyyy-MM-dd}).");
        }

        return new MarketPanel(this, start, end - start);
    }

    /// <summary>
    /// Aligns per-instrument daily bars on the union of their dates; a date an instrument has no bar for is invalid
    /// for it. Prices convert from decimal to double (the engine is a model).
    /// </summary>
    public static MarketPanel FromDailyBars(IReadOnlyList<(PanelInstrument Instrument, IReadOnlyList<DailyBar> Bars)> series, DataSourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(series);
        DateOnly[] dates = [.. series.SelectMany(s => s.Bars.Select(b => b.Date)).Distinct().Order()];
        var index = new Dictionary<DateOnly, int>(dates.Length);
        for (int t = 0; t < dates.Length; t++)
        {
            index[dates[t]] = t;
        }

        int n = series.Count, periods = dates.Length;
        double[] open = Nans(n * periods), high = Nans(n * periods), low = Nans(n * periods), close = Nans(n * periods), volume = Nans(n * periods);
        for (int i = 0; i < n; i++)
        {
            foreach (DailyBar b in series[i].Bars)
            {
                int k = (i * periods) + index[b.Date];
                if (!double.IsNaN(close[k]))
                {
                    throw new ArgumentException($"{series[i].Instrument.Symbol}: two bars dated {b.Date:yyyy-MM-dd}.", nameof(series));
                }

                open[k] = (double)b.Open;
                high[k] = (double)b.High;
                low[k] = (double)b.Low;
                close[k] = (double)b.Close;
                volume[k] = b.Volume;
            }
        }

        return new MarketPanel(dates, [.. series.Select(s => s.Instrument)], open, high, low, close, volume, source);
    }

    /// <summary>
    /// Aligns per-instrument intraday bars (plan 17) on the union of their starts; a start an instrument has no bar for
    /// is invalid for it (nothing fills). Each bar's date is its Stockholm trading date.
    /// </summary>
    public static MarketPanel FromIntradayBars(IReadOnlyList<(PanelInstrument Instrument, IReadOnlyList<Bar> Bars)> series, DataSourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(series);
        DateTimeOffset[] starts = [.. series.SelectMany(s => s.Bars.Select(b => b.TimestampUtc)).Distinct().Order()];
        var index = new Dictionary<DateTimeOffset, int>(starts.Length);
        for (int t = 0; t < starts.Length; t++)
        {
            index[starts[t]] = t;
        }

        int n = series.Count, periods = starts.Length;
        double[] open = Nans(n * periods), high = Nans(n * periods), low = Nans(n * periods), close = Nans(n * periods), volume = Nans(n * periods);
        for (int i = 0; i < n; i++)
        {
            foreach (Bar b in series[i].Bars)
            {
                int k = (i * periods) + index[b.TimestampUtc];
                if (!double.IsNaN(close[k]))
                {
                    throw new ArgumentException($"{series[i].Instrument.Symbol}: two bars start at {b.TimestampUtc:O}.", nameof(series));
                }

                open[k] = (double)b.Open;
                high[k] = (double)b.High;
                low[k] = (double)b.Low;
                close[k] = (double)b.Close;
                volume[k] = b.Volume;
            }
        }

        DateOnly[] dates = [.. starts.Select(s => DateOnly.FromDateTime(MarketTime.ToStockholm(s).DateTime))];
        return new MarketPanel(dates, [.. series.Select(s => s.Instrument)], open, high, low, close, volume, source, starts);
    }

    internal ReadOnlySpan<double> OpenColumn(int instrument, int count) => Column(_open, instrument, count);

    internal ReadOnlySpan<double> HighColumn(int instrument, int count) => Column(_high, instrument, count);

    internal ReadOnlySpan<double> LowColumn(int instrument, int count) => Column(_low, instrument, count);

    internal ReadOnlySpan<double> CloseColumn(int instrument, int count) => Column(_close, instrument, count);

    internal ReadOnlySpan<double> VolumeColumn(int instrument, int count) => Column(_volume, instrument, count);

    private ReadOnlySpan<double> Column(double[] data, int instrument, int count)
    {
        if ((uint)instrument >= (uint)InstrumentCount || (uint)count > (uint)Periods)
        {
            throw new ArgumentOutOfRangeException(nameof(instrument), $"Column ({instrument}, {count}) is outside the {Periods} × {InstrumentCount} panel.");
        }

        return data.AsSpan((instrument * _stride) + _offset, count);
    }

    private int Index(int t, int instrument)
    {
        if ((uint)t >= (uint)Periods || (uint)instrument >= (uint)InstrumentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(t), $"Bar ({t}, {instrument}) is outside the {Periods} × {InstrumentCount} panel.");
        }

        return (instrument * _stride) + _offset + t;
    }

    private static bool Positive(double x) => double.IsFinite(x) && x > 0;

    private static double[] Nans(int length)
    {
        var a = new double[length];
        Array.Fill(a, double.NaN);
        return a;
    }
}

/// <summary>A strategy read past the current bar (<see cref="BarWindow"/>): the run is rejected as look-ahead.</summary>
public sealed class LookAheadException(string message) : Exception(message);

/// <summary>
/// What a strategy may see at the close of bar <see cref="Now"/>: bars 0..Now and nothing later. Indexing past
/// <see cref="Now"/> throws <see cref="LookAheadException"/>; the column spans end at <see cref="Now"/>.
/// </summary>
public sealed class BarWindow
{
    private readonly MarketPanel _panel;

    internal BarWindow(MarketPanel panel)
    {
        _panel = panel;
        Now = -1;
    }

    /// <summary>Gets the index of the bar whose close the decision is made at.</summary>
    public int Now { get; private set; }

    /// <summary>Gets the number of visible bars (Now + 1).</summary>
    public int Count => Now + 1;

    public int InstrumentCount => _panel.InstrumentCount;

    public IReadOnlyList<PanelInstrument> Instruments => _panel.Instruments;

    public DateOnly Date(int t) => _panel.Dates[Check(t)];

    /// <summary>Gets a value indicating whether the bars are intraday (plan 17).</summary>
    public bool IsIntraday => _panel.IsIntraday;

    /// <summary>An intraday bar's start (UTC); convert with <c>MarketTime.ToStockholm</c> for the clock time.</summary>
    public DateTimeOffset Time(int t) =>
        _panel.BarStartsUtc is { } starts ? starts[Check(t)] : throw new StrategyException("Daily bars have no start time; use Date(t).");

    public bool IsValid(int instrument, int t) => _panel.IsValid(Check(t), instrument);

    public double Open(int instrument, int t) => _panel.OpenColumn(instrument, Count)[Check(t)];

    public double High(int instrument, int t) => _panel.HighColumn(instrument, Count)[Check(t)];

    public double Low(int instrument, int t) => _panel.LowColumn(instrument, Count)[Check(t)];

    public double Close(int instrument, int t) => _panel.CloseColumn(instrument, Count)[Check(t)];

    public double Volume(int instrument, int t) => _panel.VolumeColumn(instrument, Count)[Check(t)];

    /// <summary>Closes 0..Now of one instrument (NaN where it did not trade).</summary>
    public ReadOnlySpan<double> Closes(int instrument) => _panel.CloseColumn(instrument, Count);

    public ReadOnlySpan<double> Opens(int instrument) => _panel.OpenColumn(instrument, Count);

    public ReadOnlySpan<double> Highs(int instrument) => _panel.HighColumn(instrument, Count);

    public ReadOnlySpan<double> Lows(int instrument) => _panel.LowColumn(instrument, Count);

    public ReadOnlySpan<double> Volumes(int instrument) => _panel.VolumeColumn(instrument, Count);

    internal void MoveTo(int t) => Now = t;

    private int Check(int t)
    {
        if (t > Now)
        {
            throw new LookAheadException($"The strategy read bar {t} at the close of bar {Now}: look-ahead.");
        }

        return t < 0 ? throw new ArgumentOutOfRangeException(nameof(t), t, "Bar index must be >= 0.") : t;
    }
}
