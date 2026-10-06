using System.Globalization;

namespace QuantAnalyst.Analytics.Backtesting;

/// <summary>
/// A trading rule. <see cref="Decide"/> is called once per bar, in time order, at the close of bar
/// <c>window.Now</c>; the orders it causes are submitted for the next bar. Decisions must depend only on the window
/// (the runner's leakage check replays the strategy on truncated data and rejects the run if a decision changes).
/// </summary>
public interface IStrategy
{
    /// <summary>
    /// Writes one target per instrument: a fraction of equity (≥ 0, all together ≤ 1; long-only), or NaN to leave
    /// that instrument's position as it is.
    /// </summary>
    void Decide(BarWindow window, Span<double> targets);
}

/// <summary>Creates a fresh strategy for a data set (called again, on truncated data, by the leakage check).</summary>
public delegate IStrategy StrategyFactory(MarketPanel data);

/// <summary>A strategy name and its canonical parameters (what the TrialLedger records).</summary>
public sealed record StrategySpec(string Name, IReadOnlyDictionary<string, string> Parameters)
{
    public string Describe() =>
        Parameters.Count == 0 ? Name : $"{Name}({string.Join(", ", Parameters.Select(p => $"{p.Key}={p.Value}"))})";
}

/// <summary>A strategy the catalog can build: its spec for the ledger and its factory.</summary>
public sealed record StrategyDefinition(StrategySpec Spec, StrategyFactory Factory);

/// <summary>Equal weight in every instrument, bought during the first <c>entry</c> bars and then held.</summary>
public sealed class BuyAndHold(int entryBars = 5) : IStrategy
{
    public void Decide(BarWindow window, Span<double> targets)
    {
        ArgumentNullException.ThrowIfNull(window);
        // Re-sending the target during the entry bars retries limits that did not fill; after that, hold.
        targets.Fill(window.Now < entryBars ? 1.0 / window.InstrumentCount : double.NaN);
    }
}

/// <summary>
/// Long an instrument while its fast simple moving average of valid closes is above the slow one, else flat. Each
/// instrument gets a fixed 1/N slice of equity. Averages are kept incrementally, so a decision is O(N).
/// </summary>
public sealed class MovingAverageCross : IStrategy
{
    private readonly int _fast;
    private readonly int _slow;
    private double[][] _history = [];
    private int[] _count = [];
    private double[] _sumFast = [];
    private double[] _sumSlow = [];
    private int _last = -1;

    public MovingAverageCross(int fast, int slow)
    {
        if (fast < 1 || slow <= fast)
        {
            throw new ArgumentOutOfRangeException(nameof(slow), $"Need 1 <= fast < slow, got fast={fast}, slow={slow}.");
        }

        _fast = fast;
        _slow = slow;
    }

    public void Decide(BarWindow window, Span<double> targets)
    {
        ArgumentNullException.ThrowIfNull(window);
        int n = window.InstrumentCount;
        if (_history.Length != n)
        {
            _history = [.. Enumerable.Range(0, n).Select(_ => new double[_slow])];
            _count = new int[n];
            _sumFast = new double[n];
            _sumSlow = new double[n];
        }

        if (window.Now != _last + 1)
        {
            throw new InvalidOperationException($"MovingAverageCross must see every bar in order (last {_last}, now {window.Now}).");
        }

        _last = window.Now;
        double slice = 1.0 / n;
        for (int i = 0; i < n; i++)
        {
            double close = window.Close(i, window.Now);
            if (!double.IsNaN(close))
            {
                Push(i, close);
            }

            targets[i] = _count[i] < _slow
                ? 0.0
                : (_sumFast[i] / _fast) > (_sumSlow[i] / _slow) ? slice : 0.0;
        }
    }

    private void Push(int i, double close)
    {
        // Ring buffer of the last `slow` valid closes; the value leaving the fast window is `fast` pushes back.
        double[] ring = _history[i];
        int c = _count[i];
        if (c >= _fast)
        {
            _sumFast[i] -= ring[(c - _fast) % _slow];
        }

        if (c >= _slow)
        {
            _sumSlow[i] -= ring[c % _slow];
        }

        ring[c % _slow] = close;
        _sumFast[i] += close;
        _sumSlow[i] += close;
        _count[i] = c + 1;
    }
}

/// <summary>
/// Random long-only targets, redrawn every <c>rebalance</c> bars: each instrument is held with probability
/// <c>p</c>, equal weight among the held ones. A null model for the deflation tests (its skill is zero by design).
/// </summary>
public sealed class RandomTargets(ulong seed, int rebalance = 21, double p = 0.5) : IStrategy
{
    private readonly SeededRandom _rng = new(seed);
    private double[] _current = [];

