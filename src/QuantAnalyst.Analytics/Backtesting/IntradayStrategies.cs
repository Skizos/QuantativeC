using System.Globalization;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Analytics.Backtesting;

/// <summary>
/// The session an intraday strategy trades in (plan 17, ADR 0006): each day's open and close from the Nasdaq Stockholm
/// calendar (half days close early), and the bar length, so a decision knows the clock time of the close it is made at.
/// The calendar is known in advance, so reading it is not look-ahead.
/// </summary>
public sealed class IntradayClock
{
    private readonly MarketCalendar _calendar;
    private readonly Dictionary<DateOnly, (DateTimeOffset, DateTimeOffset)> _sessions = [];
    private readonly Dictionary<DateOnly, TimeSpan> _days;

    /// <param name="dayBarLengths">
    /// Days whose bars are longer than <paramref name="barLength"/> (plan 17 A2b: 10-minute bars on a day no 5-minute bars
    /// were collected for); every bar of such a day has that length.
    /// </param>
    public IntradayClock(MarketCalendar calendar, TimeSpan barLength, IReadOnlyDictionary<DateOnly, TimeSpan>? dayBarLengths = null)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        foreach (TimeSpan length in (dayBarLengths?.Values ?? []).Append(barLength))
        {
            if (length <= TimeSpan.Zero || length > TimeSpan.FromHours(1))
            {
                throw new ArgumentOutOfRangeException(nameof(barLength), length, "An intraday bar is longer than zero and at most an hour.");
            }
        }

        _calendar = calendar;
        BarLength = barLength;
        _days = dayBarLengths is null ? [] : new Dictionary<DateOnly, TimeSpan>(dayBarLengths);
    }

    /// <summary>Gets the length of one bar (a decision is made at a bar's start plus this), except on <see cref="BarLengthOn"/>'s own days.</summary>
    public TimeSpan BarLength { get; }

    /// <summary>The length of the bars of <paramref name="date"/>.</summary>
    public TimeSpan BarLengthOn(DateOnly date) => _days.GetValueOrDefault(date, BarLength);

    /// <summary>The session of <paramref name="date"/> in UTC. Bars on a day the calendar has closed are refused, not guessed.</summary>
    public (DateTimeOffset OpenUtc, DateTimeOffset CloseUtc) Session(DateOnly date)
    {
        if (_sessions.TryGetValue(date, out (DateTimeOffset, DateTimeOffset) known))
        {
            return known;
        }

        TradingDay day;
        try
        {
            day = _calendar.Classify(date);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new StrategyException(ex.Message);
        }

        if (!day.IsTradingDay)
        {
            throw new StrategyException($"There are bars on {date:yyyy-MM-dd}, which the {_calendar.Mic} calendar has {(day.Kind == TradingDayKind.Weekend ? "as a weekend" : $"closed ({day.Name})")}.");
        }

        (DateTimeOffset, DateTimeOffset) session = (_calendar.ToUtc(date, day.Open!.Value), _calendar.ToUtc(date, day.Close!.Value));
        _sessions[date] = session;
        return session;
    }
}

/// <summary>
/// The shared frame of the intraday strategies (plan 17 step A5):
/// <list type="bullet">
/// <item>every bar is seen in order; each new trading day starts from nothing (state never carries overnight)</item>
/// <item>a decision is made at a bar's close, clock time start + bar length, and trades at the next bar's open</item>
/// <item>each name is waiting, in, or done for the day: at most one entry per name per day</item>
/// <item>a name in the position gets <c>weight</c> of equity; at most ⌊1 / weight⌋ names are in at once, and when more
/// want in on the same bar the strongest signal goes first (ties: the panel's order)</item>
/// <item>from <c>exit</c> minutes before the close everything goes flat, and nothing enters (ADR 0006 D4: flat by 17:20;
/// R16 stops orders then)</item>
/// </list>
/// A name in the position keeps its target, so an entry that did not fill (no trade in the next bar) is retried.
/// </summary>
public abstract class IntradayStrategy : IStrategy
{
    private readonly IntradayClock _clock;
    private readonly List<(double Strength, int Instrument)> _candidates = [];
    private int _last = -1;
    private DateOnly _day;
    private Phase[] _phase = [];

