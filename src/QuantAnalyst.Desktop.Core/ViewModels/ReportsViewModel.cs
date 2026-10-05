using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Data.Calendar;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Presentation;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Reports;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>One trading day's end-of-day report, rebuilt from the audit log (the same as <c>qa report eod</c>).</summary>
public sealed record ReportRow(DateOnly Date, string State, string Summary, IReadOnlyList<string> Details)
{
    public string DateText => Date.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture);

    public bool IsClean => State == "CLEAN";
}

/// <summary>
/// The week of the selected day (plan 20): the same summary as <c>qa report week</c>, with its headline numbers apart for
/// the card. The marks are words the app's colours know (<see cref="Tone"/>).
/// </summary>
/// <param name="ReturnMark">"up", "down" or "flat".</param>
/// <param name="ThisWeekMark">"ok" within the backtest's range, "FAIL" below it, "info" above it, "none" nothing to compare.</param>
/// <param name="Hold">Paper against holding the list since the start (plan 24), e.g. "0.31 points ahead over 12 day(s), noise so far".</param>
/// <param name="HoldMark">"ok" ahead and "FAIL" behind once the difference is more than noise, "info" before, "none" nothing to compare.</param>
public sealed record WeekCard(
    string Title, string Return, string ReturnMark, string ThisWeek, string ThisWeekMark, string SinceStart, string SinceStartMark, string Days,
    string Hold, string HoldMark, IReadOnlyList<string> Lines)
{
    public static WeekCard From(WeeklyReport week)
    {
        ArgumentNullException.ThrowIfNull(week);
        CultureInfo c = CultureInfo.InvariantCulture;
        (string thisWeek, string thisMark) = Verdict(week.ThisWeek, week.Backtest is not null);
        (string since, string sinceMark) = Verdict(week.SinceStart, week.Backtest is not null);
        (string hold, string holdMark) = HoldVerdict(week.HoldSinceStart);
        string missing = week.NoSessionDays > 0 ? string.Create(c, $", {week.NoSessionDays} without a session") : string.Empty;
        return new WeekCard(
            string.Create(c, $"WEEK {week.Week} · {week.Monday:ddd d MMM} – {week.Monday.AddDays(6):ddd d MMM}").ToUpperInvariant(),
            week.Return is { } r ? r.ToString("+0.00%;-0.00%;0.00%", c) : "no Paper day",
            Tone.Direction(week.Return ?? 0m),
            thisWeek,
            thisMark,
            since,
            sinceMark,
            string.Create(c, $"{week.CleanDays} of {week.TradingDays} trading day(s) clean{missing}"),
            hold,
            holdMark,
            [.. week.Lines().Skip(1)]);
    }

    /// <summary>The chip's words and colour mark for Paper against holding the list (plan 24): coloured once it is more than noise.</summary>
    internal static (string Word, string Mark) HoldVerdict(PaperVsList? v)
    {
        if (v is null)
        {
            return ("from the second Paper day", "none");
        }

        if (v.Difference == 0m)
        {
            return (string.Create(CultureInfo.InvariantCulture, $"level with it over {v.Days} day(s)"), "none");
        }

        bool clear = v.TStat is { } t && Math.Abs(t) >= HoldTheList.TNoise;
        string word = string.Create(CultureInfo.InvariantCulture,
            $"{Math.Abs(v.Difference) * 100:0.00} points {(v.Difference > 0 ? "ahead" : "behind")} over {v.Days} day(s){(clear ? string.Empty : ", noise so far")}");
        return (word, !clear ? "info" : v.Difference > 0 ? "ok" : "FAIL");
    }

    /// <summary>The chip's words and colour mark for a comparison with the backtest.</summary>
    internal static (string Word, string Mark) Verdict(PaperVsBacktest? v, bool hasBacktest) => v switch
    {
        null => (hasBacktest ? "no Paper day yet" : "no backtest to compare", "none"),
        { Verdict: var t } when t.StartsWith("BELOW", StringComparison.Ordinal) => ("below the backtest's range", "FAIL"),
        { Verdict: var t } when t.StartsWith("above", StringComparison.Ordinal) => ("above the backtest's range", "info"),
        { Verdict: var t } when t.StartsWith("within", StringComparison.Ordinal) => ("within the backtest's range", "ok"),
        _ => ("not invested", "none"),
    };
}

/// <summary>
/// Every day's report and the progress towards the Confirm gate (ADR 0003 §3), rebuilt read-only from the audit log:
/// nothing is written here (the session saves its own report; <c>qa report eod</c> rebuilds files). The week card shows
/// the week of the selected day (plan 20).
/// </summary>
public sealed class ReportsViewModel : PageViewModel
{
    private readonly Workspace _workspace;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, WeekCard> _weeks = new(StringComparer.Ordinal);
    private ReportRow? _selected;
    private int _cleanDays;
    private bool _gateMet;
    private WeekCard? _week;
    private string _weekNote = string.Empty;
    private int _weekLoads;
    private bool _refreshing;

    public ReportsViewModel(Workspace workspace, QaEngine engine, TimeProvider time)
        : base(PageKind.Reports, "Reports", "Each day's report, its week against the backtest, and how far Paper is from the Confirm gate.", engine)
    {
        _workspace = workspace;
        _time = time;
    }

    /// <summary>Gets the days, newest first.</summary>
    public ObservableCollection<ReportRow> Days { get; } = [];

    public ObservableCollection<string> GateLines { get; } = [];

    /// <summary>Gets one entry per day the gate needs: true for each clean day already counted (the page's dots).</summary>
    public ObservableCollection<bool> GateDots { get; } = [];

