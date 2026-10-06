using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Analytics.Statistics;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Trading.Modes;

namespace QuantAnalyst.Trading.Reports;

/// <summary>One day of the weekly summary (plan 20).</summary>
/// <param name="State">CLEAN, NOT CLEAN, INCOMPLETE, or "no session".</param>
/// <param name="Return">The day's change of the account value, as a fraction; null without end-of-day values.</param>
/// <param name="Invested">The share of the account in shares at the close; null without end-of-day values.</param>
/// <param name="Fills">Paper fills, and live orders filled on a Confirm day.</param>
public sealed record WeekDay(DateOnly Date, string State, IReadOnlyList<string> Modes, decimal? Return, decimal? Invested, decimal? EndValue, decimal Fees, int Sent, int Fills)
{
    public bool IsPaper => Modes.Contains("Paper") && !Modes.Any(m => m is "Confirm" or "Auto");
}

/// <summary>Paper's return over some days against what the backtest expects over as many days (plan 20).</summary>
/// <param name="Invested">Paper's average share invested at the close over those days; the expectation is scaled by it.</param>
/// <param name="Verdict">"below the range", "within the range", "above the range", or "not invested".</param>
public sealed record PaperVsBacktest(int Days, decimal PaperReturn, decimal Invested, double Expected, double Low, double High, string Verdict)
{
    public string Describe()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        return string.Create(c,
            $"{Days} day(s) at {Invested:P0} invested: Paper {PaperReturn:+0.00%;-0.00%;0.00%}; the backtest expects {Expected:+0.00%;-0.00%;0.00%} (95 % range {Low:+0.00%;-0.00%;0.00%} to {High:+0.00%;-0.00%;0.00%}): {Verdict}");
    }
}

/// <summary>
/// What the saved strategy's recorded backtest expects of a day (plan 20). It comes from the trial ledger; no backtest is
/// run for it, since a new run would be an evaluation for the ledger, and one over the last weeks could read the locked
/// holdout.
/// </summary>
/// <param name="MeanDaily">The backtest's average daily return: Sharpe per day × daily volatility.</param>
/// <param name="StdDaily">Its daily volatility: annual volatility / √252 (the runner's convention).</param>
/// <param name="SameList">True when the backtest ran on the current allowlist.</param>
public sealed record BacktestExpectation(
    string TrialId, string Strategy, IReadOnlyList<string> Universe, DateOnly From, DateOnly To, string CostModel, int Observations, double MeanDaily, double StdDaily, bool SameList)
{
    /// <summary>The two-sided 95 % normal quantile.</summary>
    public const double Z95 = 1.96;

    /// <summary>Below this share invested there is nothing to compare.</summary>
    public const decimal MinInvested = 0.01m;

    /// <summary>
    /// The latest successful backtest of <paramref name="strategy"/> (same parameters) on imported data, preferring one on
    /// <paramref name="allowlist"/>; null when there is none.
    /// </summary>
    public static BacktestExpectation? Find(IEnumerable<TrialRecord> trials, StrategySpec strategy, IReadOnlyCollection<string> allowlist)
    {
        ArgumentNullException.ThrowIfNull(trials);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(allowlist);
        TrialRecord[] matching = [.. trials
            .Where(t => t.Strategy == strategy.Name && t.Status == TrialStatus.Ok && t.DataSource != SyntheticMarket.SourceName
                        && t.Metrics is { Observations: >= 2 } m && double.IsFinite(m.SharpePerPeriod) && double.IsFinite(m.VolatilityAnnualised)
                        && t.Parameters.Count == strategy.Parameters.Count
                        && strategy.Parameters.All(p => t.Parameters.TryGetValue(p.Key, out string? v) && v == p.Value))
            .OrderBy(t => t.Sequence)];
        if (matching.Length == 0)
        {
            return null;
        }

        HashSet<string> list = [.. allowlist.Select(Normalize)];
        TrialRecord? same = matching.LastOrDefault(t => t.Universe.Count == list.Count && t.Universe.All(u => list.Contains(Normalize(u))));
        return Of(same ?? matching[^1], same is not null);
    }

    public static BacktestExpectation Of(TrialRecord trial, bool sameList)
    {
        ArgumentNullException.ThrowIfNull(trial);
        TrialMetrics m = trial.Metrics ?? throw new ArgumentException($"Trial {trial.Id} has no metrics.", nameof(trial));
        double sd = m.VolatilityAnnualised / Math.Sqrt(PerformanceStatistics.TradingDaysPerYear);
        return new BacktestExpectation(trial.Id, new StrategySpec(trial.Strategy, trial.Parameters).Describe(), trial.Universe, trial.From, trial.To,
            trial.CostModel, m.Observations, m.SharpePerPeriod * sd, sd, sameList);
    }

    /// <summary>
    /// Paper's <paramref name="paperReturn"/> over <paramref name="days"/> days at an average <paramref name="invested"/>
    /// share, against s·n·μ ± 1.96·s·σ·√n.
    /// </summary>
    public PaperVsBacktest Compare(int days, decimal paperReturn, decimal invested)
    {
        double s = (double)invested;
        double expected = s * days * MeanDaily;
        double half = Z95 * s * StdDaily * Math.Sqrt(days);
        double paper = (double)paperReturn;
        string verdict = invested < MinInvested ? "not invested: nothing to compare"
            : paper < expected - half ? "BELOW the range: Paper does worse than the backtest; look at the fills and what the limits missed"
            : paper > expected + half ? "above the range"
            : "within the range";
        return new PaperVsBacktest(days, paperReturn, invested, expected, expected - half, expected + half, verdict);
    }

    public string Describe()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        return string.Create(c,
            $"{TrialId}, {Strategy} on {string.Join(", ", Universe)}, {From:yyyy-MM-dd}..{To:yyyy-MM-dd}, costs {CostModel} (its average day {MeanDaily:+0.000%;-0.000%;0.000%}, sd {StdDaily:0.00%})");
    }

    private static string Normalize(string ticker) => ticker.Trim().Replace('-', ' ').Replace('_', ' ').ToUpperInvariant();
}

