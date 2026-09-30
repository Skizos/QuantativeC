using System.Globalization;
using QuantAnalyst.Analytics.Statistics;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Backtesting;

/// <summary>What the go/no-go report is asked (plan 17 step A6).</summary>
public sealed record IntradayReportRequest
{
    /// <summary>Gets the runs' template: the data before the intraday holdout, the costs, cash, holdout policy and ledger.</summary>
    public required BacktestRequest Template { get; init; }

    public required IntradayClock Clock { get; init; }

    /// <summary>Gets the holdout's own bars, only once the owner has unlocked it; null while it is locked.</summary>
    public MarketPanel? HoldoutData { get; init; }
}

/// <summary>A criterion of the go bar: met, not met, or not decidable yet.</summary>
public enum CriterionStatus
{
    Pass,
    Fail,
    Wait,
}

public sealed record Criterion(string Name, CriterionStatus Status, string Detail);

/// <summary>Round trips (one name, one day: in and out) and their net returns.</summary>
/// <param name="ClusteredT">The mean's t-statistic with standard errors clustered by day (trades on one day move together).</param>
public sealed record TradeStatistics(int RoundTrips, int Days, double Mean, double ClusteredT, double WinRate);

/// <summary>A strategy's daily P&amp;L at one courtage class.</summary>
public sealed record CostView(string Strategy, string Costs, double SharpePerDay, double TotalReturn, double Courtage)
{
    public double SharpeAnnualised => SharpePerDay * Math.Sqrt(PerformanceStatistics.TradingDaysPerYear);
}

/// <summary>Walk-forward over the ORB grid: pick the best range on the days before, trade it on the next block.</summary>
public sealed record WalkForwardSummary(int TrainDays, int TestDays, int OutOfSampleDays, double SharpePerDay, double MeanDaily, IReadOnlyDictionary<string, int> Picks)
{
    public double SharpeAnnualised => SharpePerDay * Math.Sqrt(PerformanceStatistics.TradingDaysPerYear);
}

/// <summary>The holdout check, once the owner has unlocked it: the chosen range and the baseline on the held-out days only.</summary>
public sealed record HoldoutCheck(BacktestResult Candidate, BacktestResult Baseline, CostView? CandidateAfter, CostView? BaselineAfter, int Days);

public sealed record IntradayReportResult(
    int Days,
    SweepResult Grid,
    BacktestResult LateMomentum,
    BacktestResult OpenClose,
    CostModel After,
    CostView? CandidateAfter,
    CostView? OpenCloseAfter,
    TradeStatistics? Trades,
    WalkForwardSummary? WalkForward,
    double? FillsPerDay,
    HoldoutCheck? Holdout,
    IReadOnlyList<Criterion> Criteria,
    string Verdict)
{
    /// <summary>Gets the ORB configuration with the best in-sample daily Sharpe (null when every grid run failed).</summary>
    public BacktestResult? Candidate => Grid.Best;
}

/// <summary>
/// The go/no-go report (plan 17 step A6, ADR 0006). A fixed set of runs, every one logged to the TrialLedger:
/// <list type="bullet">
/// <item>the ORB grid of the plan (range 5, 15 and 30 minutes, the rest at the defaults) at the configured costs
/// (Start and its free trades by default): the best in-sample daily Sharpe is the candidate, with its Deflated Sharpe
/// over the whole study and the grid's PBO</item>
/// <item>the controls, late-momentum and open-close</item>
/// <item>the candidate and open-close re-costed at the class after the free trades (Mini): no new runs</item>
/// <item>per-trade net returns at Mini with a day-clustered t-statistic; walk-forward over the grid</item>
/// <item>once the owner unlocks the holdout: the candidate and open-close on the held-out days</item>
/// </list>
/// The bar is plan 17's proposal; the owner decides.
/// </summary>
public static class IntradayReport
{
    public const int MinDays = 120;

    public const double MinDsr = 0.95;

    public const double MaxPbo = 0.2;

    public const int WalkForwardTrain = 60;

    public const int WalkForwardTest = 20;

    /// <summary>Gets the plan's ORB grid: the opening range in minutes.</summary>
    public static IReadOnlyList<int> OrbRanges { get; } = [5, 15, 30];

