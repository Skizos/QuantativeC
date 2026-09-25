using System.Runtime.InteropServices;

namespace QuantAnalyst.Native;

/// <summary>VaR and ES as positive loss fractions (mirror of <c>qe_var_es</c>, 32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct VarEsResult
{
    private readonly double valueAtRisk;
    private readonly double expectedShortfall;
    private readonly long observations;
    private readonly ulong seed;

    internal VarEsResult(double valueAtRisk, double expectedShortfall, long observations, ulong seed)
    {
        this.valueAtRisk = valueAtRisk;
        this.expectedShortfall = expectedShortfall;
        this.observations = observations;
        this.seed = seed;
    }

    /// <summary>Gets the Value-at-Risk (positive = loss).</summary>
    public double ValueAtRisk => valueAtRisk;

    /// <summary>Gets the Expected Shortfall (positive = loss), always &gt;= VaR.</summary>
    public double ExpectedShortfall => expectedShortfall;

    /// <summary>Gets the sample size or simulated path count (0 for parametric).</summary>
    public long Observations => observations;

    /// <summary>Gets the seed used (Monte Carlo only).</summary>
    public ulong Seed => seed;
}

/// <summary>Covariance estimate and, for Ledoit-Wolf, the shrinkage intensity.</summary>
/// <param name="Covariance">N x N covariance.</param>
/// <param name="Shrinkage">Ledoit-Wolf intensity in [0, 1]; 0 otherwise.</param>
public sealed record CovarianceResult(DenseMatrix Covariance, double Shrinkage);

/// <summary>Optimizer configuration.</summary>
public sealed record OptimizationOptions
{
    /// <summary>Gets the method.</summary>
    public OptimizationMethod Method { get; init; } = OptimizationMethod.MinVariance;

    /// <summary>Gets the risk aversion (mean-variance only).</summary>
    public double RiskAversion { get; init; } = 1.0;

    /// <summary>Gets the iterate tolerance; 0 selects the native default (1e-12).</summary>
    public double Tolerance { get; init; }

    /// <summary>Gets the iteration cap; 0 selects the native default (100000).</summary>
    public int MaxIterations { get; init; }
}

/// <summary>Optimized weights plus solver diagnostics.</summary>
/// <param name="Weights">Weights, summing to 1.</param>
/// <param name="Objective">Method-specific objective (see qe_api.h, qe_opt_result).</param>
/// <param name="Iterations">Iterations or sweeps used.</param>
/// <param name="Converged">Whether the solver met its tolerance.</param>
public sealed record OptimizationResult(IReadOnlyList<double> Weights, double Objective, int Iterations, bool Converged);

/// <summary>One asset for the rebalance solver (mirror of <c>qe_rebalance_asset</c>, 32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct RebalanceAsset
{
    private readonly double price;
    private readonly double targetWeight;
    private readonly long currentQuantity;
    private readonly long lotSize;

    /// <summary>Initializes a new instance.</summary>
    public RebalanceAsset(double price, double targetWeight, long currentQuantity, long lotSize)
    {
        this.price = price;
        this.targetWeight = targetWeight;
        this.currentQuantity = currentQuantity;
        this.lotSize = lotSize;
    }

    /// <summary>Gets the price in portfolio currency.</summary>
    public double Price => price;

    /// <summary>Gets the target weight.</summary>
    public double TargetWeight => targetWeight;

    /// <summary>Gets the current quantity.</summary>
    public long CurrentQuantity => currentQuantity;

    /// <summary>Gets the lot size.</summary>
    public long LotSize => lotSize;
}

/// <summary>Rebalance output for one asset (mirror of <c>qe_rebalance_trade</c>, 40 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct RebalanceTrade
{
    private readonly long targetQuantity;
    private readonly long tradeQuantity;
    private readonly double tradeValue;
    private readonly double fee;
    private readonly double finalWeight;

    internal RebalanceTrade(long targetQuantity, long tradeQuantity, double tradeValue, double fee, double finalWeight)
    {
        this.targetQuantity = targetQuantity;
        this.tradeQuantity = tradeQuantity;
        this.tradeValue = tradeValue;
        this.fee = fee;
        this.finalWeight = finalWeight;
    }

    /// <summary>Gets the quantity after the trade.</summary>
    public long TargetQuantity => targetQuantity;

    /// <summary>Gets the signed trade quantity (&gt; 0 buy).</summary>
    public long TradeQuantity => tradeQuantity;

    /// <summary>Gets the signed trade value.</summary>
    public double TradeValue => tradeValue;

    /// <summary>Gets the modeled courtage.</summary>
    public double Fee => fee;

    /// <summary>Gets the weight after the trade.</summary>
    public double FinalWeight => finalWeight;
}

/// <summary>Rebalance totals (mirror of <c>qe_rebalance_summary</c>, 40 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct RebalanceSummary
{
    private readonly double portfolioValue;
    private readonly double cashAfter;
    private readonly double totalFees;
    private readonly double trackingError;
    private readonly int trades;
    private readonly int feasible;

    internal RebalanceSummary(double portfolioValue, double cashAfter, double totalFees, double trackingError, int trades, int feasible)
    {
        this.portfolioValue = portfolioValue;
        this.cashAfter = cashAfter;
        this.totalFees = totalFees;
        this.trackingError = trackingError;
        this.trades = trades;
        this.feasible = feasible;
    }

    /// <summary>Gets the portfolio value before trades.</summary>
    public double PortfolioValue => portfolioValue;

    /// <summary>Gets the cash after trades and fees.</summary>
    public double CashAfter => cashAfter;

    /// <summary>Gets the total modeled courtage.</summary>
    public double TotalFees => totalFees;

    /// <summary>Gets 0.5 * sum |final - target| over assets and cash.</summary>
    public double TrackingError => trackingError;

    /// <summary>Gets the number of non-zero trades.</summary>
    public int Trades => trades;

    /// <summary>Gets a value indicating whether the cash buffer is respected.</summary>
    public bool Feasible => feasible != 0;
}

/// <summary>Rebalance configuration. Courtage = max(FeeMin, FeeRate * |value|).</summary>
public sealed record RebalanceOptions
{
    /// <summary>Gets the available cash.</summary>
    public double Cash { get; init; }

    /// <summary>Gets the cash to keep after trades and fees.</summary>
    public double CashBuffer { get; init; }

    /// <summary>Gets the minimum trade value.</summary>
    public double MinTradeValue { get; init; }

    /// <summary>Gets the minimum courtage per trade.</summary>
    public double FeeMin { get; init; }

    /// <summary>Gets the proportional courtage rate.</summary>
    public double FeeRate { get; init; }
}

/// <summary>Trades per asset (input order) and totals.</summary>
/// <param name="Trades">One entry per input asset.</param>
/// <param name="Summary">Totals.</param>
public sealed record RebalanceResult(IReadOnlyList<RebalanceTrade> Trades, RebalanceSummary Summary);