    public void Decide(BarWindow window, Span<double> targets)
    {
        ArgumentNullException.ThrowIfNull(window);
        int n = window.InstrumentCount;
        if (_current.Length != n || window.Now % rebalance == 0)
        {
            _current = new double[n];
            int held = 0;
            for (int i = 0; i < n; i++)
            {
                if (_rng.NextDouble() < p)
                {
                    _current[i] = 1;
                    held++;
                }
            }

            for (int i = 0; i < n; i++)
            {
                _current[i] = held == 0 ? 0 : _current[i] / held;
            }
        }

        _current.CopyTo(targets);
    }
}

/// <summary>One strategy parameter, for forms and help: its key, its default (null = required) and what it means.</summary>
public sealed record StrategyParameter(string Key, string? Default, string Description);

/// <summary>The built-in strategies by name, with parameter parsing and validation. Defaults are written into the spec, so the ledger shows every effective value.</summary>
public static class StrategyCatalog
{
    public static IReadOnlyList<string> Names { get; } = ["buy-and-hold", "ma-cross", "inverse-vol", "risk-parity", "random-targets"];

    /// <summary>A one-line description of a strategy, for help and the Windows app.</summary>
    public static string Summary(string name) => name switch
    {
        "buy-and-hold" => "Equal weight in every instrument, bought during the first bars and then held.",
        "ma-cross" => "Holds an instrument while its fast moving average is above its slow one; a fixed equal slice each.",
        "inverse-vol" => "Always invested; each share weighted by 1 / its recent volatility, so calm shares get more. Rebalanced monthly.",
        "risk-parity" => "Always invested; each share contributes the same risk, counting how they move together (the native engine). Rebalanced monthly.",
        "random-targets" => "Random long-only weights: the null model, to see what luck alone looks like.",
        _ => throw new ArgumentException($"Unknown strategy '{name}'. Known: {string.Join(", ", Names)}."),
    };

    /// <summary>The parameters <see cref="Create"/> accepts for a strategy, in order (a test keeps the two in step).</summary>
    public static IReadOnlyList<StrategyParameter> ParametersOf(string name) => name switch
    {
        "buy-and-hold" => [new("entry", "5", "bars over which the position is bought")],
        "ma-cross" => [new("fast", null, "fast moving average, in bars"), new("slow", null, "slow moving average, in bars (more than fast)")],
        "inverse-vol" => [new("lookback", "63", "days of returns behind each volatility"), new("rebalance", "21", "bars between new weights")],
        "risk-parity" => [new("lookback", "126", "days of returns behind the covariance"), new("rebalance", "21", "bars between new weights")],
        "random-targets" =>
        [
            new("seed", null, "random seed (any whole number)"), new("rebalance", "21", "bars between new random weights"),
            new("p", "0.5", "chance of holding each instrument"),
        ],
        _ => throw new ArgumentException($"Unknown strategy '{name}'. Known: {string.Join(", ", Names)}."),
    };

    /// <summary>Builds a strategy from its CLI name and parameters (unknown names or parameters are errors).</summary>
    public static StrategyDefinition Create(string name, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var given = new Dictionary<string, string>(parameters, StringComparer.Ordinal);
        var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal);

        int Int(string key, int? fallback)
        {
            string? text = Take(key);
            int value = text is null
                ? fallback ?? throw new ArgumentException($"{name}: parameter '{key}' is required.")
                : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                    ? v
                    : throw new ArgumentException($"{name}: parameter '{key}' must be an integer, got '{text}'.");
            canonical[key] = value.ToString(CultureInfo.InvariantCulture);
            return value;
        }