    public static IntradayReportResult Run(IntradayReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        BacktestRequest template = request.Template;
        MarketPanel data = template.Data;
        if (!data.IsIntraday)
        {
            throw new ArgumentException("The intraday report needs intraday bars.", nameof(request));
        }

        StrategyDefinition[] grid = [.. OrbRanges.Select(r => Define("orb-long", request.Clock, ("range", r.ToString(CultureInfo.InvariantCulture))))];
        SweepResult sweep = BacktestSweep.Run(template, grid);
        BacktestResult late = BacktestRunner.Run(template with { Strategy = Define("late-momentum", request.Clock) });
        StrategyDefinition baseline = Define("open-close", request.Clock);
        BacktestResult openClose = BacktestRunner.Run(template with { Strategy = baseline });

        CostModel after = template.Costs.FreeTrades?.Then ?? template.Costs;
        BacktestResult? candidate = sweep.Best;
        CostView? candidateAfter = candidate is null ? null : View(template, candidate, after);
        CostView? openCloseAfter = openClose.Ok ? View(template, openClose, after) : null;
        TradeStatistics? trades = candidate is null ? null : Trades(data, candidate.Fills, after);
        int days = data.Dates.Distinct().Count();
        double? fillsPerDay = candidate is null ? null : (double)candidate.Fills.Count / days;

        HoldoutCheck? holdout = null;
        if (request.HoldoutData is { } held && candidate is not null)
        {
            StrategyDefinition chosen = IntradayStrategyCatalog.Create("orb-long", candidate.Record.Parameters, request.Clock);
            BacktestRequest onHoldout = template with { Data = held };
            BacktestResult c = BacktestRunner.Run(onHoldout with { Strategy = chosen });
            BacktestResult b = BacktestRunner.Run(onHoldout with { Strategy = baseline });
            holdout = new HoldoutCheck(
                c, b, c.Ok ? View(onHoldout, c, after) : null, b.Ok ? View(onHoldout, b, after) : null, held.Dates.Distinct().Count());
        }

        WalkForwardSummary? walk = WalkForward(sweep);
        IReadOnlyList<Criterion> criteria = Criteria(days, sweep, trades, candidateAfter, openCloseAfter, holdout, template.Holdout, after);
        return new IntradayReportResult(days, sweep, late, openClose, after, candidateAfter, openCloseAfter, trades, walk, fillsPerDay, holdout, criteria, Verdict(criteria));
    }

    /// <summary>
    /// Round trips from fills: per share and day, everything bought against everything sold (each name enters at most
    /// once a day and is flat by the close). Each fill's price already carries the spread and slippage; courtage is
    /// <paramref name="courtageAt"/>'s on Swedish fills (a foreign fill keeps what it paid).
    /// </summary>
    public static TradeStatistics? Trades(MarketPanel data, IReadOnlyList<TimedFill> fills, CostModel courtageAt)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(courtageAt);
        var trips = new List<(DateOnly Day, double Return)>();
        foreach (IGrouping<(int Instrument, DateOnly Day), TimedFill> g in fills.GroupBy(f => (f.Fill.Instrument, data.Dates[f.Bar])))
        {
            double bought = 0, sold = 0, boughtQty = 0, soldQty = 0;
            foreach (TimedFill f in g)
            {
                double value = f.Fill.Quantity * f.Fill.Price;
                PanelInstrument instrument = data.Instruments[f.Fill.Instrument];
                double fee = (instrument.ForeignCurrency ? f.Fill.Courtage : (double)courtageAt.CourtageIn(instrument.Currency, (decimal)value, instrument.MarketPlace)) + f.Fill.FxFee;
                if (f.Fill.Side == BacktestSide.Buy)
                {
                    bought += value + fee;
                    boughtQty += f.Fill.Quantity;
                }
                else
                {
                    sold += value - fee;
                    soldQty += f.Fill.Quantity;
                }
            }

            if (boughtQty > 0 && boughtQty == soldQty)
            {
                trips.Add((g.Key.Day, (sold / bought) - 1));
            }
        }

        if (trips.Count == 0)
        {
            return null;
        }

        double mean = trips.Average(t => t.Return);
        var clusters = trips.GroupBy(t => t.Day).Select(g => g.Sum(t => t.Return - mean)).ToArray();
        int n = trips.Count, groups = clusters.Length;
        double t = double.NaN;
        if (groups >= 2)
        {
            double variance = (double)groups / (groups - 1) * clusters.Sum(s => s * s) / ((double)n * n);
            t = variance > 0 ? mean / Math.Sqrt(variance) : double.NaN;
        }

