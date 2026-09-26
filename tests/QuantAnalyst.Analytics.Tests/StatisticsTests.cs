using System.Text.Json;
using QuantAnalyst.Analytics.Statistics;

namespace QuantAnalyst.Analytics.Tests;

/// <summary>
/// Sharpe/PSR/DSR/PBO against <c>Reference/phase5_reference.json</c>: values from an independent numpy/scipy
/// implementation of the published formulas (tools/reference/phase5_reference.py), which also agree with pypbo.
/// </summary>
public sealed class StatisticsTests
{
    private static readonly JsonElement Ref = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reference", "phase5_reference.json"))).RootElement;

    private static double[] Doubles(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetDouble())];

    [Fact]
    public void NormalCdfAndQuantile_MatchScipy()
    {
        foreach (JsonElement pair in Ref.GetProperty("normal").GetProperty("cdf").EnumerateArray())
        {
            double x = pair[0].GetDouble(), expected = pair[1].GetDouble();
            Assert.Equal(expected, Normal.Cdf(x), expected * 1e-12 + 1e-300);
        }

        foreach (JsonElement pair in Ref.GetProperty("normal").GetProperty("ppf").EnumerateArray())
        {
            double p = pair[0].GetDouble(), expected = pair[1].GetDouble();
            Assert.Equal(expected, Normal.Quantile(p), Math.Max(1e-12, Math.Abs(expected) * 1e-12));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => Normal.Quantile(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Normal.Quantile(1.0));
    }

    [Fact]
    public void SharpeSkewKurtosisAndPsr_MatchReference()
    {
        JsonElement s = Ref.GetProperty("series");
        double[] r = Doubles(s.GetProperty("returns"));
        double sr = PerformanceStatistics.Sharpe(r);
        double skew = PerformanceStatistics.Skewness(r);
        double kurt = PerformanceStatistics.Kurtosis(r);
        Assert.Equal(s.GetProperty("sharpe").GetDouble(), sr, 1e-13);
        Assert.Equal(s.GetProperty("skew").GetDouble(), skew, 1e-12);
        Assert.Equal(s.GetProperty("kurtosis").GetDouble(), kurt, 1e-11);
        Assert.Equal(s.GetProperty("psr0").GetDouble(), PerformanceStatistics.Psr(sr, r.Length, skew, kurt), 1e-12);
        Assert.Equal(s.GetProperty("psr005").GetDouble(), PerformanceStatistics.Psr(sr, r.Length, skew, kurt, 0.05), 1e-12);
        Assert.Equal(sr * Math.Sqrt(252), PerformanceStatistics.AnnualisedSharpe(r), 1e-12);
    }

    [Fact]
    public void ExpectedMaxAndDsr_MatchReference()
    {
        foreach (JsonElement pair in Ref.GetProperty("expectedMaxZ").EnumerateArray())
        {
            Assert.Equal(pair[1].GetDouble(), PerformanceStatistics.ExpectedMaxZ(pair[0].GetInt32()), 1e-12);
        }

        JsonElement w = Ref.GetProperty("sweep");
        (double[] m, int t, int n) = Sweep();
        double[] sharpes = [.. Enumerable.Range(0, n).Select(j => PerformanceStatistics.Sharpe(Column(m, t, n, j)))];
        Assert.Equal(Doubles(w.GetProperty("sharpes")), sharpes, (a, b) => Math.Abs(a - b) < 1e-13);
        int best = Array.IndexOf(sharpes, sharpes.Max());
        Assert.Equal(w.GetProperty("best").GetInt32(), best);
        double std = StdDev(sharpes);
        Assert.Equal(w.GetProperty("sharpeStd").GetDouble(), std, 1e-13);
        double[] col = Column(m, t, n, best);
        double dsr = PerformanceStatistics.Dsr(sharpes[best], std, n, t, PerformanceStatistics.Skewness(col), PerformanceStatistics.Kurtosis(col));
        Assert.Equal(w.GetProperty("dsr").GetDouble(), dsr, 1e-12);
    }

    [Theory]
    [InlineData("pboS16", 16)]
    [InlineData("pboS8", 8)]
    public void Pbo_MatchesReferenceCscv(string key, int blocks)
    {
        JsonElement expected = Ref.GetProperty("sweep").GetProperty(key);
        (double[] m, int t, int n) = Sweep();
        PboResult r = Pbo.Cscv(m, t, n, blocks);
        Assert.Equal(expected.GetProperty("combinations").GetInt32(), r.Combinations);
        Assert.Equal(expected.GetProperty("pbo").GetDouble(), r.Probability, 1e-15);
        Assert.Equal(expected.GetProperty("probOosLoss").GetDouble(), r.ProbabilityOfOosLoss, 1e-15);
        Assert.Equal(expected.GetProperty("logitMean").GetDouble(), r.LogitMean, 1e-10);
        Assert.Equal(Doubles(expected.GetProperty("firstLogits")), r.Logits.Take(5), (a, b) => Math.Abs(a - b) < 1e-10);
    }

    [Fact]
    public void Pbo_OnPureNoise_MatchesReference_AndIsHigh()
    {
        JsonElement w = Ref.GetProperty("sweep");
        (double[] edge, int t, int n) = Sweep();
        // The reference noise matrix is the edge matrix without the +0.0015 drift in column 3.
        double[] noise = [.. edge];
        for (int i = 0; i < t; i++)
        {
            noise[(i * n) + 3] -= 0.0015;
        }

        PboResult r = Pbo.Cscv(noise, t, n, 8);
        Assert.Equal(w.GetProperty("pboNoiseS8").GetProperty("pbo").GetDouble(), r.Probability, 1e-15);
        Assert.True(r.Probability >= 0.5);
    }

    [Fact]
    public void PboArgumentsAreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Pbo.Cscv(new double[10], 10, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pbo.Cscv(new double[20], 10, 2, 7));
        Assert.Throws<ArgumentException>(() => Pbo.Cscv(new double[20], 10, 2, 8));
        Assert.Equal(16, Pbo.ChooseBlocks(2520));
        Assert.Equal(10, Pbo.ChooseBlocks(200));
        Assert.Throws<ArgumentException>(() => Pbo.ChooseBlocks(30));
        Assert.Equal(70, Pbo.Combinations(8, 4).Count());
        Assert.Equal([0, 1, 2, 3], Pbo.Combinations(8, 4).First());
        Assert.Equal([4, 5, 6, 7], Pbo.Combinations(8, 4).Last());
    }

    [Theory]
    [InlineData(100, 5, 0, 0)]
    [InlineData(100, 5, 3, 2)]
    [InlineData(103, 4, 5, 10)]
    public void PurgedKFold_NoTrainObservationTouchesTheTestLabels(int observations, int folds, int horizon, int embargo)
    {
        IReadOnlyList<TrainTestSplit> splits = CrossValidation.PurgedKFold(observations, folds, horizon, embargo);
        Assert.Equal(folds, splits.Count);
        Assert.Equal(Enumerable.Range(0, observations), splits.SelectMany(s => s.Test).Order());
        foreach (TrainTestSplit s in splits)
        {
            int start = s.Test[0], last = s.Test[^1];
            Assert.All(s.Train, i =>
            {
                Assert.False(i >= start && i <= last, "train inside the test fold");
                Assert.False(i < start && i + horizon >= start, "a training label reaches into the test fold");
                Assert.False(i > last && i <= last + horizon + embargo, "inside the purge/embargo after the test fold");
            });
            Assert.Equal(observations - s.Test.Count - PurgedCount(observations, start, last, horizon, embargo), s.Train.Count);
        }
    }

    [Fact]
    public void WalkForward_OnlyThePastTrainsTheFuture()
    {
        IReadOnlyList<TrainTestSplit> rolling = CrossValidation.WalkForward(100, trainSize: 40, testSize: 20, gap: 5);
        Assert.Equal(2, rolling.Count); // tests 45..64 and 65..84; 85..104 does not fit
        Assert.Equal((0, 39), (rolling[0].Train[0], rolling[0].Train[^1]));
        Assert.Equal((45, 64), (rolling[0].Test[0], rolling[0].Test[^1]));
        Assert.Equal((20, 59), (rolling[1].Train[0], rolling[1].Train[^1]));
        Assert.Equal((65, 84), (rolling[1].Test[0], rolling[1].Test[^1]));
        Assert.All(rolling, s => Assert.True(s.Train[^1] + 5 < s.Test[0]));

        IReadOnlyList<TrainTestSplit> anchored = CrossValidation.WalkForward(100, 40, 20, 0, anchored: true);
        Assert.Equal(3, anchored.Count);
        Assert.All(anchored, s => Assert.Equal(0, s.Train[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => CrossValidation.WalkForward(50, 40, 20));
    }

    [Fact]
    public void MaxDrawdown()
    {
        Assert.Equal(0.5, PerformanceStatistics.MaxDrawdown([100, 120, 60, 90, 130, 110]), 1e-15);
        Assert.Equal(0, PerformanceStatistics.MaxDrawdown([1, 2, 3]));
    }

    private static int PurgedCount(int observations, int start, int last, int horizon, int embargo)
    {
        int before = Math.Min(horizon, start); // i in [start - horizon, start)
        int after = Math.Min(horizon + embargo, observations - 1 - last);
        return before + after;
    }

    private static (double[] Matrix, int T, int N) Sweep()
    {
        JsonElement w = Ref.GetProperty("sweep");
        return (Doubles(w.GetProperty("matrixRowMajor")), w.GetProperty("t").GetInt32(), w.GetProperty("n").GetInt32());
    }

    private static double[] Column(double[] m, int t, int n, int j) => [.. Enumerable.Range(0, t).Select(i => m[(i * n) + j])];

    private static double StdDev(double[] x)
    {
        double mean = x.Average();
        return Math.Sqrt(x.Sum(v => (v - mean) * (v - mean)) / (x.Length - 1));
    }
}