        ulong UInt64(string key)
        {
            string text = Take(key) ?? throw new ArgumentException($"{name}: parameter '{key}' is required.");
            ulong value = ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong v)
                ? v
                : throw new ArgumentException($"{name}: parameter '{key}' must be a non-negative integer, got '{text}'.");
            canonical[key] = value.ToString(CultureInfo.InvariantCulture);
            return value;
        }

        double Double(string key, double fallback)
        {
            string? text = Take(key);
            double value = text is null
                ? fallback
                : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
                    ? v
                    : throw new ArgumentException($"{name}: parameter '{key}' must be a number, got '{text}'.");
            canonical[key] = value.ToString("R", CultureInfo.InvariantCulture);
            return value;
        }

        string? Take(string key) => given.Remove(key, out string? v) ? v : null;

        StrategyFactory factory;
        switch (name)
        {
            case "buy-and-hold":
                {
                    int entry = Int("entry", 5);
                    if (entry < 1)
                    {
                        throw new ArgumentException("buy-and-hold: entry must be >= 1.");
                    }

                    factory = _ => new BuyAndHold(entry);
                    break;
                }

            case "ma-cross":
                {
                    int fast = Int("fast", null), slow = Int("slow", null);
                    if (fast < 1 || slow <= fast)
                    {
                        throw new ArgumentException($"ma-cross: need 1 <= fast < slow, got fast={fast}, slow={slow}.");
                    }

                    factory = _ => new MovingAverageCross(fast, slow);
                    break;
                }

            case "inverse-vol":
            case "risk-parity":
                {
                    bool parity = name == "risk-parity";
                    int lookback = Int("lookback", parity ? 126 : 63), rebalance = Int("rebalance", 21);
                    if (lookback is < 20 or > 1_000 || rebalance is < 1 or > 252)
                    {
                        throw new ArgumentException($"{name}: need 20 <= lookback <= 1000 and 1 <= rebalance <= 252, got lookback={lookback}, rebalance={rebalance}.");
                    }

                    factory = parity ? _ => new RiskParity(lookback, rebalance) : _ => new InverseVolatility(lookback, rebalance);
                    break;
                }

            case "random-targets":
                {
                    ulong seed = UInt64("seed");
                    int rebalance = Int("rebalance", 21);
                    double p = Double("p", 0.5);
                    if (rebalance < 1 || p is < 0 or > 1)
                    {
                        throw new ArgumentException("random-targets: need rebalance >= 1 and 0 <= p <= 1.");
                    }

                    factory = _ => new RandomTargets(seed, rebalance, p);
                    break;
                }

            default:
                throw new ArgumentException($"Unknown strategy '{name}'. Known: {string.Join(", ", Names)}.");
        }

        if (given.Count > 0)
        {
            throw new ArgumentException($"{name}: unknown parameter(s) {string.Join(", ", given.Keys.Order(StringComparer.Ordinal))}.");
        }

        return new StrategyDefinition(new StrategySpec(name, canonical), factory);
    }
}

/// <summary>
/// Paper and live use of a strategy (Phase 6): the decision at the close of the panel's last bar, after replaying every
/// earlier bar in order (stateful strategies such as <see cref="MovingAverageCross"/> need the whole history). The
/// window is the same look-ahead-safe <see cref="BarWindow"/> the backtest uses.
/// <para>
/// An instrument the strategy leaves as "hold" (NaN) on the last bar gets the last target it set during the replay (plan
/// 27 review): in a backtest "hold" keeps the position that target built, but a Paper or live book did not live through
/// the replay. Without this, <see cref="BuyAndHold"/> on a year of history (targets only in its first bars) would never
/// buy into a new book. An instrument never given a target stays NaN.
/// </para>
/// </summary>
public static class StrategyReplay
{
    /// <summary>Creates the strategy for <paramref name="data"/>, decides at its last bar, and disposes the strategy if it owns anything (plan 27).</summary>
    public static double[] DecideAtLastBar(MarketPanel data, StrategyFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        IStrategy strategy = factory(data);
        using IDisposable? owned = strategy as IDisposable;
        return DecideAtLastBar(data, strategy);
    }

    public static double[] DecideAtLastBar(MarketPanel data, IStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(strategy);
        if (data.Periods == 0)
        {
            throw new ArgumentException("The panel has no bars.", nameof(data));
        }

        var window = new BarWindow(data);
        var targets = new double[data.InstrumentCount];
        var lastSet = new double[data.InstrumentCount];
        Array.Fill(lastSet, double.NaN);
        for (int t = 0; t < data.Periods; t++)
        {
            window.MoveTo(t);
            Array.Fill(targets, double.NaN);
            strategy.Decide(window, targets);
            for (int i = 0; i < targets.Length; i++)
            {
                if (!double.IsNaN(targets[i]))
                {
                    lastSet[i] = targets[i];
                }
            }
        }

        targets = lastSet;

        double sum = 0;
        foreach (double w in targets)
        {
            if (double.IsNaN(w))
            {
                continue;
            }

            if (!double.IsFinite(w) || w < 0)
            {
                throw new StrategyException($"Target weights must be finite and >= 0 (long-only); got {w} at {data.Dates[^1]:yyyy-MM-dd}.");
            }

            sum += w;
        }

        return sum <= 1 + 1e-9 ? targets : throw new StrategyException($"Target weights sum to {sum:0.######} > 1 at {data.Dates[^1]:yyyy-MM-dd} (no leverage).");
    }
}