    public ReportRow? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value) && !_refreshing)
            {
                _ = ShowWeekAsync();
            }
        }
    }

    /// <summary>Gets the week of the selected day; null before a day is selected or when it could not be built.</summary>
    public WeekCard? Week
    {
        get => _week;
        private set => Set(ref _week, value);
    }

    /// <summary>Gets why the week could not be built, or while it loads; empty otherwise.</summary>
    public string WeekNote
    {
        get => _weekNote;
        private set => Set(ref _weekNote, value);
    }

    /// <summary>Gets the clean Paper days in a row that count towards the gate.</summary>
    public int CleanDays
    {
        get => _cleanDays;
        private set
        {
            if (Set(ref _cleanDays, value))
            {
                OnPropertyChanged(nameof(GateProgress));
            }
        }
    }

    public int NeededDays { get; } = PromotionGate.MinPaperDays;

    /// <summary>Gets "3 of 10 clean Paper days".</summary>
    public string GateProgress => string.Create(CultureInfo.InvariantCulture, $"{CleanDays} of {NeededDays} clean Paper days");

    public bool GateMet
    {
        get => _gateMet;
        private set => Set(ref _gateMet, value);
    }

    public override async Task RefreshAsync()
    {
        try
        {
            (IReadOnlyList<ReportRow> rows, GateResult? gate) = await Task.Run(Load);
            Days.Clear();
            foreach (ReportRow row in rows)
            {
                Days.Add(row);
            }

            _weeks.Clear();
            _refreshing = true;
            try
            {
                Selected = Days.FirstOrDefault();
            }
            finally
            {
                _refreshing = false;
            }

            await ShowWeekAsync();
            GateLines.Clear();
            foreach (string line in gate?.Lines ?? [])
            {
                GateLines.Add(line);
            }

            CleanDays = gate?.Evidence.Count ?? 0;
            GateMet = gate?.Met ?? false;
            GateDots.Clear();
            for (int i = 0; i < NeededDays; i++)
            {
                GateDots.Add(i < CleanDays);
            }

            Say(rows.Count == 0
                ? "No session has run yet. Reports appear here after the first Paper session."
                : GateMet ? "The Confirm gate is met. Promoting is your decision, made in the terminal (docs/guide.md §6)." : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Say($"The reports could not be read: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Shows the week of the selected day, built as <c>qa report week</c> builds it (never saved from here). The price
    /// store is read only while no command runs; a week built without it is not kept, so the next look reads it.
    /// </summary>
    internal async Task ShowWeekAsync()
    {
        ReportRow? row = _selected;
        if (row is null)
        {
            Week = null;
            WeekNote = string.Empty;
            return;
        }

        string name = WeeklyReport.WeekName(row.Date);
        if (_weeks.TryGetValue(name, out WeekCard? known))
        {
            Week = known;
            WeekNote = string.Empty;
            return;
        }

        int load = ++_weekLoads;
        bool storeFree = !IsBusy;
        try
        {
            WeeklyReport week = await Task.Run(() => TradingCommands.BuildWeek(_workspace.WeekPaths, row.Date, _time.GetUtcNow(), storeFree));
            var card = WeekCard.From(week);
            if (storeFree && week.IntradayUnavailable is null)
            {
                _weeks[name] = card;
            }

            if (load == _weekLoads)
            {
                Week = card;
                WeekNote = string.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException
                                       or CalendarConfigException or TradingConfigException or BacktestConfigException)
        {
            if (load == _weekLoads)
            {
                Week = null;
                WeekNote = $"The week could not be built: {ex.Message}";
            }
        }
    }

    private (IReadOnlyList<ReportRow> Rows, GateResult? Gate) Load()
    {
        if (!Directory.Exists(_workspace.AuditDir))
        {
            return ([], null);
        }

        DateOnly[] days = [.. Directory.EnumerateFiles(_workspace.AuditDir, "*.jsonl")
            .Select(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) ? d : (DateOnly?)null)
            .OfType<DateOnly>()
            .Order()];
        List<EodReport> reports = [.. days.Select(d => EodReport.Build(_workspace.AuditDir, d, _time))];
        GateResult gate = PromotionGate.Confirm(reports, AuditLog.Verify(_workspace.AuditDir));
        IReadOnlyList<ReportRow> rows = [.. reports.OrderByDescending(r => r.Date).Select(ToRow)];
        return (rows, gate);
    }

    private static ReportRow ToRow(EodReport r)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        var details = new List<string>();
        foreach (EodFill f in r.Fills)
        {
            details.Add(string.Create(c,
                $"fill {f.Side} {f.Volume} {f.Ticker} @ {f.Price} (limit {f.Limit}, {f.How}); {(f.DeviationBps is { } d ? $"{d:+0.0;-0.0} bps from the {f.ReferenceKind}" : "no reference")}"));
        }

        details.AddRange((r.FillRate?.Orders ?? []).Select(o => "limit " + o.Describe())); // plan 19
        details.AddRange(r.CorporateActions.Select(a => $"{a.Kind} {a.Text}")); // plan 21
        details.AddRange(r.ManualOrders.Select(m => "manual " + m)); // plan 23
        details.AddRange(r.Violations.Select(v => "VIOLATION: " + v));
        details.AddRange(r.Events.Select(e => "event: " + e));
        string state = r.Clean ? "CLEAN" : !r.Complete ? "INCOMPLETE" : "NOT CLEAN";
        return new ReportRow(r.Date, state, r.Summary(), details);
    }
}
