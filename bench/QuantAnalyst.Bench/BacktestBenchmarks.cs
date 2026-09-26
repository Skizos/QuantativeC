using BenchmarkDotNet.Attributes;
using QuantAnalyst.Analytics.Backtesting;

namespace QuantAnalyst.Bench;

/// <summary>
/// Phase 5 gate: a 10-year × 300-instrument MA-cross(20, 100) backtest on synthetic bars (2520 × 300), the same
/// configuration as <c>qa backtest run --strategy ma-cross --param fast=20 --param slow=100 --synthetic 300x2520</c>.
/// Timing only: BenchmarkDotNet repeats the run many times, so its iterations are not written to the TrialLedger
/// (the one CLI run of this configuration is). Costs and holdout come from the repository's config folder.
/// </summary>
[MemoryDiagnoser]
public class BacktestBenchmarks
{
    private BacktestRequest request = null!;

    [GlobalSetup]
    public void Setup()
    {
        string config = FindConfig();
        MarketPanel data = SyntheticMarket.Generate(new SyntheticMarketOptions { Instruments = 300, Periods = 2520, Seed = 20260925 });
        request = new BacktestRequest
        {
            Data = data,
            Strategy = StrategyCatalog.Create("ma-cross", new Dictionary<string, string> { ["fast"] = "20", ["slow"] = "100" }),
            Costs = CostModel.Load(Path.Combine(config, "costs.avanza-small.json")),
            Holdout = HoldoutPolicy.Load(Path.Combine(config, HoldoutPolicy.FileName)),
            Seed = 20260925,
            Ledger = null,
            Runner = "benchmark",
        };
    }

    /// <summary>Everything a run does: 2520 engine steps, 2519 decisions, order planning, 8 truncation replays, metrics.</summary>
    [Benchmark(Baseline = true)]
    public double MaCross300x2520() => BacktestRunner.Run(request).Record.Metrics!.SharpePerPeriod;

    /// <summary>The same run without the leakage check (its cost is the difference).</summary>
    [Benchmark]
    public double MaCrossWithoutLeakageCheck() => BacktestRunner.Run(request with { LeakageCheckpoints = 0 }).Record.Metrics!.SharpePerPeriod;

    private static string FindConfig()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "QuantAnalyst.sln")))
            {
                return Path.Combine(d.FullName, "config");
            }
        }

        throw new InvalidOperationException("Run the benchmark from inside the repository (config/ is needed).");
    }
}
