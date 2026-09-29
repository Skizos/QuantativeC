using System.Globalization;
using System.Runtime.InteropServices;
using QuantAnalyst.Analytics.Statistics;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Backtesting;

/// <summary>How targets become orders.</summary>
public sealed record ExecutionOptions
{
    public BacktestOrderType OrderType { get; init; } = BacktestOrderType.Limit;

    /// <summary>
    /// Gets the limit distance from the decision close, in basis points, toward the market (buy above, sell below).
    /// The limit is then rounded passively to the instrument's tick.
    /// </summary>
    public double LimitOffsetBps { get; init; } = 50;

    /// <summary>Gets the share of equity targets leave in cash (for costs).</summary>
    public double CashBuffer { get; init; } = 0.01;

    /// <summary>
    /// Gets the no-trade band: a trade smaller than this share of max(target, current) position value is skipped,
    /// so price drift does not cause churn. Entries (from zero) and exits (to zero) always trade.
    /// </summary>
    public double RebalanceBand { get; init; } = 0.10;

    /// <summary>Gets the smallest order value sent, except exits.</summary>
    public decimal MinTradeValue { get; init; }
}

/// <summary>One backtest to run.</summary>
public sealed record BacktestRequest
{
    public required MarketPanel Data { get; init; }

    public required StrategyDefinition Strategy { get; init; }

    public required CostModel Costs { get; init; }

    public required HoldoutPolicy Holdout { get; init; }

    public decimal InitialCash { get; init; } = 1_000_000m;

    public ExecutionOptions Execution { get; init; } = new();

    /// <summary>Gets the seed stored with the result (the synthetic data seed; 0 when nothing random was used).</summary>
    public ulong Seed { get; init; }

    /// <summary>Gets the ledger every run is appended to (null only in tests that check the runner alone).</summary>
    public TrialLedger? Ledger { get; init; }

    /// <summary>Gets who runs it (QA_RUNNER), recorded in the ledger.</summary>
    public string Runner { get; init; } = RunnerFromEnvironment();

    public string? GitCommit { get; init; }

    /// <summary>Gets how many decision points the leakage check replays on truncated data.</summary>
    public int LeakageCheckpoints { get; init; } = 8;

    public static string RunnerFromEnvironment() =>
        Environment.GetEnvironmentVariable("QA_RUNNER") is { Length: > 0 } r ? r : "unspecified";
}

/// <summary>The outcome of a run: the ledger record plus the series behind it.</summary>
public sealed record BacktestResult(
    TrialRecord Record,
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyList<double> Equity,
    IReadOnlyList<double> Returns,
    BacktestState FinalState,
    IReadOnlyList<long> Positions,
    double TradedNotional)
{
    public bool Ok => Record.Status == TrialStatus.Ok;
}

