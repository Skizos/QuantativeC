using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Backtesting;

/// <summary>
/// Plan 27: the shared shape of the risk-weighted strategies. Every <c>rebalance</c> bars (and on each bar until the
/// first weights exist) the weights are computed from the last <c>lookback</c> daily returns; on every bar they are
/// stated in full (not NaN), so a fresh book is invested at once and the no-trade band keeps drift from trading.
/// Fully invested among the instruments with enough history; the others get 0.
/// </summary>
public abstract class RiskWeightedStrategy : IStrategy
{
    private double[] _weights = [];

    protected RiskWeightedStrategy(int lookback, int rebalance)
    {
        if (lookback < 2 || rebalance < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lookback), $"Need lookback >= 2 and rebalance >= 1, got {lookback} and {rebalance}.");
        }

        Lookback = lookback;
        Rebalance = rebalance;
    }

    public int Lookback { get; }

    public int Rebalance { get; }

    public void Decide(BarWindow window, Span<double> targets)
    {
        ArgumentNullException.ThrowIfNull(window);
        int n = window.InstrumentCount;
        if (_weights.Length != n || window.Now % Rebalance == 0 || Array.TrueForAll(_weights, w => w == 0))
        {
            _weights = Normalized(Compute(window, n));
        }

        _weights.CopyTo(targets);
    }

    /// <summary>Raw non-negative weights from the window (any scale; 0 for an instrument left out).</summary>
    protected abstract double[] Compute(BarWindow window, int instruments);

    /// <summary>The instrument's last <see cref="Lookback"/> simple daily returns from consecutive valid closes; null without enough.</summary>
    protected double[]? Returns(BarWindow window, int instrument)
    {
        ArgumentNullException.ThrowIfNull(window);
        var closes = new List<double>(Lookback + 1);
        for (int t = window.Now; t >= 0 && closes.Count <= Lookback; t--)
        {
            double close = window.Close(instrument, t);
            if (!double.IsNaN(close) && close > 0)
            {
                closes.Add(close);
            }
        }

        if (closes.Count <= Lookback)
        {
            return null;
        }

        closes.Reverse();
        var returns = new double[Lookback];
        for (int k = 0; k < Lookback; k++)
        {
            returns[k] = closes[k + 1] / closes[k] - 1;
        }

        return returns;
    }

    protected static double StdDev(double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        double mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1));
    }

    private static double[] Normalized(double[] raw)
    {
        double sum = raw.Where(w => double.IsFinite(w) && w > 0).Sum();
        return [.. raw.Select(w => sum > 0 && double.IsFinite(w) && w > 0 ? w / sum : 0.0)];
    }
}

/// <summary>
/// Plan 27: weight ∝ 1 / volatility of the last <c>lookback</c> daily returns: a calm share gets more, a wild one less,
/// so each contributes about the same day-to-day swing (ignoring how they move together).
/// </summary>
public sealed class InverseVolatility(int lookback = 63, int rebalance = 21) : RiskWeightedStrategy(lookback, rebalance)
{
    protected override double[] Compute(BarWindow window, int instruments)
    {
        var raw = new double[instruments];
        for (int i = 0; i < instruments; i++)
        {
            if (Returns(window, i) is { } r && StdDev(r) is var sd and > 0)
            {
                raw[i] = 1 / sd;
            }
        }

        return raw;
    }
}

/// <summary>
/// Plan 27: equal risk contribution (risk parity): each share contributes the same to the portfolio's variance, taking
/// into account how they move together. The covariance of the last <c>lookback</c> daily returns (Ledoit-Wolf
/// shrinkage) and the solve are the native engine's (<c>qe_covariance</c>, <c>qe_optimize</c> with
/// <c>QE_OPT_RISK_PARITY</c>). Shares need a close on every one of those days; without convergence the weights fall
/// back to inverse volatility. Disposable: it owns a native engine, created at the first solve.
/// </summary>
public sealed class RiskParity(int lookback = 126, int rebalance = 21) : RiskWeightedStrategy(lookback, rebalance), IDisposable
{
    private QeEngine? _engine;

    public void Dispose() => _engine?.Dispose();

    protected override double[] Compute(BarWindow window, int instruments)
    {
        var raw = new double[instruments];
        if (window.Now < Lookback)
        {
            return raw;
        }

        // The shares with a close on each of the last lookback + 1 bars: their returns line up day by day.
        int first = window.Now - Lookback;
        int[] eligible = [.. Enumerable.Range(0, instruments).Where(i => Enumerable.Range(first, Lookback + 1).All(t => window.Close(i, t) is var c && !double.IsNaN(c) && c > 0))];
        if (eligible.Length <= 1)
        {
            foreach (int i in eligible)
            {
                raw[i] = 1;
            }

            return raw;
        }

        var returns = new DenseMatrix(Lookback, eligible.Length);
        for (int k = 0; k < Lookback; k++)
        {
            for (int j = 0; j < eligible.Length; j++)
            {
                returns[k, j] = window.Close(eligible[j], first + k + 1) / window.Close(eligible[j], first + k) - 1;
            }
        }

        _engine ??= QeEngine.Create();
        CovarianceResult cov = _engine.Covariance(returns, CovarianceMethod.LedoitWolf);
        OptimizationResult solved = _engine.Optimize(cov.Covariance, new OptimizationOptions { Method = OptimizationMethod.RiskParity });
        for (int j = 0; j < eligible.Length; j++)
        {
            raw[eligible[j]] = solved.Converged ? solved.Weights[j] : 1 / Math.Sqrt(cov.Covariance[j, j]);
        }

        return raw;
    }
}
