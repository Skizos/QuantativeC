using QuantAnalyst.Analytics.Data;
using QuantAnalyst.Analytics.Reporting;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Portfolio;

/// <summary>Inputs for portfolio optimization.</summary>
public sealed record OptimizeRequest
{
    /// <summary>Gets the instruments' returns.</summary>
    public required ReturnsTable Returns { get; init; }

    /// <summary>Gets the method.</summary>
    public OptimizationMethod Method { get; init; } = OptimizationMethod.MinVariance;

    /// <summary>Gets the covariance estimator.</summary>
    public CovarianceMethod Covariance { get; init; } = CovarianceMethod.LedoitWolf;

    /// <summary>Gets the EWMA decay.</summary>
    public double EwmaLambda { get; init; } = 0.94;

    /// <summary>Gets the risk aversion (mean-variance).</summary>
    public double RiskAversion { get; init; } = 3.0;

    /// <summary>Gets the per-asset lower bound (min-variance / mean-variance).</summary>
    public double MinWeight { get; init; }

    /// <summary>Gets the per-asset upper bound (min-variance / mean-variance).</summary>
    public double MaxWeight { get; init; } = 1.0;

    /// <summary>Gets the periods per year used to annualize.</summary>
    public int PeriodsPerYear { get; init; } = 252;
}

/// <summary>One asset's weight and share of portfolio risk.</summary>
/// <param name="Instrument">Instrument name.</param>
/// <param name="Weight">Portfolio weight.</param>
/// <param name="RiskContribution">w_i (C w)_i / w'Cw.</param>
public sealed record WeightLine(string Instrument, double Weight, double RiskContribution);

/// <summary>Optimization report (in-sample; not a backtest).</summary>
public sealed record OptimizeReport(
    DataLabel Label,
    string Method,
    string CovarianceMethod,
    double Shrinkage,
    IReadOnlyList<WeightLine> Weights,
    double ExpectedReturnAnnual,
    double VolatilityAnnual,
    int Iterations,
    bool Converged,
    double Objective);

/// <summary>Runs the native optimizers on estimated moments.</summary>
public static class PortfolioOptimization
{
    /// <summary>Optimizes weights from sample means and the chosen covariance estimator.</summary>
    public static OptimizeReport Run(QeEngine engine, OptimizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        ReturnsTable table = request.Returns;
        int n = table.Instruments.Count;
        CovarianceResult cov = engine.Covariance(table.Returns, request.Covariance, request.EwmaLambda);
        double[] mean = table.MeanReturns();
        bool bounded = request.Method is OptimizationMethod.MinVariance or OptimizationMethod.MeanVariance;
        double[] lower = bounded ? Enumerable.Repeat(request.MinWeight, n).ToArray() : [];
        double[] upper = bounded ? Enumerable.Repeat(request.MaxWeight, n).ToArray() : [];

        OptimizationResult result = engine.Optimize(
            cov.Covariance,
            new OptimizationOptions { Method = request.Method, RiskAversion = request.RiskAversion },
            request.Method == OptimizationMethod.MeanVariance ? mean : [],
            lower,
            upper);

        double[] w = result.Weights.ToArray();
        var marginal = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                marginal[i] += cov.Covariance[i, j] * w[j];
            }
        }

        double variance = w.Zip(marginal, (a, b) => a * b).Sum();
        var lines = table.Instruments
            .Select((name, i) => new WeightLine(name, w[i], variance > 0 ? w[i] * marginal[i] / variance : 0))
            .ToList();
        double expected = w.Zip(mean, (a, b) => a * b).Sum() * request.PeriodsPerYear;
        return new OptimizeReport(
            DataLabel.For(table),
            request.Method.ToString(),
            request.Covariance.ToString(),
            cov.Shrinkage,
            lines,
            expected,
            Math.Sqrt(Math.Max(variance, 0) * request.PeriodsPerYear),
            result.Iterations,
            result.Converged,
            result.Objective);
    }
}