    protected IntradayStrategy(IntradayClock clock, double weight, int exitMinutes)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!double.IsFinite(weight) || weight <= 0 || weight > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "weight must be in (0, 1].");
        }

        _clock = clock;
        Weight = weight;
        Exit = TimeSpan.FromMinutes(exitMinutes);
    }

    private enum Phase
    {
        Waiting,
        In,
        Done,
    }

    /// <summary>Gets the share of equity one position gets.</summary>
    protected double Weight { get; }

    /// <summary>Gets how long before the close everything is flat.</summary>
    protected TimeSpan Exit { get; }

    /// <summary>Gets today's session open (UTC).</summary>
    protected DateTimeOffset OpenUtc { get; private set; }

    /// <summary>Gets today's session close (UTC).</summary>
    protected DateTimeOffset CloseUtc { get; private set; }

    public void Decide(BarWindow window, Span<double> targets)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsIntraday)
        {
            throw new StrategyException($"{GetType().Name} trades intraday bars; run it with 'qa intraday backtest'.");
        }

        if (window.Now != _last + 1)
        {
            throw new InvalidOperationException($"{GetType().Name} must see every bar in order (last {_last}, now {window.Now}).");
        }

        _last = window.Now;
        int n = window.InstrumentCount;
        DateOnly date = window.Date(window.Now);
        if (_phase.Length != n || date != _day)
        {
            _day = date;
            (OpenUtc, CloseUtc) = _clock.Session(date);
            _phase = new Phase[n];
            StartDay(n);
        }

        DateTimeOffset barStart = window.Time(window.Now);
        DateTimeOffset barEnd = barStart + _clock.BarLengthOn(date);
        bool closing = barEnd >= CloseUtc - Exit;
        _candidates.Clear();
        int held = 0;
        for (int i = 0; i < n; i++)
        {
            if (closing)
            {
                _phase[i] = Phase.Done;
            }

            Observe(window, i, barStart, barEnd);
            if (_phase[i] == Phase.In && ShouldLeave(window, i))
            {
                _phase[i] = Phase.Done;
            }

            if (_phase[i] == Phase.In)
            {
                held++;
            }
            else if (_phase[i] == Phase.Waiting && window.IsValid(i, window.Now) && Signal(window, i, barStart, barEnd) is { } strength)
            {
                _candidates.Add((strength, i));
            }
        }

        int slots = Slots(n) - held;
        foreach ((double _, int i) in _candidates.OrderByDescending(c => c.Strength).ThenBy(c => c.Instrument))
        {
            if (slots-- <= 0)
            {
                break;
            }

            _phase[i] = Phase.In;
        }

        double w = PositionWeight(n);
        for (int i = 0; i < n; i++)
        {
            targets[i] = _phase[i] == Phase.In ? w : 0.0;
        }
    }

    /// <summary>How many names may be in at once: ⌊1 / weight⌋.</summary>
    protected virtual int Slots(int instruments) => (int)Math.Floor((1 / Weight) + 1e-9);

    /// <summary>The weight of one position.</summary>
    protected virtual double PositionWeight(int instruments) => Weight;

    /// <summary>A new trading day begins: forget yesterday.</summary>
    protected abstract void StartDay(int instruments);

    /// <summary>Every bar of every name, in order, before any decision (for ranges and first-hour returns).</summary>
    protected abstract void Observe(BarWindow window, int instrument, DateTimeOffset barStart, DateTimeOffset barEnd);

    /// <summary>A reason to leave a position before the exit time (a stop); false by default.</summary>
    protected virtual bool ShouldLeave(BarWindow window, int instrument) => false;

    /// <summary>A name that is waiting wants in at this bar's close: its strength, or null.</summary>
    protected abstract double? Signal(BarWindow window, int instrument, DateTimeOffset barStart, DateTimeOffset barEnd);

    protected static void RequireGrid(TimeSpan span, TimeSpan barLength, string what)
    {
        if (span.Ticks % barLength.Ticks != 0)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"{what} ({span.TotalMinutes:0} minutes) must be a whole number of {barLength.TotalMinutes:0}-minute bars."));
        }
    }
}

