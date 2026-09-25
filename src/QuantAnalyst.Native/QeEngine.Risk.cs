using QuantAnalyst.Native.Interop;

namespace QuantAnalyst.Native;

/// <content>Risk: covariance, VaR/ES, betas, stress.</content>
public sealed partial class QeEngine
{
    /// <summary>Covariance of a T x N return matrix (rows = observations).</summary>
    public unsafe CovarianceResult Covariance(DenseMatrix returns, CovarianceMethod method, double ewmaLambda = 0.94)
    {
        ArgumentNullException.ThrowIfNull(returns);
        ThrowIfDisposed();
        var config = new QeCovConfig(method, ewmaLambda);
        var cov = new DenseMatrix(returns.Columns, returns.Columns);
        double shrinkage;
        fixed (double* pReturns = returns.AsSpan())
        fixed (double* pCov = cov.AsWritableSpan())
        {
            QeErrors.ThrowIfFailed(
                QeNative.Covariance(handle, &config, pReturns, returns.Rows, returns.Columns, pCov, &shrinkage),
                "qe_covariance");
        }

        return new CovarianceResult(cov, shrinkage);
    }

    /// <summary>Historical VaR/ES of fractional returns (positive = loss).</summary>
    public unsafe VarEsResult HistoricalVarEs(ReadOnlySpan<double> returns, double confidence)
    {
        ThrowIfDisposed();
        VarEsResult result;
        fixed (double* pReturns = returns)
        {
            QeErrors.ThrowIfFailed(QeNative.VarEsHistorical(pReturns, returns.Length, confidence, &result), "qe_var_es_historical");
        }

        return result;
    }

    /// <summary>Normal (variance-covariance) VaR/ES. An empty <paramref name="mean"/> means zero means.</summary>
    public unsafe VarEsResult ParametricVarEs(
        ReadOnlySpan<double> weights, ReadOnlySpan<double> mean, DenseMatrix covariance, double confidence)
    {
        ThrowIfDisposed();
        RequirePortfolio(weights, mean, covariance);
        VarEsResult result;
        fixed (double* pW = weights)
        fixed (double* pMean = mean)
        fixed (double* pCov = covariance.AsSpan())
        {
            QeErrors.ThrowIfFailed(
                QeNative.VarEsParametric(pW, pMean, pCov, weights.Length, confidence, &result), "qe_var_es_parametric");
        }

        return result;
    }

    /// <summary>Monte Carlo VaR/ES with multivariate normal returns. <paramref name="seed"/> 0 = engine seed.</summary>
    public unsafe VarEsResult MonteCarloVarEs(
        ReadOnlySpan<double> weights, ReadOnlySpan<double> mean, DenseMatrix covariance, long paths, ulong seed, double confidence)
    {
        ThrowIfDisposed();
        RequirePortfolio(weights, mean, covariance);
        VarEsResult result;
        fixed (double* pW = weights)
        fixed (double* pMean = mean)
        fixed (double* pCov = covariance.AsSpan())
        {
            QeErrors.ThrowIfFailed(
                QeNative.VarEsMonteCarlo(handle, pW, pMean, pCov, weights.Length, paths, seed, confidence, &result),
                "qe_var_es_monte_carlo");
        }

        return result;
    }

    /// <summary>OLS betas of each column of a T x N return matrix on an index series of length T.</summary>
    public unsafe double[] Betas(DenseMatrix returns, ReadOnlySpan<double> index)
    {
        ArgumentNullException.ThrowIfNull(returns);
        ThrowIfDisposed();
        RequireLength(index.Length, returns.Rows, nameof(index));
        var betas = new double[returns.Columns];
        fixed (double* pReturns = returns.AsSpan())
        fixed (double* pIndex = index)
        fixed (double* pBetas = betas)
        {
            QeErrors.ThrowIfFailed(QeNative.Betas(pReturns, pIndex, returns.Rows, returns.Columns, pBetas), "qe_betas");
        }

        return betas;
    }

    /// <summary>Scenario P&amp;L for position values and an S x N matrix of fractional shocks.</summary>
    public unsafe double[] StressPnl(ReadOnlySpan<double> values, DenseMatrix shocks)
    {
        ArgumentNullException.ThrowIfNull(shocks);
        ThrowIfDisposed();
        RequireLength(values.Length, shocks.Columns, nameof(values));
        var pnl = new double[shocks.Rows];
        fixed (double* pValues = values)
        fixed (double* pShocks = shocks.AsSpan())
        fixed (double* pPnl = pnl)
        {
            QeErrors.ThrowIfFailed(QeNative.StressPnl(pValues, pShocks, shocks.Columns, shocks.Rows, pPnl), "qe_stress_pnl");
        }

        return pnl;
    }

    private static void RequirePortfolio(ReadOnlySpan<double> weights, ReadOnlySpan<double> mean, DenseMatrix covariance)
    {
        RequireSquare(covariance, weights.Length, nameof(covariance));
        if (!mean.IsEmpty)
        {
            RequireLength(mean.Length, weights.Length, nameof(mean));
        }
    }
}