/// <summary>
/// Runs a strategy through the native engine bar by bar and logs the evaluation to the TrialLedger, whatever the
/// outcome (docs/plans/05-phase5-backtesting.md). Guards, in order:
/// <list type="number">
/// <item>the holdout: a locked holdout refuses the run (logged as rejected)</item>
/// <item>structural look-ahead: the strategy sees a <see cref="BarWindow"/> that throws past the current bar</item>
/// <item>the truncation check: at <see cref="BacktestRequest.LeakageCheckpoints"/> decision points a fresh strategy is
/// replayed on data cut at that bar; a different decision means it used later data (logged as rejected)</item>
/// </list>
/// Orders decided at the close of bar t are submitted with bar t+1, never earlier.
/// </summary>
public static class BacktestRunner
{
    public static BacktestResult Run(BacktestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        MarketPanel data = request.Data;
        DateOnly from = data.Dates[0], to = data.Dates[^1];
        bool touchesHoldout = request.Holdout.Touches(to);
        if (touchesHoldout && request.Holdout.Locked)
        {
            string note = $"refused: the data ends {to:yyyy-MM-dd}, inside the locked final holdout (from {request.Holdout.Start:yyyy-MM-dd}; {request.Holdout.Path})";
            return Finish(request, TrialStatus.RejectedHoldout, note, metrics: null, holdoutTouched: false, Empty(data));
        }

        Simulation sim;
        try
        {
            sim = Simulate(request);
        }
        catch (LookAheadException ex)
        {
            return Finish(request, TrialStatus.RejectedLeakage, ex.Message, null, touchesHoldout, Empty(data));
        }
        catch (StrategyException ex)
        {
            return Finish(request, TrialStatus.Failed, ex.Message, null, touchesHoldout, Empty(data));
        }

        string? leak;
        try
        {
            leak = CheckTruncation(request, sim.Checkpoints);
        }
        catch (LookAheadException ex)
        {
            leak = ex.Message;
        }

        if (leak is not null)
        {
            return Finish(request, TrialStatus.RejectedLeakage, leak, null, touchesHoldout, sim.ToOutcome());
        }

        TrialMetrics metrics = ComputeMetrics(request, sim);
        var notes = new List<string>();
        if (!request.Costs.Verified)
        {
            notes.Add($"costs UNVERIFIED: {request.Costs.Name} has no verified_on");
        }

        if (request.Costs.EligibleBelowCapital is { } limit && Array.FindIndex(sim.Equity, e => e >= (double)limit) is var t and >= 0)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"equity reached {limit:N0} SEK on {data.Dates[t]:yyyy-MM-dd}; {request.Costs.DisplayName ?? request.Costs.Name} can only be chosen below that, so fees after that date are likely understated"));
        }

        return Finish(request, TrialStatus.Ok, notes.Count == 0 ? null : string.Join("; ", notes), metrics, touchesHoldout, sim.ToOutcome());
    }

    /// <summary>Study key: trials with the same key form one family for the Deflated Sharpe Ratio.</summary>
    public static string StudyKey(MarketPanel data, StrategySpec strategy, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(strategy);
        string universe = data.Source.Name == SyntheticMarket.SourceName
            ? string.Create(CultureInfo.InvariantCulture, $"syn{data.InstrumentCount}s{seed}")
            : string.Join(",", data.Instruments.Select(i => i.Symbol));
        return string.Create(CultureInfo.InvariantCulture, $"{strategy.Name}|{universe}|{data.Dates[0]:yyyy-MM-dd}..{data.Dates[^1]:yyyy-MM-dd}|{data.Source.Name}");
    }

    private static Simulation Simulate(BacktestRequest request)
    {
        MarketPanel data = request.Data;
        int periods = data.Periods, n = data.InstrumentCount;
        var instruments = new BacktestInstrument[n];
        for (int i = 0; i < n; i++)
        {
            instruments[i] = new BacktestInstrument(data.Instruments[i].LotSize, data.Instruments[i].ForeignCurrency);
        }

        using QeBacktest engine = QeBacktest.Create(request.Costs.ToEngineConfig(request.InitialCash), instruments);
        for (int i = 0; i < n; i++)
        {
            // ADR 0005: a US or Canadian share pays its market's courtage; the minimum (e.g. 1 USD) in SEK at the last fixing.
            PanelInstrument p = data.Instruments[i];
            if (p.ForeignCurrency && p.LastSekPerUnit is { } fx)
            {
                ForeignCourtage c = request.Costs.ForeignFor(p.Currency)
                    ?? throw new BacktestConfigException($"The courtage class {request.Costs.Name} has no courtage for {p.Currency} shares ({p.Symbol}); add foreign_courtage.{p.Currency} to costs.{request.Costs.Name}.json (ADR 0005).");
                engine.SetCourtage(i, (double)(c.Min * fx), (double)c.Rate);
            }
        }

        IStrategy strategy = request.Strategy.Factory(data);
        var window = new BarWindow(data);
        var bars = new BacktestBar[n];
        var fills = new BacktestFill[n];
        var positions = new long[n];
        var targets = new double[n];
        var orders = new List<BacktestOrder>(n);
        var pending = new List<BacktestOrder>(n);
        var equity = new double[periods];
        var checkpoints = LeakageCheckpoints(periods, request.LeakageCheckpoints).ToDictionary(t => t, _ => Array.Empty<double>());
        double traded = 0;
        BacktestState state = default;

        for (int t = 0; t < periods; t++)
        {
            for (int i = 0; i < n; i++)
            {
                bars[i] = data.Bar(t, i);
            }

            state = engine.Step(bars, CollectionsMarshal.AsSpan(pending), fills, out int filled);
            for (int k = 0; k < filled; k++)
            {
                traded += fills[k].Quantity * fills[k].Price;
            }

            equity[t] = state.Equity;
            if (t == periods - 1)
            {
                break; // nothing trades after the last bar
            }

            window.MoveTo(t);
            Array.Fill(targets, double.NaN);
            strategy.Decide(window, targets);
            if (checkpoints.ContainsKey(t))
            {
                checkpoints[t] = (double[])targets.Clone();
            }

            engine.GetPositions(positions);
            PlanOrders(data, t, targets, state.Equity, positions, request.Execution, orders);
            (pending, orders) = (orders, pending);
        }

        engine.GetPositions(positions);
        return new Simulation(equity, state, positions, traded, checkpoints);
    }

    /// <summary>Targets at the close of bar t → day orders for bar t+1 (whole lots; limits rounded passively to the tick).</summary>
    internal static void PlanOrders(
        MarketPanel data, int t, ReadOnlySpan<double> targets, double equity, ReadOnlySpan<long> positions, ExecutionOptions execution, List<BacktestOrder> orders)
    {
        orders.Clear();
        double sum = 0;
        foreach (double w in targets)
        {
            if (double.IsNaN(w))
            {
                continue;
            }

            if (!double.IsFinite(w) || w < 0)
            {
                throw new StrategyException($"Target weights must be finite and >= 0 (long-only); got {w} at {data.Dates[t]:yyyy-MM-dd}.");
            }

            sum += w;
        }

        if (sum > 1 + 1e-9)
        {
            throw new StrategyException($"Target weights sum to {sum:0.######} > 1 at {data.Dates[t]:yyyy-MM-dd} (no leverage).");
        }

        double investable = Math.Max(0, equity) * (1 - execution.CashBuffer);
        for (int i = 0; i < targets.Length; i++)
        {
            double w = targets[i];
            if (double.IsNaN(w) || !data.IsValid(t, i))
            {
                continue; // hold, or no price today
            }

            PanelInstrument instrument = data.Instruments[i];
            double price = data.Bar(t, i).Close;
            long lot = instrument.LotSize;
            long target = (long)Math.Floor(w * investable / price / lot) * lot;
            long current = positions[i];
            long delta = target - current;
            if (delta == 0)
            {
                continue;
            }

            double value = Math.Abs(delta) * price;
            bool entryOrExit = target == 0 || current == 0;
            if ((!entryOrExit && value < execution.RebalanceBand * Math.Max(target, current) * price)
                || (target != 0 && value < (double)execution.MinTradeValue))
            {
                continue; // inside the no-trade band, or too small (exits always go)
            }

            BacktestSide side = delta > 0 ? BacktestSide.Buy : BacktestSide.Sell;
            double limit = 0;
            if (execution.OrderType == BacktestOrderType.Limit)
            {
                double offset = execution.LimitOffsetBps / 10_000;
                double raw = price * (side == BacktestSide.Buy ? 1 + offset : 1 - offset);
                try
                {
                    limit = (double)instrument.TickSizes.RoundForOrder(
                        (decimal)raw, side == BacktestSide.Buy ? Core.OrderSide.Buy : Core.OrderSide.Sell);
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue; // no valid tick at this price (outside the table)
                }
            }

            orders.Add(new BacktestOrder(i, side, execution.OrderType, Math.Abs(delta), limit));
        }
    }

    /// <summary>Decision points the truncation check replays: evenly spaced, always including the last decision.</summary>
    internal static IReadOnlyList<int> LeakageCheckpoints(int periods, int count)
    {
        int lastDecision = periods - 2; // decisions happen at bars 0..T-2
        if (lastDecision < 0 || count < 1)
        {
            return [];
        }

        return [.. Enumerable.Range(1, count).Select(k => (int)Math.Round((double)k * lastDecision / count)).Distinct()];
    }

    private static string? CheckTruncation(BacktestRequest request, IReadOnlyDictionary<int, double[]> decisions)
    {
        MarketPanel data = request.Data;
        var replay = new double[data.InstrumentCount];
        foreach ((int t, double[] full) in decisions.OrderBy(kv => kv.Key))
        {
            MarketPanel truncated = data.Truncate(t + 1);
            IStrategy fresh = request.Strategy.Factory(truncated);
            var window = new BarWindow(truncated);
            for (int u = 0; u <= t; u++)
            {
                window.MoveTo(u);
                Array.Fill(replay, double.NaN);
                fresh.Decide(window, replay);
            }

            for (int i = 0; i < replay.Length; i++)
            {
                if (!replay[i].Equals(full[i]))
                {
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"look-ahead: the decision at the close of {data.Dates[t]:yyyy-MM-dd} for {data.Instruments[i].Symbol} was {full[i]:R} on the full data but {replay[i]:R} on data ending that day");
                }
            }
        }

        return null;
    }

    private static TrialMetrics ComputeMetrics(BacktestRequest request, Simulation sim)
    {
        double[] equity = sim.Equity;
        double[] returns = Returns(equity);
        int obs = returns.Length;
        double sharpe = PerformanceStatistics.Sharpe(returns);
        double skew = PerformanceStatistics.Skewness(returns);
        double kurt = PerformanceStatistics.Kurtosis(returns);
        double initial = (double)request.InitialCash;
        double totalReturn = (equity[^1] / initial) - 1;
        double years = obs / (double)PerformanceStatistics.TradingDaysPerYear;
        double cagr = years > 0 && totalReturn > -1 ? Math.Pow(1 + totalReturn, 1 / years) - 1 : double.NaN;
        double vol = obs >= 2 ? StdDev(returns) * Math.Sqrt(PerformanceStatistics.TradingDaysPerYear) : double.NaN;
        double meanEquity = equity.Average();
        double turnover = years > 0 && meanEquity > 0 ? sim.TradedNotional / (2 * meanEquity) / years : double.NaN;
        double psr0 = obs >= 2 && double.IsFinite(sharpe) ? PerformanceStatistics.Psr(sharpe, obs, skew, kurt) : double.NaN;

        // Deflate against every completed trial of the same study, this one included.
        string study = StudyKey(request.Data, request.Strategy.Spec, request.Seed);
        List<double> sharpes = request.Ledger is null ? [] : [.. request.Ledger.StudySharpes(study)];
        double? dsr = null;
        if (double.IsFinite(sharpe))
        {
            sharpes.Add(sharpe);
            double spread = sharpes.Count >= 2 ? StdDev([.. sharpes]) : double.NaN;
            if (sharpes.Count >= 2 && spread > 0 && obs >= 2)
            {
                double d = PerformanceStatistics.Dsr(sharpe, spread, sharpes.Count, obs, skew, kurt);
                dsr = double.IsFinite(d) ? d : null;
            }
        }

        return new TrialMetrics(
            obs,
            sharpe,
            sharpe * Math.Sqrt(PerformanceStatistics.TradingDaysPerYear),
            skew,
            kurt,
            totalReturn,
            cagr,
            vol,
            PerformanceStatistics.MaxDrawdown(equity),
            turnover,
            sim.FinalState.TotalCosts,
            psr0,
            dsr,
            sharpes.Count,
            Pbo: null);
    }

    private static BacktestResult Finish(
        BacktestRequest request, TrialStatus status, string? note, TrialMetrics? metrics, bool holdoutTouched, Outcome outcome)
    {
        MarketPanel data = request.Data;
        var record = new TrialRecord
        {
            Id = "unlogged",
            RecordedAtUtc = DateTimeOffset.UtcNow,
            Runner = request.Runner,
            GitCommit = request.GitCommit,
            Study = StudyKey(data, request.Strategy.Spec, request.Seed),
            Strategy = request.Strategy.Spec.Name,
            Parameters = new SortedDictionary<string, string>(request.Strategy.Spec.Parameters.ToDictionary(), StringComparer.Ordinal),
            Universe = data.Source.Name == SyntheticMarket.SourceName && data.InstrumentCount > 1
                ? [$"{data.Instruments[0].Symbol}..{data.Instruments[^1].Symbol}"]
                : [.. data.Instruments.Select(i => i.Symbol)],
            DataSource = data.Source.Name,
            PointInTime = data.Source.PointInTime,
            SurvivorshipFree = data.Source.SurvivorshipFree,
            From = data.Dates[0],
            To = data.Dates[^1],
            Seed = request.Seed,
            CostModel = request.Costs.Name,
            CostsVerified = request.Costs.Verified,
            HoldoutTouched = holdoutTouched,
            Status = status,
            Note = note,
            Metrics = metrics,
        };
        if (request.Ledger is not null)
        {
            record = request.Ledger.Append(record);
        }

        return new BacktestResult(record, data.Dates, outcome.Equity, outcome.Returns, outcome.State, outcome.Positions, outcome.Traded);
    }

    private static double[] Returns(double[] equity)
    {
        var r = new double[Math.Max(0, equity.Length - 1)];
        for (int t = 1; t < equity.Length; t++)
        {
            r[t - 1] = (equity[t] / equity[t - 1]) - 1;
        }

        return r;
    }

    private static double StdDev(double[] values)
    {
        double mean = values.Average();
        double ss = 0;
        foreach (double v in values)
        {
            ss += (v - mean) * (v - mean);
        }

        return Math.Sqrt(ss / (values.Length - 1));
    }

    private static Outcome Empty(MarketPanel data) => new([], [], default, new long[data.InstrumentCount], 0);

    private sealed record Outcome(double[] Equity, double[] Returns, BacktestState State, long[] Positions, double Traded);

    private sealed record Simulation(
        double[] Equity, BacktestState FinalState, long[] Positions, double TradedNotional, Dictionary<int, double[]> Checkpoints)
    {
        public Outcome ToOutcome() => new(Equity, Returns(Equity), FinalState, Positions, TradedNotional);
    }
}

/// <summary>A strategy produced invalid targets (negative, non-finite or leveraged); the run is logged as failed.</summary>
public sealed class StrategyException(string message) : Exception(message);
