namespace QuantAnalyst.Native;

/// <summary>Exercise style for lattice pricing (<c>QE_EXERCISE_*</c>).</summary>
public enum ExerciseStyle
{
    /// <summary>Exercise at expiry only.</summary>
    European = 0,

    /// <summary>Exercise at any lattice node.</summary>
    American = 1,
}

/// <summary>Covariance estimator (<c>QE_COV_*</c>).</summary>
public enum CovarianceMethod
{
    /// <summary>Unbiased (T - 1) sample covariance.</summary>
    Sample = 0,

    /// <summary>Exponentially weighted: weights proportional to lambda^(T-1-t), normalized, weighted mean removed.</summary>
    Ewma = 1,

    /// <summary>Ledoit-Wolf shrinkage toward mu * I (as scikit-learn <c>LedoitWolf</c>).</summary>
    LedoitWolf = 2,
}

/// <summary>Portfolio construction method (<c>QE_OPT_*</c>).</summary>
public enum OptimizationMethod
{
    /// <summary>Minimum variance with budget and box constraints.</summary>
    MinVariance = 0,

    /// <summary>Mean-variance (maximize mu'w - lambda/2 w'Cw) with budget and box constraints.</summary>
    MeanVariance = 1,

    /// <summary>Equal risk contribution, long-only.</summary>
    RiskParity = 2,

    /// <summary>Hierarchical Risk Parity (Lopez de Prado 2016), long-only.</summary>
    Hrp = 3,
}
