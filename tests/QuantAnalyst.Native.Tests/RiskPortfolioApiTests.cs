namespace QuantAnalyst.Native.Tests;

public sealed class RiskPortfolioApiTests : IDisposable
{
    private readonly QeEngine engine = QeEngine.Create(seed: 777);

    public void Dispose() => engine.Dispose();

    [Fact]
    public void SampleCovariance_MatchesHandComputation()
    {
        // Two assets, three observations: x = (1, 2, 3)%, y = (2, 1, 3)%.
        var returns = DenseMatrix.FromRows([[0.01, 0.02], [0.02, 0.01], [0.03, 0.03]]);

        CovarianceResult r = engine.Covariance(returns, CovarianceMethod.Sample);

        Assert.Equal(1e-4, r.Covariance[0, 0], tolerance: 1e-18);
        Assert.Equal(1e-4, r.Covariance[1, 1], tolerance: 1e-18);
        Assert.Equal(0.5e-4, r.Covariance[0, 1], tolerance: 1e-18);
        Assert.Equal(r.Covariance[0, 1], r.Covariance[1, 0]);
        Assert.Equal(0.0, r.Shrinkage);
    }

    [Fact]
    public void LedoitWolf_ReportsShrinkage()
    {
        var returns = DenseMatrix.FromRows([[0.01, 0.02, -0.01], [0.02, 0.01, 0.0], [0.03, 0.03, 0.02], [-0.01, 0.0, 0.01]]);

        CovarianceResult r = engine.Covariance(returns, CovarianceMethod.LedoitWolf);

        Assert.InRange(r.Shrinkage, 0.0, 1.0);
    }

    [Fact]
    public void HistoricalVarEs_HandComputed()
    {
        double[] returns = Enumerable.Range(1, 100).Select(i => -i / 100.0).ToArray();

        VarEsResult r = engine.HistoricalVarEs(returns, 0.95);

        Assert.Equal(0.95, r.ValueAtRisk);
        Assert.Equal(0.975, r.ExpectedShortfall, tolerance: 1e-15);
        Assert.Equal(100, r.Observations);
    }

    [Fact]
    public void ParametricAndMonteCarloVarEs_Agree_AndMonteCarloIsDeterministic()
    {
        var cov = DenseMatrix.FromRows([[4e-4, 1e-4], [1e-4, 2.25e-4]]);
        double[] w = [0.6, 0.4];

        VarEsResult exact = engine.ParametricVarEs(w, [], cov, 0.99);
        VarEsResult mc = engine.MonteCarloVarEs(w, [], cov, 400_000, 0, 0.99);
        VarEsResult again = engine.MonteCarloVarEs(w, [], cov, 400_000, 0, 0.99);

        Assert.Equal(777UL, mc.Seed);
        Assert.Equal(1.0, mc.ValueAtRisk / exact.ValueAtRisk, tolerance: 0.015);
        Assert.True(mc.ExpectedShortfall >= mc.ValueAtRisk);
        Assert.Equal(mc.ValueAtRisk, again.ValueAtRisk);
    }

    [Fact]
    public void BetasAndStress_ThroughBinding()
    {
        double[] index = [0.01, -0.02, 0.015, -0.005];
        var returns = DenseMatrix.FromRows([[0.02, 0.01], [-0.04, -0.02], [0.03, 0.015], [-0.01, -0.005]]);

        double[] betas = engine.Betas(returns, index);
        double[] pnl = engine.StressPnl([1000.0, 2000.0], DenseMatrix.FromRows([[-0.1, -0.05], [0.0, 0.05]]));

        Assert.Equal(2.0, betas[0], tolerance: 1e-12);
        Assert.Equal(1.0, betas[1], tolerance: 1e-12);
        Assert.Equal([-200.0, 100.0], pnl);
    }