/// <summary>
/// The weekly summary (plan 20): the week's Paper days, Paper's return against the saved strategy's recorded backtest
/// and against holding the list (plan 24; this week and since the first Paper day), the limit fill rate (plan 19), and
/// the intraday collection (plan 17). Built from the end-of-day reports (rebuilt from the audit), the trial ledger and
/// the store; nothing is asked of Avanza.
/// </summary>
public sealed record WeeklyReport
{
    public const string Format = "qa-week-report/1";

    public string ReportFormat { get; init; } = Format;

    /// <summary>Gets the ISO week, e.g. "2026-W40".</summary>
    public required string Week { get; init; }

    public required DateOnly Monday { get; init; }

    public required IReadOnlyList<WeekDay> Days { get; init; }

    public required int TradingDays { get; init; }

    public required int CleanDays { get; init; }

    public required int NoSessionDays { get; init; }

    /// <summary>Gets the week's Paper return, compounded over its Paper days with end-of-day values.</summary>
    public decimal? Return { get; init; }

    public decimal? EndValue { get; init; }

    public required decimal Fees { get; init; }

    /// <summary>Gets the Confirm gate's count, e.g. "4 of 10 clean Paper days in a row".</summary>
    public required string Gate { get; init; }

    public BacktestExpectation? Backtest { get; init; }

    public PaperVsBacktest? ThisWeek { get; init; }

    public DateOnly? FirstPaperDay { get; init; }

    public PaperVsBacktest? SinceStart { get; init; }

    public EodFillRate? FillRate { get; init; }

    public EodFillRate? FillRateSinceStart { get; init; }

    public IntradayCoverage? Intraday { get; init; }

    /// <summary>Gets how many collected days the intraday go/no-go needs (plan 17: 120 before the holdout's days).</summary>
    public int? IntradayNeeded { get; init; }

    /// <summary>Gets why the intraday collection could not be read this time (e.g. the store is in use); null when it was.</summary>
    public string? IntradayUnavailable { get; init; }

    /// <summary>Gets how many manual orders the gateway accepted this week (plan 23): Paper is then not the strategy alone.</summary>
    public int ManualOrdersSent { get; init; }

    /// <summary>Gets Paper against holding the list this week (plan 24); null without a Paper day that has a Paper close before it.</summary>
    public PaperVsList? HoldThisWeek { get; init; }

    /// <summary>Gets the first day of <see cref="HoldSinceStart"/>.</summary>
    public DateOnly? HoldFirstDay { get; init; }

    /// <summary>Gets Paper against holding the list since the first comparable Paper day (plan 24).</summary>
    public PaperVsList? HoldSinceStart { get; init; }