/// <summary>
/// <c>orb-long</c>, the candidate (ADR 0006): the opening range is the high and low of the bars in the <c>range</c>
/// minutes that start <c>skip</c> minutes after the open (09:05 by default, after the opening auction's bar). A close
/// above the range high × (1 + <c>buffer</c> bps) buys; a close below the range low sells (the stop, at a bar's close,
/// not inside it); everything is out <c>exit</c> minutes before the close. Long only; one entry per name per day.
/// </summary>
public sealed class OpeningRangeBreakout : IntradayStrategy
{
    private readonly TimeSpan _skip;
    private readonly TimeSpan _range;
    private readonly double _buffer;
    private double[] _high = [];
    private double[] _low = [];

    public OpeningRangeBreakout(IntradayClock clock, int rangeMinutes, int skipMinutes, double bufferBps, int exitMinutes, double weight)
        : base(clock, weight, exitMinutes)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _skip = TimeSpan.FromMinutes(skipMinutes);
        _range = TimeSpan.FromMinutes(rangeMinutes);
        _buffer = bufferBps / 10_000;
        RequireGrid(_skip, clock.BarLength, "skip");
        RequireGrid(_range, clock.BarLength, "range");
    }

    protected override void StartDay(int instruments)
    {
        _high = new double[instruments];
        _low = new double[instruments];
        Array.Fill(_high, double.NaN);
        Array.Fill(_low, double.NaN);
    }

    protected override void Observe(BarWindow window, int instrument, DateTimeOffset barStart, DateTimeOffset barEnd)
    {
        DateTimeOffset from = OpenUtc + _skip;
        if (barStart >= from && barEnd <= from + _range && window.IsValid(instrument, window.Now))
        {
            double high = window.High(instrument, window.Now), low = window.Low(instrument, window.Now);
            _high[instrument] = double.IsNaN(_high[instrument]) ? high : Math.Max(_high[instrument], high);
            _low[instrument] = double.IsNaN(_low[instrument]) ? low : Math.Min(_low[instrument], low);
        }
    }

    protected override bool ShouldLeave(BarWindow window, int instrument) =>
        window.IsValid(instrument, window.Now) && window.Close(instrument, window.Now) < _low[instrument];

    protected override double? Signal(BarWindow window, int instrument, DateTimeOffset barStart, DateTimeOffset barEnd)
    {
        if (barStart < OpenUtc + _skip + _range || double.IsNaN(_high[instrument]))
        {
            return null; // the range is not complete yet, or the share did not trade in it
        }

        double close = window.Close(instrument, window.Now);
        double trigger = _high[instrument] * (1 + _buffer);
        return close > trigger ? (close / _high[instrument]) - 1 : null;
    }
}

/// <summary>
/// <c>late-momentum</c>, a control (ADR 0006): if a name's first <c>lookback</c> minutes returned more than
/// <c>threshold</c> bps (from the day's first open to the close at the open + lookback), it is bought <c>entry</c>
/// minutes before the close and sold <c>exit</c> minutes before it (16:30 and 17:15 by default).
/// </summary>
public sealed class LateMomentum : IntradayStrategy
{
    private readonly TimeSpan _lookback;
    private readonly TimeSpan _entry;
    private readonly double _threshold;
    private double[] _firstOpen = [];
    private double[] _lastClose = [];

