using QuantAnalyst.Analytics.Statistics;

namespace QuantAnalyst.Analytics.Backtesting;

/// <summary>The outcome of a parameter sweep.</summary>
/// <param name="Trials">Every configuration's result, in grid order (each one is in the ledger).</param>
/// <param name="Pbo">Probability of backtest overfitting across the completed configurations (null with fewer than 2, or too few bars).</param>
/// <param name="Best">The completed configuration with the highest per-period Sharpe ratio.</param>
/// <param name="BestDsr">The best configuration's Deflated Sharpe Ratio against the whole study as it stands after the sweep.</param>
/// <param name="StudyTrials">Completed trials in the study after the sweep (earlier runs included).</param>
public sealed record SweepResult(IReadOnlyList<BacktestResult> Trials, PboResult? Pbo, BacktestResult? Best, double? BestDsr, int StudyTrials);

/// <summary>Runs every combination of a parameter grid through <see cref="BacktestRunner"/>, then PBO and DSR of the best.</summary>
public static class BacktestSweep
{
    /// <summary>CSCV needs at least 2 blocks of 20 periods.</summary>
    public const int MinPeriodsForPbo = 40;

    /// <summary>All combinations of the grid values, plus the fixed parameters, in a stable order (last key varies fastest).</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ExpandGrid(
        IReadOnlyDictionary<string, IReadOnlyList<string>> grid, IReadOnlyDictionary<string, string>? fixedParameters = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        IEnumerable<SortedDictionary<string, string>> combos =
            [new SortedDictionary<string, string>(fixedParameters?.ToDictionary() ?? [], StringComparer.Ordinal)];
        foreach ((string key, IReadOnlyList<string> values) in grid.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (values.Count == 0)
            {
                throw new ArgumentException($"Grid parameter '{key}' has no values.", nameof(grid));
            }

            if (fixedParameters?.ContainsKey(key) == true)
            {
                throw new ArgumentException($"'{key}' is both fixed and in the grid.", nameof(grid));
            }

            combos = [.. combos.SelectMany(c => values.Select(v => new SortedDictionary<string, string>(c, StringComparer.Ordinal) { [key] = v }))];
        }

        return [.. combos];
    }

    public static SweepResult Run(BacktestRequest template, IReadOnlyList<StrategyDefinition> configurations, Action<BacktestResult>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(configurations);
        if (configurations.Select(c => c.Spec.Name).Distinct().Count() > 1)
        {
            throw new ArgumentException("A sweep varies the parameters of one strategy.", nameof(configurations));
        }

        var results = new List<BacktestResult>(configurations.Count);
        foreach (StrategyDefinition configuration in configurations)
        {
            BacktestResult r = BacktestRunner.Run(template with { Strategy = configuration });
            results.Add(r);
            progress?.Invoke(r);
        }

        BacktestResult[] ok = [.. results.Where(r => r.Ok && r.Record.Metrics is { } m && double.IsFinite(m.SharpePerPeriod))];
        PboResult? pbo = null;
        if (ok.Length >= 2)
        {
            int periods = ok[0].Returns.Count;
            if (periods >= MinPeriodsForPbo)
            {
                int blocks = Pbo.ChooseBlocks(periods);
                var matrix = new double[periods * ok.Length];
                for (int j = 0; j < ok.Length; j++)
                {
                    for (int t = 0; t < periods; t++)
                    {
                        matrix[(t * ok.Length) + j] = ok[j].Returns[t];
                    }
                }

                pbo = Pbo.Cscv(matrix, periods, ok.Length, blocks);
            }
        }

        BacktestResult? best = ok.Length == 0 ? null : ok.MaxBy(r => r.Record.Metrics!.SharpePerPeriod);
        double? bestDsr = null;
        int studyTrials = ok.Length;
        if (best is not null)
        {
            TrialMetrics m = best.Record.Metrics!;
            double[] sharpes = template.Ledger?.StudySharpes(best.Record.Study) ?? [.. ok.Select(r => r.Record.Metrics!.SharpePerPeriod)];
            studyTrials = sharpes.Length;
            if (sharpes.Length >= 2)
            {
                double spread = TrialLedger.StdDev(sharpes);
                double d = PerformanceStatistics.Dsr(m.SharpePerPeriod, spread, sharpes.Length, m.Observations, m.Skewness, m.Kurtosis);
                bestDsr = double.IsFinite(d) ? d : null;
            }
        }

        return new SweepResult(results, pbo, best, bestDsr, studyTrials);
    }
}
