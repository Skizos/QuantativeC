using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Cli.Output;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;

namespace QuantAnalyst.Cli.Commands;

/// <summary><c>qa intraday report</c>: the go/no-go report of plan 17 step A6. Offline; its runs are logged like any other.</summary>
internal static partial class IntradayBacktestCommands
{
    public static Command Report()
    {
        var o = new Inputs();
        var command = new Command(
            "report",
            "The go/no-go report (plan 17 step A6): the ORB grid (range 5, 15, 30) and the two controls on the days before the intraday holdout, "
            + "the candidate's Deflated Sharpe, PBO, walk-forward, per-trade net at Mini costs, and the proposed bar. Every run is logged. Offline; the decision is yours.");
        o.AddTo(command, report: true);
        command.SetAction(parse => BacktestCommands.Execute(parse, w =>
        {
            string configDir = BacktestCommands.ResolveConfigDir(parse.GetValue(o.ConfigDir));
            ChartResolution resolution = ParseResolution(parse.GetValue(o.Resolution));
            MarketCalendar calendar = LoadCalendar(configDir, out string? calendarNote);
            var clock = new IntradayClock(calendar, IntradayImporter.Length(resolution));
            StrategyDefinition first = IntradayStrategyCatalog.Create("orb-long", new Dictionary<string, string>(), clock);
            BacktestCommands.Setup setup = Prepare(parse, o, configDir, resolution, first, calendar, calendarNote, report: true, out MarketPanel? holdoutData);
            IntradayReportResult r = IntradayReport.Run(new IntradayReportRequest { Template = setup.Template, Clock = clock, HoldoutData = holdoutData });
            if (parse.GetValue(o.Json))
            {
                w.WriteLine(JsonSerializer.Serialize(Json(r, setup), QaCli.Json));
            }
            else
            {
                Write(w, r, setup);
            }

            return 0;
        }));
        return command;
    }