    public LateMomentum(IntradayClock clock, int lookbackMinutes, double thresholdBps, int entryMinutes, int exitMinutes, double weight)
        : base(clock, weight, exitMinutes)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _lookback = TimeSpan.FromMinutes(lookbackMinutes);
        _entry = TimeSpan.FromMinutes(entryMinutes);
        _threshold = thresholdBps / 10_000;
        RequireGrid(_lookback, clock.BarLength, "lookback");
        if (entryMinutes <= exitMinutes)
        {
            throw new ArgumentException($"late-momentum: entry ({entryMinutes} minutes before the close) must come before exit ({exitMinutes}).");
        }
    }

    protected override void StartDay(int instruments)
    {
        _firstOpen = new double[instruments];
        _lastClose = new double[instruments];
        Array.Fill(_firstOpen, double.NaN);
        Array.Fill(_lastClose, double.NaN);
    }

    protected override void Observe(BarWindow window, int instrument, DateTimeOffset barStart, DateTimeOffset barEnd)
    {
        if (barEnd <= OpenUtc + _lookback && window.IsValid(instrument, window.Now))
        {
            if (double.IsNaN(_firstOpen[instrument]))
            {
                _firstOpen[instrument] = window.Open(instrument, window.Now);
            }

            _lastClose[instrument] = window.Close(instrument, window.Now);
        }
    }

    protected override double? Signal(BarWindow window, int instrument, DateTimeOffset barStart, DateTimeOffset barEnd)
    {
        if (barEnd < CloseUtc - _entry || double.IsNaN(_firstOpen[instrument]))
        {
            return null;
        }

        double firstHour = (_lastClose[instrument] / _firstOpen[instrument]) - 1;
        return firstHour > _threshold ? firstHour : null;
    }
}

/// <summary>
/// <c>open-close</c>, the baseline (ADR 0006): every name is bought <c>entry</c> minutes after the open and sold
/// <c>exit</c> minutes before the close (09:10 and 17:10 by default), each with min(weight, 1/N) so all fit. A strategy
/// that cannot beat it after costs has found nothing.
/// </summary>
public sealed class OpenClose : IntradayStrategy
{
    private readonly TimeSpan _entry;

    public OpenClose(IntradayClock clock, int entryMinutes, int exitMinutes, double weight)
        : base(clock, weight, exitMinutes) => _entry = TimeSpan.FromMinutes(entryMinutes);

    protected override int Slots(int instruments) => instruments;

    protected override double PositionWeight(int instruments) => Math.Min(Weight, 1.0 / instruments);

    protected override void StartDay(int instruments)
    {
    }

    protected override void Observe(BarWindow window, int instrument, DateTimeOffset barStart, DateTimeOffset barEnd)
    {
    }

    protected override double? Signal(BarWindow window, int instrument, DateTimeOffset barStart, DateTimeOffset barEnd) =>
        barEnd >= OpenUtc + _entry ? 0.0 : null;
}

/// <summary>
/// The intraday strategies by name (plan 17 step A5), apart from <see cref="StrategyCatalog"/>: they need a session
/// clock, trade only intraday bars, and are never offered to <c>qa paper strategy</c> (Phase B comes after a go).
/// Defaults are written into the spec, so the ledger shows every effective value.
/// </summary>
public static class IntradayStrategyCatalog
{
    public static IReadOnlyList<string> Names { get; } = ["orb-long", "late-momentum", "open-close"];

    public static string Summary(string name) => name switch
    {
        "orb-long" => "Opening-range breakout, long only: buys a close above the first minutes' high, stops at their low, out before the close.",
        "late-momentum" => "Control: buys late in the day when the first hour was up, out before the close.",
        "open-close" => "Baseline: buys every name after the open and sells before the close, every day.",
        _ => throw new ArgumentException($"Unknown intraday strategy '{name}'. Known: {string.Join(", ", Names)}."),
    };

