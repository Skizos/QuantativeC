using QuantAnalyst.Analytics.Data;
using QuantAnalyst.Analytics.Portfolio;
using QuantAnalyst.Analytics.Reporting;
using QuantAnalyst.Analytics.Risk;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Tests;

public sealed class AnalysisTests : IDisposable
{
    private readonly QeEngine engine = QeEngine.Create(seed: 20260925);
    private readonly ReturnsTable all = CsvPriceLoader
        .Load(Path.Combine(AppContext.BaseDirectory, "data", "synthetic-prices.csv"))
        .ToReturns(ReturnKind.Simple);

    public void Dispose() => engine.Dispose();

    [Fact]
    public void Risk_HistoricalMatchesManualPortfolioReturns()
    {
        ReturnsTable portfolio = all.Select(["SYN-A", "SYN-B"]);
        double[] a = portfolio.Column("SYN-A");
        double[] b = portfolio.Column("SYN-B");
        double[] losses = a.Zip(b, (x, y) => -(0.25 * x + 0.75 * y)).Order().ToArray();
        int k = (int)Math.Ceiling(0.95 * losses.Length);

        RiskReport r = RiskAnalysis.Run(engine, new RiskRequest { Returns = portfolio, Weights = [0.25, 0.75], Confidence = 0.95 });

        VarEsLine historical = r.VarEs.Single(l => l.Method == "historical");
        Assert.Equal(losses[k - 1], historical.VarFraction, tolerance: 1e-15);
        Assert.Equal(losses[(k - 1)..].Average(), historical.EsFraction, tolerance: 1e-15);
        Assert.Equal(historical.VarFraction * 1_000_000, historical.VarAmount, tolerance: 1e-6);
        Assert.All(r.VarEs, l => Assert.True(l.EsFraction >= l.VarFraction));
        Assert.Equal(20260925UL, r.VarEs.Single(l => l.Method == "monte-carlo").Seed);
    }

    [Fact]
    public void Risk_StressUsesBetasAndFxExposure()
    {
        RiskReport r = RiskAnalysis.Run(engine, new RiskRequest
        {
            Returns = all.Select(["SYN-A", "SYN-D"]),
            IndexReturns = all.Column("SYN-INDEX"),
            IndexName = "SYN-INDEX",
            FxExposed = new HashSet<string> { "SYN-D" },
            PortfolioValue = 100_000,
        });

        Assert.NotNull(r.Betas);
        double expectedEquity = 50_000 * r.Betas[0] * -0.10 + 50_000 * r.Betas[1] * -0.10;
        Assert.Equal(expectedEquity, r.Stress[0].PnlAmount, tolerance: 1e-6);
        Assert.Equal(-2_500, r.Stress[1].PnlAmount, tolerance: 1e-9); // SEK +5 % on the 50 % foreign position
        Assert.Equal(2_500, r.Stress[2].PnlAmount, tolerance: 1e-9);
        Assert.Equal(expectedEquity - 2_500, r.Stress[3].PnlAmount, tolerance: 1e-6);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Risk_NotesSekOnlyPortfoliosAndLabelsSurvivorship()
    {
        RiskReport r = RiskAnalysis.Run(engine, new RiskRequest { Returns = all.Select(["SYN-A"]) });

        Assert.Contains(r.Notes, n => n.Contains("SEK", StringComparison.Ordinal));
        Assert.Equal(DataLabel.NotSurvivorshipFree, r.Label.Survivorship);
        Assert.Contains("ISK", r.Label.Taxation, StringComparison.Ordinal);
        Assert.Equal(259, r.Label.Observations);
    }

    [Fact]
    public void Risk_RejectsBadWeights()
    {
        ReturnsTable p = all.Select(["SYN-A", "SYN-B"]);

        Assert.Throws<ArgumentException>(() => RiskAnalysis.Run(engine, new RiskRequest { Returns = p, Weights = [1.0] }));
        Assert.Throws<ArgumentException>(() => RiskAnalysis.Run(engine, new RiskRequest { Returns = p, Weights = [0.6, 0.6] }));
    }

    [Theory]
    [InlineData(OptimizationMethod.MinVariance)]
    [InlineData(OptimizationMethod.MeanVariance)]
    [InlineData(OptimizationMethod.RiskParity)]
    [InlineData(OptimizationMethod.Hrp)]
    public void Optimize_WeightsSumToOneAndRiskSharesSumToOne(OptimizationMethod method)
    {
        OptimizeReport r = PortfolioOptimization.Run(engine, new OptimizeRequest
        {
            Returns = all.Select(["SYN-A", "SYN-B", "SYN-C", "SYN-D"]),
            Method = method,
            MaxWeight = 0.6,
        });

        Assert.Equal(1.0, r.Weights.Sum(w => w.Weight), tolerance: 1e-12);
        Assert.Equal(1.0, r.Weights.Sum(w => w.RiskContribution), tolerance: 1e-9);
        Assert.True(r.Converged);
        if (method == OptimizationMethod.RiskParity)
        {
            Assert.All(r.Weights, w => Assert.Equal(0.25, w.RiskContribution, tolerance: 1e-9));
        }

        if (method is OptimizationMethod.MinVariance or OptimizationMethod.MeanVariance)
        {
            Assert.All(r.Weights, w => Assert.InRange(w.Weight, 0.0, 0.6 + 1e-15));
        }
    }
}
