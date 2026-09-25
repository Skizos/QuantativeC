using QuantAnalyst.Native.Interop;

namespace QuantAnalyst.Native;

/// <content>Pricing: Greeks, implied volatility, CRR lattice, Monte Carlo.</content>
public sealed partial class QeEngine
{
    /// <summary>Computes price and Greeks per element; invalid elements get a non-OK status.</summary>
    /// <returns>The number of failed elements.</returns>
    public unsafe long ComputeGreeks(ReadOnlySpan<BlackScholesInput> inputs, Span<BlackScholesGreeks> outputs)
    {
        ThrowIfDisposed();
        RequireSameLength(inputs.Length, outputs.Length, nameof(outputs));
        long failed;
        fixed (BlackScholesInput* pIn = inputs)
        fixed (BlackScholesGreeks* pOut = outputs)
        {
            QeErrors.ThrowIfFailed(QeNative.BsGreeksBatch(handle, pIn, pOut, inputs.Length, &failed), "qe_bs_greeks_batch");
        }

        return failed;
    }

    /// <summary>Solves Black-Scholes implied volatility per element (Brent).</summary>
    /// <returns>The number of failed elements.</returns>
    public unsafe long ImpliedVolatility(ReadOnlySpan<ImpliedVolInput> inputs, Span<ImpliedVolOutput> outputs)
    {
        ThrowIfDisposed();
        RequireSameLength(inputs.Length, outputs.Length, nameof(outputs));
        long failed;
        fixed (ImpliedVolInput* pIn = inputs)
        fixed (ImpliedVolOutput* pOut = outputs)
        {
            QeErrors.ThrowIfFailed(QeNative.ImpliedVolBatch(handle, pIn, pOut, inputs.Length, &failed), "qe_implied_vol_batch");
        }

        return failed;
    }

    /// <summary>Prices options on a Cox-Ross-Rubinstein lattice (European or American).</summary>
    /// <returns>The number of failed elements.</returns>
    public unsafe long PriceLattice(ReadOnlySpan<LatticeInput> inputs, Span<BlackScholesOutput> outputs)
    {
        ThrowIfDisposed();
        RequireSameLength(inputs.Length, outputs.Length, nameof(outputs));
        long failed;
        fixed (LatticeInput* pIn = inputs)
        fixed (BlackScholesOutput* pOut = outputs)
        {
            QeErrors.ThrowIfFailed(QeNative.LatticeBatch(handle, pIn, pOut, inputs.Length, &failed), "qe_lattice_batch");
        }

        return failed;
    }

    /// <summary>Monte Carlo price of one European option. The result carries SE, paths and seed.</summary>
    public unsafe MonteCarloResult PriceMonteCarlo(in BlackScholesInput option, MonteCarloOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        int flags = (options.Antithetic ? QeMcConfig.Antithetic : 0) |
                    (options.ControlVariate ? QeMcConfig.ControlVariate : 0) |
                    (options.Sobol ? QeMcConfig.Sobol : 0);
        var config = new QeMcConfig(flags, options.Paths, options.Seed, options.Replications, options.Steps);
        MonteCarloResult result;
        fixed (BlackScholesInput* pOption = &option)
        {
            QeErrors.ThrowIfFailed(QeNative.McEuropean(handle, &config, pOption, &result), "qe_mc_european");
        }

        return result;
    }
}