    /// <summary>Gets why the list's dividends are left out (e.g. the store was busy); null when they are counted.</summary>
    public string? HoldDividendsMissing { get; init; }

    /// <summary>Gets the listed shares left out of this week's days, e.g. "Tue ERIC B (split-like move)".</summary>
    public IReadOnlyList<string> HoldLeftOut { get; init; } = [];

    /// <summary>Gets the market index compared with (plan 24 B), e.g. "OMX Stockholm 30"; null when none is set.</summary>
    public string? BenchmarkName { get; init; }

    /// <summary>Gets the index over this week's Paper days.</summary>
    public BenchmarkReturn? BenchmarkThisWeek { get; init; }

    /// <summary>Gets the index over the Paper days since the first comparable one.</summary>
    public BenchmarkReturn? BenchmarkSinceStart { get; init; }

    /// <summary>Gets why the index is not shown although one is set (no closes stored yet, the store busy).</summary>
    public string? BenchmarkMissing { get; init; }

    public required DateTimeOffset GeneratedUtc { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The ISO week of <paramref name="day"/>, e.g. "2026-W40".</summary>
    public static string WeekName(DateOnly day)
    {
        DateTime d = day.ToDateTime(TimeOnly.MinValue);
        return string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d):00}");
    }

    /// <summary>The Monday of <paramref name="day"/>'s ISO week.</summary>
    public static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>Parses "2026-W40" (or "2026-w40") to its Monday.</summary>
    public static DateOnly ParseWeek(string week)
    {
        ArgumentNullException.ThrowIfNull(week);
        string[] parts = week.Trim().ToUpperInvariant().Split("-W");
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int year)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number is >= 1 and <= 53
               && year is >= 2000 and <= 2100 && number <= ISOWeek.GetWeeksInYear(year)
            ? DateOnly.FromDateTime(ISOWeek.ToDateTime(year, number, DayOfWeek.Monday))
            : throw new ArgumentException($"--week: '{week}' is not an ISO week such as 2026-W40.");
    }

    /// <param name="day">Any day of the week to summarize.</param>
    /// <param name="tradingDays">The Stockholm trading days of that week.</param>
    /// <param name="reports">Every day's end-of-day report (for the weeks before, too: "since the start").</param>
    /// <param name="dividends">The listed shares' dividends by orderbook id, for holding the list (plan 24); null leaves them out.</param>
    /// <param name="benchmark">The market index's name and its closes by date (plan 24 B); null when none is set.</param>
    public static WeeklyReport Build(
        DateOnly day, IReadOnlyList<DateOnly> tradingDays, IReadOnlyList<EodReport> reports, GateResult gate, BacktestExpectation? backtest,
        IntradayCoverage? intraday, int? intradayNeeded, DateTimeOffset now, IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? dividends = null,
        (string Name, IReadOnlyDictionary<DateOnly, decimal> Closes)? benchmark = null)
    {
        ArgumentNullException.ThrowIfNull(tradingDays);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(gate);
        DateOnly monday = MondayOf(day);
        DateOnly sunday = monday.AddDays(6);
        Dictionary<DateOnly, EodReport> byDay = reports.Where(r => r.Date >= monday && r.Date <= sunday).ToDictionary(r => r.Date);
        WeekDay[] days = [.. tradingDays.Where(d => d >= monday && d <= sunday).Union(byDay.Keys).Order()
            .Select(d => byDay.TryGetValue(d, out EodReport? r) ? Row(r) : new WeekDay(d, "no session", [], null, null, null, 0m, 0, 0))];

        WeekDay[] paperWeek = [.. days.Where(d => d.IsPaper && d.Return is not null)];
        WeekDay[] paperSince = [.. reports.Where(r => r.Date <= sunday).OrderBy(r => r.Date).Select(Row).Where(d => d.IsPaper && d.Return is not null)];
        EodReport[] week = [.. byDay.Values.OrderBy(r => r.Date)];
        IReadOnlyList<ListDay> held = HoldTheList.Days(reports.Where(r => r.Date <= sunday), dividends);
        ListDay[] heldThisWeek = [.. held.Where(d => d.Date >= monday)];
        return new WeeklyReport
        {
            Week = WeekName(monday),
            Monday = monday,
            Days = days,
            TradingDays = days.Count(d => tradingDays.Contains(d.Date)),
            CleanDays = days.Count(d => d.State == "CLEAN"),
            NoSessionDays = days.Count(d => d.State == "no session"),
            Return = paperWeek.Length == 0 ? null : Compound(paperWeek),
            EndValue = days.LastOrDefault(d => d.EndValue is not null)?.EndValue,
            Fees = days.Sum(d => d.Fees),
            Gate = string.Create(CultureInfo.InvariantCulture, $"{gate.Evidence.Count} of {PromotionGate.MinPaperDays} clean Paper days in a row{(gate.Met ? " (MET)" : string.Empty)}"),
            Backtest = backtest,
            ThisWeek = backtest is null || paperWeek.Length == 0 ? null : backtest.Compare(paperWeek.Length, Compound(paperWeek), AverageInvested(paperWeek)),
            FirstPaperDay = paperSince.Length == 0 ? null : paperSince[0].Date,
            SinceStart = backtest is null || paperSince.Length == 0 ? null : backtest.Compare(paperSince.Length, Compound(paperSince), AverageInvested(paperSince)),
            FillRate = EodFillRate.Combine(week),
            FillRateSinceStart = EodFillRate.Combine(reports.Where(r => r.Date <= sunday)),
            Intraday = intraday,
            IntradayNeeded = intradayNeeded,
            ManualOrdersSent = week.Sum(r => r.ManualOrdersSent),
            HoldThisWeek = HoldTheList.Compare(heldThisWeek),
            HoldFirstDay = held.Count == 0 ? null : held[0].Date,
            HoldSinceStart = HoldTheList.Compare(held),
            HoldDividendsMissing = dividends is null ? "not read" : null,
            HoldLeftOut = [.. heldThisWeek.SelectMany(d => d.LeftOut.Select(x => d.Date.ToString("ddd", CultureInfo.InvariantCulture) + " " + x))],
            BenchmarkName = benchmark?.Name,
            BenchmarkThisWeek = benchmark is { } b1 ? MarketBenchmark.Over(heldThisWeek, b1.Closes) : null,
            BenchmarkSinceStart = benchmark is { } b2 ? MarketBenchmark.Over(held, b2.Closes) : null,
            GeneratedUtc = now,
        };
    }

    /// <summary>The summary as text, one line each.</summary>
    public IReadOnlyList<string> Lines()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        var lines = new List<string> { string.Create(c, $"Week {Week}, {Monday:ddd yyyy-MM-dd} to {Monday.AddDays(6):ddd yyyy-MM-dd}") };
        foreach (WeekDay d in Days)
        {
            string modes = d.Modes.Count == 0 || d.IsPaper ? string.Empty : $" ({string.Join(", ", d.Modes)})";
            string values = d.Return is { } r
                ? string.Create(c, $"  {r,8:+0.00%;-0.00%;0.00%}  invested {d.Invested ?? 0m,4:P0}  {d.Sent} sent, {d.Fills} fill(s)")
                : d.State == "no session" ? string.Empty : string.Create(c, $"  no end-of-day values  {d.Sent} sent, {d.Fills} fill(s)");
            lines.Add(string.Create(c, $"  {d.Date:ddd yyyy-MM-dd}  {d.State + modes,-11}{values}").TrimEnd());
        }

        string ret = Return is { } w ? string.Create(c, $"{w:+0.00%;-0.00%;0.00%}{(EndValue is { } v ? $" (value {v:N2} SEK)" : string.Empty)}") : "no Paper day with values";
        string missing = NoSessionDays > 0 ? $", {NoSessionDays} without a session" : string.Empty;
        lines.Add(string.Create(c, $"Week: {ret}, fees {Fees:N2} SEK; {CleanDays} of {TradingDays} trading day(s) clean{missing}; Confirm gate: {Gate}."));

        if (ManualOrdersSent > 0)
        {
            lines.Add(string.Create(c,
                $"Manual: {ManualOrdersSent} order(s) by hand this week: the returns are not the strategy's alone, so the comparison with the backtest says less."));
        }

        if (Backtest is { } b)
        {
            lines.Add($"Against the backtest {b.Describe()}:");
            lines.Add("  this week: " + (ThisWeek?.Describe() ?? "no Paper day with values"));
            if (SinceStart is { } s && FirstPaperDay is { } first)
            {
                lines.Add(string.Create(c, $"  since {first:yyyy-MM-dd}: {s.Describe()}"));
            }

            if (!b.SameList)
            {
                lines.Add("  (that backtest ran on a different list than today's allowlist: back-test the current list for a fair comparison)");
            }
        }
        else
        {
            lines.Add("Against the backtest: no successful backtest of the saved strategy on imported data in the trial ledger (qa backtest run first).");
        }

        if (HoldSinceStart is { } hold && HoldFirstDay is { } holdFirst)
        {
            string dividendsNote = HoldDividendsMissing is { } why ? $"dividends left out ({why})" : "dividends included";
            lines.Add($"Against holding the list (equal weights, {dividendsNote}, no costs; the same close prices as Paper):");
            lines.Add("  this week: " + (HoldThisWeek?.Describe() ?? "no Paper day with a Paper close before it"));
            lines.Add(string.Create(c, $"  since {holdFirst:yyyy-MM-dd}: {hold.Describe()}"));
            if (HoldLeftOut.Count > 0)
            {
                lines.Add("  left out this week: " + string.Join("; ", HoldLeftOut));
            }

            if (BenchmarkName is { } index)
            {
                lines.Add(BenchmarkSinceStart is { } market
                    ? string.Create(c, $"  the market ({index}) over the same days: this week {Describe(BenchmarkThisWeek)}, since {holdFirst:yyyy-MM-dd} {Describe(market)}")
                    : $"  the market ({index}): {BenchmarkMissing ?? "no closes stored for these days yet ('qa benchmark import')"}");
            }
        }
        else
        {
            lines.Add("Against holding the list: from the second Paper day with close prices (each day runs from the Paper close before it).");
        }

        lines.Add("Limits this week: " + (FillRate?.Describe() ?? "no orders") + (FillRateSinceStart is { } all ? $". Since the start: {all.Describe()}." : "."));
        if (Intraday is { } i)
        {
            string shares = i.Shares.Count == 0
                ? "no research shares"
                : string.Join(", ", i.Shares.Select(s => string.Create(c,
                    $"{s.Ticker} {s.Fine + s.Coarse}{(s.Coarse > 0 ? $" ({s.Coarse} at 10 minutes)" : string.Empty)}{(s.Missing.Count > 0 ? $" (missing {string.Join(", ", s.Missing.Select(m => m.ToString("MM-dd", c)))})" : string.Empty)}")));
            string need = IntradayNeeded is { } n ? string.Create(c, $" of the {n} the go/no-go needs") : string.Empty;
            lines.Add(string.Create(c, $"Intraday bars, {i.TradingDays.Count} trading day(s): {shares}; {i.CollectedDays} day(s) collected so far{need}."));
        }
        else if (IntradayUnavailable is { } why)
        {
            lines.Add($"Intraday bars: {why}.");
        }

        return lines;
    }

    public string Save(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Week + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + "\n");
        return path;
    }

    public static WeeklyReport Load(string path) =>
        JsonSerializer.Deserialize<WeeklyReport>(File.ReadAllText(path), Json) ?? throw new JsonException($"{path} is empty.");

    private static string Describe(BenchmarkReturn? r) => r is null
        ? "no closes"
        : string.Create(CultureInfo.InvariantCulture, $"{r.Return:+0.00%;-0.00%;0.00%} ({r.Days} day(s){(r.Missing > 0 ? $", {r.Missing} without closes" : string.Empty)})");

    private static WeekDay Row(EodReport r)
    {
        string state = r.Clean ? "CLEAN" : !r.Complete ? "INCOMPLETE" : "NOT CLEAN";
        decimal? invested = r.Account is { EndValue: > 0 } a ? decimal.Round((a.EndValue - a.Cash) / a.EndValue, 4) : null;
        return new WeekDay(r.Date, state, r.Modes, r.Account?.PnlPct, invested, r.Account?.EndValue, r.Account?.FeesPaid ?? 0m, r.Submitted,
            r.Fills.Count + (r.Live?.Filled ?? 0));
    }

    private static decimal Compound(IEnumerable<WeekDay> days) =>
        decimal.Round(days.Aggregate(1m, (acc, d) => acc * (1 + d.Return!.Value)) - 1, 6);

    private static decimal AverageInvested(IReadOnlyCollection<WeekDay> days) =>
        decimal.Round(days.Average(d => d.Invested ?? 0m), 4);
}
