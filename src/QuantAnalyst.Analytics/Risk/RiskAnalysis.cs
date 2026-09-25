using QuantAnalyst.Analytics.Data;
using QuantAnalyst.Analytics.Reporting;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Risk;

/// <summary>Inputs for a portfolio risk report.</summary>
public sealed record RiskRequest
{
    /// <summary>Gets the portfolio instruments' returns (simple returns aggregate exactly).</summary>
    public required ReturnsTable Returns { get; init; }

    /// <summary>Gets the weights aligned with <see cref="ReturnsTable.Instruments"/>; null = equal weights.</summary>
    public IReadOnlyList<double>? Weights { get; init; }

    /// <summary>Gets the VaR/ES confidence level.</summary>
    public double Confidence { get; init; } = 0.99;

    /// <summary>Gets the covariance estimator for parametric and Monte Carlo VaR.</summary>
    public CovarianceMethod Covariance { get; init; } = CovarianceMethod.Sample;

    /// <summary>Gets the EWMA decay.</summary>
    public double EwmaLambda { get; init; } = 0.94;

    /// <summary>Gets the Monte Carlo path count.</summary>
    public long MonteCarloPaths { get; init; } = 100_000;

    /// <summary>Gets the Monte Carlo seed; 0 = engine seed.</summary>
    public ulong Seed { get; init; }

    /// <summary>Gets the portfolio value in <see cref="DataLabel.Currency"/>.</summary>
    public double PortfolioValue { get; init; } = 1_000_000;

    /// <summary>Gets the index returns (same dates) used for beta-based equity stress; null = uniform shock.</summary>
    public IReadOnlyList<double>? IndexReturns { get; init; }

    /// <summary>Gets the index name for labels.</summary>
    public string IndexName { get; init; } = "OMXS30";

    /// <summary>Gets the instruments priced in a foreign currency (exposed to the SEK scenarios).</summary>
    public IReadOnlySet<string> FxExposed { get; init; } = new HashSet<string>();

    /// <summary>Gets the periods per year used to annualize volatility.</summary>
    public int PeriodsPerYear { get; init; } = 252;
}

/// <summary>One VaR/ES estimate.</summary>
/// <param name="Method">historical | parametric | monte-carlo.</param>
/// <param name="VarFraction">VaR as a loss fraction.</param>
/// <param name="EsFraction">ES as a loss fraction.</param>
/// <param name="VarAmount">VaR in currency.</param>
/// <param name="EsAmount">ES in currency.</param>
/// <param name="Observations">Sample size or simulated paths.</param>
/// <param name="Seed">Seed for Monte Carlo; null otherwise.</param>
public sealed record VarEsLine(string Method, double VarFraction, double EsFraction, double VarAmount, double EsAmount, long Observations, ulong? Seed);

/// <summary>One stress scenario.</summary>
/// <param name="Scenario">Description.</param>
/// <param name="PnlAmount">P&amp;L in currency (negative = loss).</param>
/// <param name="PnlFraction">P&amp;L as a fraction of portfolio value.</param>
public sealed record StressLine(string Scenario, double PnlAmount, double PnlFraction);

/// <summary>Portfolio risk report.</summary>
public sealed record RiskReport(
    DataLabel Label,
    IReadOnlyList<string> Instruments,
    IReadOnlyList<double> Weights,
    double Confidence,
    string CovarianceMethod,
    double Shrinkage,
    double AnnualizedVolatility,
    IReadOnlyList<VarEsLine> VarEs,
    IReadOnlyList<StressLine> Stress,
    IReadOnlyList<double>? Betas,
    IReadOnlyList<string> Notes);

/// <summary>Computes historical, parametric and Monte Carlo VaR/ES plus stress scenarios.</summary>
public static class RiskAnalysis
{
    /// <summary>Equity shock of the built-in index scenario.</summary>
    public const double EquityShock = -0.10;

    /// <summary>SEK move of the built-in FX scenarios.</summary>
    public const double FxShock = 0.05;

    /// <summary>Runs the report.</summary>
    public static RiskReport Run(QeEngine engine, RiskRequest request)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        ReturnsTable table = request.Returns;
        int n = table.Instruments.Count;
        double[] weights = ResolveWeights(request.Weights, n);
        var notes = new List<string>();
        if (table.Kind == ReturnKind.Log)
        {
            notes.Add("Log returns are aggregated linearly across instruments (approximation).");
        }

        // Historical: portfolio return per period.
        var portfolio = new double[table.Returns.Rows];
        for (int r = 0; r < portfolio.Length; r++)
        {
            for (int c = 0; c < n; c++)
            {
                portfolio[r] += weights[c] * table.Returns[r, c];
            }
        }

