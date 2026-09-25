using QuantAnalyst.Native.Interop;

namespace QuantAnalyst.Native;

/// <content>Portfolio: optimizers and the integer-lot rebalance solver.</content>
public sealed partial class QeEngine
{
    /// <summary>
    /// Optimizes weights. <paramref name="expectedReturns"/> is required for mean-variance only.
    /// Empty bounds mean 0 and 1; bounds are rejected for risk parity and HRP.
    /// </summary>
    public unsafe OptimizationResult Optimize(
        DenseMatrix covariance,
        OptimizationOptions options,
        ReadOnlySpan<double> expectedReturns = default,
        ReadOnlySpan<double> lower = default,
        ReadOnlySpan<double> upper = default)
    {
        ArgumentNullException.ThrowIfNull(covariance);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        int n = covariance.Rows;
        RequireSquare(covariance, n, nameof(covariance));
        if (!expectedReturns.IsEmpty)
        {
            RequireLength(expectedReturns.Length, n, nameof(expectedReturns));
        }

        if (!lower.IsEmpty)
        {
            RequireLength(lower.Length, n, nameof(lower));
        }

        if (!upper.IsEmpty)
        {
            RequireLength(upper.Length, n, nameof(upper));
        }

        var config = new QeOptConfig(options.Method, options.RiskAversion, options.Tolerance, options.MaxIterations);
        var weights = new double[n];
        QeOptResult result;
        fixed (double* pMu = expectedReturns)
        fixed (double* pCov = covariance.AsSpan())
        fixed (double* pLower = lower)
        fixed (double* pUpper = upper)
        fixed (double* pWeights = weights)
        {
            QeErrors.ThrowIfFailed(
                QeNative.Optimize(handle, &config, pMu, pCov, pLower, pUpper, n, pWeights, &result), "qe_optimize");
        }

        return new OptimizationResult(weights, result.Objective, result.Iterations, result.Converged != 0);
    }

    /// <summary>Integer-lot rebalance toward target weights (see qe_api.h, qe_rebalance).</summary>
    public unsafe RebalanceResult Rebalance(ReadOnlySpan<RebalanceAsset> assets, RebalanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        var config = new QeRebalanceConfig(options);
        var trades = new RebalanceTrade[assets.Length];
        RebalanceSummary summary;
        fixed (RebalanceAsset* pAssets = assets)
        fixed (RebalanceTrade* pTrades = trades)
        {
            QeErrors.ThrowIfFailed(QeNative.Rebalance(&config, pAssets, assets.Length, pTrades, &summary), "qe_rebalance");
        }

        return new RebalanceResult(trades, summary);
    }
}