    public static IReadOnlyList<StrategyParameter> ParametersOf(string name) => name switch
    {
        "orb-long" =>
        [
            new("range", "15", "opening range, in minutes (5, 15 or 30 in plan 17)"),
            new("skip", "5", "minutes after the open before the range starts (5: after the opening auction's bar)"),
            new("buffer", "0", "how far above the range high a close must be, in bps"),
            new("exit", "20", "minutes before the close when everything is sold (at least 10: R16 stops orders then)"),
            new("weight", "0.1", "share of equity per position; at most 1/weight names at once"),
        ],
        "late-momentum" =>
        [
            new("lookback", "60", "minutes after the open whose return decides"),
            new("threshold", "0", "how much the first minutes must be up, in bps"),
            new("entry", "60", "minutes before the close when it buys"),
            new("exit", "15", "minutes before the close when it sells (at least 10)"),
            new("weight", "0.1", "share of equity per position; at most 1/weight names at once"),
        ],
        "open-close" =>
        [
            new("entry", "10", "minutes after the open when it buys"),
            new("exit", "20", "minutes before the close when it sells (at least 10)"),
            new("weight", "0.1", "share of equity per name, at most 1/N"),
        ],
        _ => throw new ArgumentException($"Unknown intraday strategy '{name}'. Known: {string.Join(", ", Names)}."),
    };

    /// <summary>Builds a strategy from its name and parameters (unknown names or parameters are errors).</summary>
    public static StrategyDefinition Create(string name, IReadOnlyDictionary<string, string> parameters, IntradayClock clock)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(clock);
        if (!Names.Contains(name))
        {
            throw new ArgumentException($"Unknown intraday strategy '{name}'. Known: {string.Join(", ", Names)}.");
        }

        var given = new Dictionary<string, string>(parameters, StringComparer.Ordinal);
        var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal);

        int Minutes(string key, int min, int max)
        {
            string text = Take(key);
            int value = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v
                : throw new ArgumentException($"{name}: parameter '{key}' must be a whole number of minutes, got '{text}'.");
            if (value < min || value > max)
            {
                throw new ArgumentException($"{name}: {key} must be {min}..{max} minutes, got {value}.");
            }

            canonical[key] = value.ToString(CultureInfo.InvariantCulture);
            return value;
        }

        double Number(string key, double min, double max)
        {
            string text = Take(key);
            double value = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
                ? v
                : throw new ArgumentException($"{name}: parameter '{key}' must be a number, got '{text}'.");
            if (value < min || value > max)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{name}: {key} must be in [{min}, {max}], got {value}."));
            }

            canonical[key] = value.ToString("R", CultureInfo.InvariantCulture);
            return value;
        }

        string Take(string key) =>
            given.Remove(key, out string? v) ? v : ParametersOf(name).First(p => p.Key == key).Default!;

        // R16 stops Stockholm orders 10 minutes before the close; nothing may be left for the auction.
        const int MinExit = 10, Day = 8 * 60;
        StrategyFactory factory;
        switch (name)
        {
            case "orb-long":
                {
                    int range = Minutes("range", 1, 120), skip = Minutes("skip", 0, 60);
                    double buffer = Number("buffer", 0, 500);
                    int exit = Minutes("exit", MinExit, Day);
                    double weight = Number("weight", 0.01, 1);
                    _ = new OpeningRangeBreakout(clock, range, skip, buffer, exit, weight); // validates the grid now
                    factory = _ => new OpeningRangeBreakout(clock, range, skip, buffer, exit, weight);
                    break;
                }

            case "late-momentum":
                {
                    int lookback = Minutes("lookback", 1, Day);
                    double threshold = Number("threshold", -500, 500);
                    int entry = Minutes("entry", MinExit + 1, Day), exit = Minutes("exit", MinExit, Day);
                    double weight = Number("weight", 0.01, 1);
                    _ = new LateMomentum(clock, lookback, threshold, entry, exit, weight);
                    factory = _ => new LateMomentum(clock, lookback, threshold, entry, exit, weight);
                    break;
                }

            default:
                {
                    int entry = Minutes("entry", 0, Day), exit = Minutes("exit", MinExit, Day);
                    double weight = Number("weight", 0.01, 1);
                    factory = _ => new OpenClose(clock, entry, exit, weight);
                    break;
                }
        }

        if (given.Count > 0)
        {
            throw new ArgumentException($"{name}: unknown parameter(s) {string.Join(", ", given.Keys.Order(StringComparer.Ordinal))}.");
        }

        return new StrategyDefinition(new StrategySpec(name, canonical), factory);
    }
}