        CovarianceResult cov = engine.Covariance(table.Returns, request.Covariance, request.EwmaLambda);
        double[] mean = table.MeanReturns();
        double value = request.PortfolioValue;

        VarEsResult historical = engine.HistoricalVarEs(portfolio, request.Confidence);
        VarEsResult parametric = engine.ParametricVarEs(weights, mean, cov.Covariance, request.Confidence);
        VarEsResult monteCarlo = engine.MonteCarloVarEs(weights, mean, cov.Covariance, request.MonteCarloPaths, request.Seed, request.Confidence);
        var varEs = new List<VarEsLine>
        {
            Line("historical", historical, value, seed: null),
            Line("parametric", parametric, value, seed: null),
            Line("monte-carlo", monteCarlo, value, monteCarlo.Seed),
        };

        double variance = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                variance += weights[i] * cov.Covariance[i, j] * weights[j];
            }
        }

        (IReadOnlyList<StressLine> stress, double[]? betas) = Stress(engine, request, weights, notes);
        return new RiskReport(
            DataLabel.For(table),
            table.Instruments,
            weights,
            request.Confidence,
            request.Covariance.ToString(),
            cov.Shrinkage,
            Math.Sqrt(Math.Max(variance, 0) * request.PeriodsPerYear),
            varEs,
            stress,
            betas,
            notes);
    }

    internal static double[] ResolveWeights(IReadOnlyList<double>? weights, int n)
    {
        if (weights is null)
        {
            return Enumerable.Repeat(1.0 / n, n).ToArray();
        }

        if (weights.Count != n)
        {
            throw new ArgumentException($"Expected {n} weights, got {weights.Count}.", nameof(weights));
        }

        if (weights.Any(w => !double.IsFinite(w)) || Math.Abs(weights.Sum() - 1.0) > 1e-6)
        {
            throw new ArgumentException("Weights must be finite and sum to 1.", nameof(weights));
        }

        return weights.ToArray();
    }

    private static VarEsLine Line(string method, VarEsResult r, double value, ulong? seed) =>
        new(method, r.ValueAtRisk, r.ExpectedShortfall, r.ValueAtRisk * value, r.ExpectedShortfall * value, r.Observations, seed);

    private static (IReadOnlyList<StressLine> Lines, double[]? Betas) Stress(
        QeEngine engine, RiskRequest request, double[] weights, List<string> notes)
    {
        ReturnsTable table = request.Returns;
        int n = weights.Length;
        double[] values = weights.Select(w => w * request.PortfolioValue).ToArray();

        double[]? betas = null;
        string equityName;
        var equity = new double[n];
        if (request.IndexReturns is { } index)
        {
            betas = engine.Betas(table.Returns, index.ToArray());
            equityName = $"{request.IndexName} {EquityShock:P0} (beta-scaled)";
            for (int i = 0; i < n; i++)
            {
                equity[i] = betas[i] * EquityShock;
            }
        }
        else
        {
            equityName = $"Equities {EquityShock:P0} (uniform, no index given)";
            Array.Fill(equity, EquityShock);
        }

        var fx = new double[n];
        for (int i = 0; i < n; i++)
        {
            fx[i] = request.FxExposed.Contains(table.Instruments[i], StringComparer.OrdinalIgnoreCase) ? -FxShock : 0.0;
        }

        bool anyFx = fx.Any(x => x != 0);
        if (!anyFx)
        {
            notes.Add("FX: all positions assumed to be in SEK; SEK scenarios have no effect.");
        }

        var scenarios = new List<(string Name, double[] Shock)>
        {
            (equityName, equity),
            ($"SEK +{FxShock:P0} (foreign assets lose in SEK)", fx),
            ($"SEK -{FxShock:P0} (foreign assets gain in SEK)", fx.Select(x => -x).ToArray()),
            ($"{equityName} and SEK +{FxShock:P0}", equity.Zip(fx, (e, f) => e + f).ToArray()),
        };

        var shocks = new DenseMatrix(scenarios.Count, n);
        for (int s = 0; s < scenarios.Count; s++)
        {
            for (int i = 0; i < n; i++)
            {
                shocks[s, i] = scenarios[s].Shock[i];
            }
        }

        double[] pnl = engine.StressPnl(values, shocks);
        var lines = scenarios.Select((sc, s) => new StressLine(sc.Name, pnl[s], pnl[s] / request.PortfolioValue)).ToList();
        return (lines, betas);
    }
}