    private static void Write(TextWriter w, IntradayReportResult r, BacktestCommands.Setup setup)
    {
        BacktestRequest t = setup.Template;
        MarketPanel d = t.Data;
        string at = r.After.DisplayName ?? r.After.Name;
        w.WriteLine("Intraday go/no-go report (plan 17 step A6, ADR 0006). Model output, not advice; the decision is yours.");
        w.WriteLine($"Data: {d.Source.Label}; {d.InstrumentCount} share(s), {r.Days} trading day(s) {d.Dates[0]:yyyy-MM-dd}..{d.Dates[^1]:yyyy-MM-dd}.");
        foreach (string note in setup.Notes)
        {
            w.WriteLine(note);
        }

        w.WriteLine($"Costs: {t.Costs.Label}; \"at {at}\" is the same trades at {at}'s courtage. Cash {t.InitialCash.ToString("N0", CultureInfo.InvariantCulture)} SEK.");
        w.WriteLine();

        var runs = new TextTable(("trial", false), ("strategy", false), ("status", false), ("SR/yr", true), ("return", true), ("max DD", true), ("fills/day", true));
        foreach (BacktestResult b in r.Grid.Trials.Append(r.LateMomentum).Append(r.OpenClose))
        {
            TrialMetrics? m = b.Record.Metrics;
            runs.Add(b.Record.Id, IntradayReport.Describe(b), b.Ok ? "ok" : $"{b.Record.Status}: {b.Record.Note}", F(m?.SharpeAnnualised, "0.00"), Pct(m?.TotalReturn),
                Pct(m?.MaxDrawdown), b.Ok ? ((double)b.Fills.Count / r.Days).ToString("0.0", CultureInfo.InvariantCulture) : "-");
        }

        runs.Write(w);
        w.WriteLine($"All {r.Grid.Trials.Count + 2} runs are in the TrialLedger ({t.Ledger!.Path}); each report adds its runs again, and the Deflated Sharpe counts them.");
        w.WriteLine();

        if (r.Candidate is { } c)
        {
            w.WriteLine($"Candidate: {IntradayReport.Describe(c)} (trial {c.Record.Id}), the grid's best daily Sharpe in sample; the Deflated Sharpe counts that choice.");
            if (c.Record.Note is { } note)
            {
                w.WriteLine($"  {note}");
            }

            if (r.CandidateAfter is { } ca && r.OpenCloseAfter is { } oa)
            {
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  At {at}: Sharpe/yr {ca.SharpeAnnualised:0.00}, return {ca.TotalReturn * 100:0.00} %, courtage {ca.Courtage:N0} SEK; open-close at {at}: Sharpe/yr {oa.SharpeAnnualised:0.00}, return {oa.TotalReturn * 100:0.00} %."));
            }

            if (r.Trades is { } tr)
            {
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  Per trade at {at}: {tr.RoundTrips} round trips on {tr.Days} days, mean {tr.Mean * 100:+0.000;-0.000} %, t {IntradayReport.TStat(tr.ClusteredT)} (clustered by day), {tr.WinRate:P0} won."));
            }

            if (r.FillsPerDay is { } perDay && t.Costs.FreeTrades is { } free && perDay > 0)
            {
                double lasts = free.Trades / perDay;
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  Free trades: {perDay:0.0} trades a day use {free.Trades} in about {lasts:0} trading days (~{lasts / 21:0.#} months); then {at} for the rest of the {free.Months} months{(free.VerifiedOn is null ? " (allowance UNVERIFIED)" : string.Empty)}."));
            }

            w.WriteLine(r.WalkForward is { } wf
                ? string.Create(CultureInfo.InvariantCulture,
                    $"  Walk-forward (choose the range on all days before, at least {wf.TrainDays}, then trade the next {wf.TestDays}): Sharpe/yr {wf.SharpeAnnualised:0.00} over {wf.OutOfSampleDays} days out of sample; chose {string.Join(", ", wf.Picks.Select(p => $"{p.Key} {p.Value}x"))}.")
                : $"  Walk-forward: n/a (needs {IntradayReport.WalkForwardTrain + IntradayReport.WalkForwardTest}+ days).");
        }
        else
        {
            w.WriteLine("Candidate: none; every ORB run failed (see the table).");
        }

        if (r.Holdout is { } h)
        {
            w.WriteLine($"  Holdout ({h.Days} days): trials {h.Candidate.Record.Id} and {h.Baseline.Record.Id}, marked in the ledger as touching it.");
        }

        w.WriteLine();
        w.WriteLine("The proposed bar (plan 17):");
        var bar = new TextTable(("", false), ("criterion", false), ("value", false));
        foreach (Criterion k in r.Criteria)
        {
            bar.Add(k.Status switch { CriterionStatus.Pass => "PASS", CriterionStatus.Fail => "FAIL", _ => "WAIT" }, k.Name, k.Detail);
        }

        bar.Write(w);
        w.WriteLine($"Verdict: {r.Verdict}");
    }

    /// <summary>The report as JSON; a value that is not a finite number (a Sharpe ratio without trades) is null.</summary>
    private static object Json(IntradayReportResult r, BacktestCommands.Setup setup) => new
    {
        days = r.Days,
        trials = r.Grid.Trials.Append(r.LateMomentum).Append(r.OpenClose).Select(b => new
        {
            id = b.Record.Id,
            strategy = b.Record.Strategy,
            parameters = b.Record.Parameters,
            status = b.Record.Status.ToString(),
            note = b.Record.Note,
            sharpeAnnualised = Finite(b.Record.Metrics?.SharpeAnnualised),
            totalReturn = Finite(b.Record.Metrics?.TotalReturn),
            fills = b.Fills.Count,
            holdoutTouched = b.Record.HoldoutTouched,
        }),
        candidate = r.Candidate?.Record.Id,
        dsr = r.Grid.BestDsr,
        studyTrials = r.Grid.StudyTrials,
        pbo = r.Grid.Pbo?.Probability,
        after = r.After.Name,
        candidateAfter = View(r.CandidateAfter),
        openCloseAfter = View(r.OpenCloseAfter),
        trades = r.Trades is { } t
            ? new { roundTrips = t.RoundTrips, days = t.Days, mean = Finite(t.Mean), clusteredT = Finite(t.ClusteredT), winRate = Finite(t.WinRate) }
            : null,
        walkForward = r.WalkForward is { } wf
            ? new { trainDays = wf.TrainDays, testDays = wf.TestDays, outOfSampleDays = wf.OutOfSampleDays, sharpeAnnualised = Finite(wf.SharpeAnnualised), meanDaily = Finite(wf.MeanDaily), picks = wf.Picks }
            : null,
        fillsPerDay = r.FillsPerDay,
        holdout = r.Holdout is { } h
            ? new { days = h.Days, candidate = h.Candidate.Record.Id, baseline = h.Baseline.Record.Id, candidateAfter = View(h.CandidateAfter), baselineAfter = View(h.BaselineAfter) }
            : null,
        criteria = r.Criteria.Select(c => new { name = c.Name, status = c.Status.ToString(), detail = c.Detail }),
        verdict = r.Verdict,
        notes = setup.Notes,
    };

    private static object? View(CostView? v) => v is null
        ? null
        : new { strategy = v.Strategy, costs = v.Costs, sharpeAnnualised = Finite(v.SharpeAnnualised), totalReturn = Finite(v.TotalReturn), courtage = Finite(v.Courtage) };

    private static double? Finite(double? v) => v is { } x && double.IsFinite(x) ? x : null;

    private static string F(double? v, string format) => v is { } x && double.IsFinite(x) ? x.ToString(format, CultureInfo.InvariantCulture) : "-";

    private static string Pct(double? v) => v is { } x && double.IsFinite(x) ? (x * 100).ToString("0.00", CultureInfo.InvariantCulture) + " %" : "-";
}
