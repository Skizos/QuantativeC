using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Tests;

/// <summary>Plan 27: inverse volatility and risk parity weigh shares by their risk, and stay invested.</summary>
public sealed class RiskStrategiesTests
{
    /// <summary>Shares with exact daily return patterns: ±1 % alternating, ±2 % alternating, or ±1 % in pairs (+,+,−,−).</summary>
    private static MarketPanel Panel(int days, params Func<int, decimal>[] patterns)
    {
        var series = new List<(PanelInstrument, IReadOnlyList<DailyBar>)>();
        for (int i = 0; i < patterns.Length; i++)
        {
            var bars = new List<DailyBar>();
            decimal close = 100m;
            for (int t = 0; t < days; t++)
            {
                if (t > 0)
                {
                    close *= 1 + patterns[i](t);
                }

                bars.Add(new DailyBar(new DateOnly(2024, 1, 1).AddDays(t), close, close * 1.03m, close * 0.97m, close, 10_000));
            }

            series.Add((new PanelInstrument($"S{i}", 1, false, SyntheticMarket.CentTicks), bars));
        }

        return MarketPanel.FromDailyBars(series, new DataSourceInfo("hand-made", true, true, "test"));
    }

    private static decimal Alternating(int t) => t % 2 == 0 ? 0.01m : -0.01m;

    private static decimal AlternatingTwice(int t) => t % 2 == 0 ? 0.02m : -0.02m;

    private static decimal Pairs(int t) => t % 4 is 0 or 1 ? 0.01m : -0.01m;

    private static double[] Decide(IStrategy strategy, MarketPanel panel) => StrategyReplay.DecideAtLastBar(panel, strategy);

    [Fact]
    public void InverseVolatility_GivesTheCalmShareTwiceTheWeight_AndNothingBeforeItHasHistory()
    {
        MarketPanel panel = Panel(100, Alternating, AlternatingTwice);
        double[] w = Decide(new InverseVolatility(lookback: 20, rebalance: 5), panel);
        Assert.Equal(2.0 / 3, w[0], 9);
        Assert.Equal(1.0 / 3, w[1], 9);

        Assert.Equal([0.0, 0.0], Decide(new InverseVolatility(lookback: 20, rebalance: 5), panel.Truncate(15))); // 14 returns: not enough
    }

    [Fact]
    public void BetweenRebalances_TheWeightsStay_AndAreStatedEveryBar()
    {
        var strategy = new InverseVolatility(lookback: 20, rebalance: 10);
        MarketPanel panel = Panel(60, Alternating, Pairs);
        var window = new BarWindow(panel);
        var targets = new double[2];
        var seen = new List<double[]>();
        for (int t = 0; t < panel.Periods; t++)
        {
            window.MoveTo(t);
            Array.Fill(targets, double.NaN);
            strategy.Decide(window, targets);
            seen.Add([.. targets]);
        }

        Assert.All(seen.Skip(20), w => Assert.False(double.IsNaN(w[0]) || double.IsNaN(w[1]))); // never "hold": a new book follows
        Assert.All(seen.Skip(20), w => Assert.Equal(1.0, w[0] + w[1], 9)); // fully invested
        Assert.Equal(seen[31], seen[39]); // between the rebalances at 30 and 40 nothing changes
    }

    [Fact]
    public void RiskParity_GivesTwoSharesThatMoveTogether_LessThanOneThatMovesAlone()
    {
        // S0 and S1 are the same share; S2 is as volatile but unrelated. Inverse volatility splits a third each; equal risk
        // contribution sees that S0 and S1 are one risk and gives S2 more (about 0.41 against 0.29 each without shrinkage).
        MarketPanel panel = Panel(120, Alternating, Alternating, Pairs);
        double[] iv = Decide(new InverseVolatility(lookback: 40, rebalance: 1), panel);
        Assert.All(iv, w => Assert.Equal(1.0 / 3, w, 6));

        using var parity = new RiskParity(lookback: 40, rebalance: 1);
        double[] rp = Decide(parity, panel);
        Assert.Equal(1.0, rp.Sum(), 9);
        Assert.Equal(rp[0], rp[1], 9);
        Assert.True(rp[2] > rp[0] + 0.05, $"S2 {rp[2]:0.000} should clearly outweigh S0 {rp[0]:0.000}");
    }

    [Fact]
    public void RiskParity_LeavesOutAShareWithoutACloseOnEveryDay()
    {
        MarketPanel full = Panel(80, Alternating, AlternatingTwice);
        // A third share listed only for the last 30 days: not enough history for the 40-day window.
        var late = new List<DailyBar>();
        for (int t = 50; t < 80; t++)
        {
            late.Add(new DailyBar(new DateOnly(2024, 1, 1).AddDays(t), 50m, 51m, 49m, 50m, 1_000));
        }

        MarketPanel panel = MarketPanel.FromDailyBars(
            [.. Enumerable.Range(0, 2).Select(i => (full.Instruments[i], (IReadOnlyList<DailyBar>)[.. Enumerable.Range(0, full.Periods).Select(t => Bar(full, t, i))])),
             (new PanelInstrument("LATE", 1, false, SyntheticMarket.CentTicks), late)],
            new DataSourceInfo("hand-made", true, true, "test"));

        double[] w = StrategyReplay.DecideAtLastBar(panel, StrategyCatalog.Create("risk-parity", new Dictionary<string, string> { ["lookback"] = "40", ["rebalance"] = "1" }).Factory);
        Assert.Equal(0.0, w[2]);
        Assert.True(w[0] > w[1]); // the calmer share weighs more
        Assert.Equal(1.0, w[0] + w[1], 9);
    }

    [Theory]
    [InlineData("inverse-vol", "lookback", "10")]
    [InlineData("risk-parity", "rebalance", "0")]
    public void TheCatalog_RefusesSillyParameters(string name, string key, string value) =>
        Assert.Throws<ArgumentException>(() => StrategyCatalog.Create(name, new Dictionary<string, string> { [key] = value }));

    private static DailyBar Bar(MarketPanel p, int t, int i)
    {
        BacktestBar b = p.Bar(t, i);
        return new DailyBar(p.Dates[t], (decimal)b.Open, (decimal)b.High, (decimal)b.Low, (decimal)b.Close, (long)b.Volume);
    }
}
