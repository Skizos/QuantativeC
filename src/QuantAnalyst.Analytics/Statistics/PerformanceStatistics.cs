namespace QuantAnalyst.Analytics.Statistics;

/// <summary>
/// Sharpe-ratio statistics used to judge backtests (CLAUDE.md: report raw SR, Deflated SR, PBO). All Sharpe values
/// here are <b>per period</b> (e.g. daily) unless a method says "annualised"; PSR/DSR are defined per period.
/// Formulas and references: docs/plans/05-phase5-backtesting.md.
/// </summary>
public static class PerformanceStatistics
{
    public const int TradingDaysPerYear = 252;
    public const double EulerGamma = 0.57721566490153286061;

    /// <summary>mean / sample standard deviation (ddof = 1). NaN for fewer than 2 values or zero variance.</summary>
    public static double Sharpe(ReadOnlySpan<double> returns)
    {
        if (returns.Length < 2)
        {
            return double.NaN;
        }

        (double mean, double m2) = Moments2(returns);
        double sd = Math.Sqrt(m2 * returns.Length / (returns.Length - 1));
        return sd > 0 ? mean / sd : double.NaN;
    }

    public static double AnnualisedSharpe(ReadOnlySpan<double> returns, int periodsPerYear = TradingDaysPerYear) =>
        Sharpe(returns) * Math.Sqrt(periodsPerYear);

    /// <summary>Adjusted Fisher–Pearson skewness G1 (as pandas/Excel). Needs at least 3 values.</summary>
    public static double Skewness(ReadOnlySpan<double> returns)
    {
        int n = returns.Length;
        if (n < 3)
        {
            return double.NaN;
        }

        (double mean, double m2) = Moments2(returns);
        double m3 = 0;
        foreach (double r in returns)
        {
            double d = r - mean;
            m3 += d * d * d;
        }

        m3 /= n;
        double g1 = m3 / Math.Pow(m2, 1.5);
        return g1 * Math.Sqrt((double)n * (n - 1)) / (n - 2);
    }

    /// <summary>Kurtosis (not excess): adjusted excess kurtosis G2 plus 3 (as pandas/Excel + 3). Needs at least 4 values.</summary>
    public static double Kurtosis(ReadOnlySpan<double> returns)
    {
        int n = returns.Length;
        if (n < 4)
        {
            return double.NaN;
        }

        (double mean, double m2) = Moments2(returns);
        double m4 = 0;
        foreach (double r in returns)
        {
            double d = (r - mean) * (r - mean);
            m4 += d * d;
        }

        m4 /= n;
        double g2 = (m4 / (m2 * m2)) - 3.0;
        double bigG2 = (((n + 1) * g2) + 6.0) * (n - 1) / ((double)(n - 2) * (n - 3));
        return bigG2 + 3.0;
    }

    /// <summary>
    /// Probabilistic Sharpe Ratio: P(true SR &gt; <paramref name="targetSharpe"/>) given the observed per-period SR over
    /// <paramref name="observations"/> periods with the returns' skewness and kurtosis (Bailey &amp; López de Prado 2012).
    /// </summary>
    public static double Psr(double sharpe, int observations, double skewness, double kurtosis, double targetSharpe = 0.0)
    {
        if (observations < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(observations), observations, "PSR needs at least 2 observations.");
        }

        double variance = 1.0 - (skewness * sharpe) + ((kurtosis - 1.0) / 4.0 * sharpe * sharpe);
        if (!(variance > 0))
        {
            return double.NaN;
        }

        return Normal.Cdf((sharpe - targetSharpe) * Math.Sqrt(observations - 1) / Math.Sqrt(variance));
    }

    /// <summary>E[max of N standard normals] ≈ (1−γ)·Φ⁻¹(1−1/N) + γ·Φ⁻¹(1−1/(N·e)) (Bailey &amp; López de Prado 2014).</summary>
    public static double ExpectedMaxZ(int trials)
    {
        if (trials < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(trials), trials, "The expected maximum needs at least 2 trials.");
        }

        return ((1 - EulerGamma) * Normal.Quantile(1.0 - (1.0 / trials))) + (EulerGamma * Normal.Quantile(1.0 - (1.0 / (trials * Math.E))));
    }

    /// <summary>The Sharpe ratio the best of N unskilled trials is expected to reach: √V[SR]·E[max Z].</summary>
    public static double DeflationThreshold(double sharpeStdAcrossTrials, int trials) => sharpeStdAcrossTrials * ExpectedMaxZ(trials);

    /// <summary>
    /// Deflated Sharpe Ratio = PSR against the expected maximum Sharpe of <paramref name="trials"/> unskilled trials whose
    /// per-period Sharpe ratios have standard deviation <paramref name="sharpeStdAcrossTrials"/> (Bailey &amp; López de
    /// Prado 2014). A value below 0.95 means the result is not significant at 5 % once the number of trials is accounted for.
    /// </summary>
    public static double Dsr(double sharpe, double sharpeStdAcrossTrials, int trials, int observations, double skewness, double kurtosis) =>
        Psr(sharpe, observations, skewness, kurtosis, DeflationThreshold(sharpeStdAcrossTrials, trials));

    /// <summary>Largest peak-to-trough fall of an equity curve, as a positive fraction.</summary>
    public static double MaxDrawdown(ReadOnlySpan<double> equity)
    {
        double peak = double.NegativeInfinity, worst = 0;
        foreach (double e in equity)
        {
            peak = Math.Max(peak, e);
            if (peak > 0)
            {
                worst = Math.Max(worst, 1.0 - (e / peak));
            }
        }

        return worst;
    }

    private static (double Mean, double M2) Moments2(ReadOnlySpan<double> values)
    {
        double sum = 0;
        foreach (double v in values)
        {
            sum += v;
        }

        double mean = sum / values.Length;
        double m2 = 0;
        foreach (double v in values)
        {
            m2 += (v - mean) * (v - mean);
        }

        return (mean, m2 / values.Length);
    }
}