    [Theory]
    [InlineData(OptimizationMethod.MinVariance)]
    [InlineData(OptimizationMethod.MeanVariance)]
    [InlineData(OptimizationMethod.RiskParity)]
    [InlineData(OptimizationMethod.Hrp)]
    public void Optimize_FullyInvestedWithinBounds(OptimizationMethod method)
    {
        var cov = DenseMatrix.FromRows([[0.040, 0.006, 0.004], [0.006, 0.025, 0.005], [0.004, 0.005, 0.090]]);
        bool bounded = method is OptimizationMethod.MinVariance or OptimizationMethod.MeanVariance;
        double[] lower = bounded ? [0.1, 0.1, 0.1] : [];
        double[] upper = bounded ? [0.6, 0.6, 0.6] : [];

        OptimizationResult r = engine.Optimize(
            cov, new OptimizationOptions { Method = method, RiskAversion = 3.0 }, [0.08, 0.05, 0.12], lower, upper);

        Assert.True(r.Converged);
        Assert.Equal(1.0, r.Weights.Sum(), tolerance: 1e-12);
        Assert.All(r.Weights, w => Assert.InRange(w, bounded ? 0.1 - 1e-15 : 0.0, bounded ? 0.6 + 1e-15 : 1.0));
    }

    [Fact]
    public void Optimize_RejectsBoundsForRiskParity()
    {
        var cov = DenseMatrix.FromRows([[0.04, 0.0], [0.0, 0.09]]);

        QeException ex = Assert.Throws<QeException>(() => engine.Optimize(
            cov, new OptimizationOptions { Method = OptimizationMethod.RiskParity }, [], [0.0, 0.0], [1.0, 1.0]));
        Assert.Equal(QeStatus.InvalidArgument, ex.Status);
    }

    [Fact]
    public void Rebalance_RespectsLotsAndCash()
    {
        RebalanceAsset[] assets = [new(101.3, 0.5, 0, 10), new(57.9, 0.3, 0, 25), new(233.1, 0.2, 0, 1)];
        var options = new RebalanceOptions { Cash = 100_000, MinTradeValue = 1_000, FeeMin = 39, FeeRate = 0.0015 };

        RebalanceResult r = engine.Rebalance(assets, options);

        Assert.Equal(0, r.Trades[0].TradeQuantity % 10);
        Assert.Equal(0, r.Trades[1].TradeQuantity % 25);
        Assert.True(r.Summary.Feasible);
        Assert.True(r.Summary.CashAfter >= 0);
        Assert.Equal(3, r.Summary.Trades);
        Assert.Equal(r.Trades.Sum(t => t.Fee), r.Summary.TotalFees, tolerance: 1e-9);
    }

    [Fact]
    public void DimensionMismatches_ThrowBeforeCallingNative()
    {
        var cov = DenseMatrix.FromRows([[0.04, 0.0], [0.0, 0.09]]);

        Assert.Throws<ArgumentException>(() => engine.ParametricVarEs([1.0], [], cov, 0.99));
        Assert.Throws<ArgumentException>(() => engine.ParametricVarEs([0.5, 0.5], [0.0], cov, 0.99));
        Assert.Throws<ArgumentException>(() => engine.Betas(cov, [0.1]));
        Assert.Throws<ArgumentException>(() => engine.StressPnl([1.0], cov));
        Assert.Throws<ArgumentException>(() => engine.Optimize(cov, new OptimizationOptions(), [], [0.0], []));
    }

    [Fact]
    public void DenseMatrix_ValidatesShapeAndIndices()
    {
        Assert.Throws<ArgumentException>(() => new DenseMatrix(2, 2, [1.0, 2.0, 3.0]));
        Assert.Throws<ArgumentException>(() => DenseMatrix.FromRows([[1.0, 2.0], [3.0]]));
        var m = new DenseMatrix(2, 3);
        m[1, 2] = 5.0;
        Assert.Equal(5.0, m.AsSpan()[5]);
        Assert.Equal([0.0, 0.0, 5.0], m.GetRow(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => m[2, 0]);
    }
}