        return new TradeStatistics(n, groups, mean, t, trips.Count(x => x.Return > 0) / (double)n);
    }

    private static StrategyDefinition Define(string name, IntradayClock clock, params (string Key, string Value)[] parameters) =>
        IntradayStrategyCatalog.Create(name, parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), clock);

    private static CostView View(BacktestRequest request, BacktestResult result, CostModel costs)
    {
        RecostedRun r = BacktestRunner.Recost(request, result, costs);
        double total = r.Equity[^1] / (double)request.InitialCash - 1;
        return new CostView(Describe(result), costs.DisplayName ?? costs.Name, PerformanceStatistics.Sharpe([.. r.DailyReturns]), total, r.Courtage);
    }

    /// <summary>"orb-long range=15".</summary>
    public static string Describe(BacktestResult r) =>
        r.Record.Strategy + (r.Record.Strategy == "orb-long" ? $" range={r.Record.Parameters["range"]}" : string.Empty);

    private static WalkForwardSummary? WalkForward(SweepResult sweep)
    {
        BacktestResult[] ok = [.. sweep.Trials.Where(t => t.Ok)];
        if (ok.Length == 0)
        {
            return null;
        }

        int days = ok[0].DailyReturns.Count;
        if (days < WalkForwardTrain + WalkForwardTest)
        {
            return null;
        }

        var outOfSample = new List<double>();
        var picks = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (TrainTestSplit split in CrossValidation.WalkForward(days, WalkForwardTrain, WalkForwardTest, anchored: true))
        {
            BacktestResult best = ok.MaxBy(r => PerformanceStatistics.Sharpe([.. split.Train.Select(t => r.DailyReturns[t])]))!;
            string key = Describe(best);
            picks[key] = picks.GetValueOrDefault(key) + 1;
            outOfSample.AddRange(split.Test.Select(t => best.DailyReturns[t]));
        }

        return new WalkForwardSummary(WalkForwardTrain, WalkForwardTest, outOfSample.Count, PerformanceStatistics.Sharpe([.. outOfSample]), outOfSample.Average(), picks);
    }

    private static List<Criterion> Criteria(
        int days, SweepResult sweep, TradeStatistics? trades, CostView? candidate, CostView? baseline, HoldoutCheck? holdout, HoldoutPolicy policy, CostModel after)
    {
        string at = after.DisplayName ?? after.Name;
        var list = new List<Criterion>
        {
            new($"at least {MinDays} trading days", days >= MinDays ? CriterionStatus.Pass : CriterionStatus.Wait, $"{days} before the holdout"),
            sweep.BestDsr is { } dsr
                ? new($"Deflated Sharpe >= {MinDsr:0.00}", dsr >= MinDsr ? CriterionStatus.Pass : CriterionStatus.Fail, string.Create(CultureInfo.InvariantCulture, $"{dsr:0.000} over {sweep.StudyTrials} trials"))
                : new($"Deflated Sharpe >= {MinDsr:0.00}", sweep.Best is null ? CriterionStatus.Fail : CriterionStatus.Wait, sweep.Best is null ? "no completed ORB run" : "n/a (too few trials or days)"),
            sweep.Pbo is { } pbo
                ? new($"PBO <= {MaxPbo:0.0}", pbo.Probability <= MaxPbo ? CriterionStatus.Pass : CriterionStatus.Fail, string.Create(CultureInfo.InvariantCulture, $"{pbo.Probability:0.000} over {sweep.Trials.Count(t => t.Ok)} ranges"))
                : new($"PBO <= {MaxPbo:0.0}", CriterionStatus.Wait, $"n/a (needs {BacktestSweep.MinPeriodsForPbo}+ days)"),
            trades is { } tr
                ? new($"net per trade > 0 at {at}", tr.Mean > 0 ? CriterionStatus.Pass : CriterionStatus.Fail, string.Create(CultureInfo.InvariantCulture, $"{tr.Mean * 100:+0.000;-0.000} % over {tr.RoundTrips} round trips, t {TStat(tr.ClusteredT)}"))
                : new($"net per trade > 0 at {at}", sweep.Best is null ? CriterionStatus.Fail : CriterionStatus.Wait, "no round trips"),
            candidate is { } c && baseline is { } b
                ? new($"beats open-close at {at}", c.SharpePerDay > b.SharpePerDay ? CriterionStatus.Pass : CriterionStatus.Fail, string.Create(CultureInfo.InvariantCulture, $"Sharpe/yr {c.SharpeAnnualised:0.00} against {b.SharpeAnnualised:0.00}"))
                : new($"beats open-close at {at}", CriterionStatus.Fail, "a run failed"),
        };

        if (holdout is null)
        {
            list.Add(new("holds on the holdout", CriterionStatus.Wait, policy.Locked ? $"locked from {policy.Start:yyyy-MM-dd}: the owner unlocks it for the last check" : "no candidate"));
        }
        else
        {
            bool holds = holdout.CandidateAfter is { } hc && holdout.BaselineAfter is { } hb && hc.TotalReturn > 0 && hc.TotalReturn > hb.TotalReturn;
            list.Add(new("holds on the holdout", holds ? CriterionStatus.Pass : CriterionStatus.Fail,
                holdout.CandidateAfter is { } x && holdout.BaselineAfter is { } y
                    ? string.Create(CultureInfo.InvariantCulture, $"{holdout.Days} days at {at}: {x.TotalReturn * 100:+0.00;-0.00} % against open-close {y.TotalReturn * 100:+0.00;-0.00} %")
                    : "a holdout run failed"));
        }

        return list;
    }

    /// <summary>"2.31", or "n/a" when there is no standard error (a single day, or no spread).</summary>
    public static string TStat(double t) => double.IsFinite(t) ? t.ToString("0.00", CultureInfo.InvariantCulture) : "n/a";

    private static string Verdict(IReadOnlyList<Criterion> criteria)
    {
        if (criteria[0].Status == CriterionStatus.Wait)
        {
            return "NOT YET: too few days to judge; keep collecting.";
        }

        if (criteria.Any(c => c.Status == CriterionStatus.Fail))
        {
            return "NO-GO on the proposed bar.";
        }

        return criteria.Any(c => c.Status == CriterionStatus.Wait)
            ? "PASSES SO FAR: the rest waits (the holdout is the last check, and yours to unlock)."
            : "GO on the proposed bar. The decision is yours (Phase B, paper only).";
    }
}
