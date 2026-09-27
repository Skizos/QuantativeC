using System.Collections.ObjectModel;
using System.Globalization;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Reports;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>One trading day's end-of-day report, rebuilt from the audit log (the same as <c>qa report eod</c>).</summary>
public sealed record ReportRow(DateOnly Date, string State, string Summary, IReadOnlyList<string> Details)
{
    public string DateText => Date.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture);

    public bool IsClean => State == "CLEAN";
}

/// <summary>
/// Every day's report and the progress towards the Confirm gate (ADR 0003 §3), rebuilt read-only from the audit log:
/// nothing is written here (the session saves its own report; <c>qa report eod</c> rebuilds files).
/// </summary>
public sealed class ReportsViewModel : PageViewModel
{
    private readonly Workspace _workspace;
    private readonly TimeProvider _time;
    private ReportRow? _selected;
    private int _cleanDays;
    private bool _gateMet;

    public ReportsViewModel(Workspace workspace, QaEngine engine, TimeProvider time)
        : base(PageKind.Reports, "Reports", "Each day's report, and how far Paper is from the Confirm gate.", engine)
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
        set => Set(ref _selected, value);
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

            Selected = Days.FirstOrDefault();
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

        details.AddRange(r.Violations.Select(v => "VIOLATION: " + v));
        details.AddRange(r.Events.Select(e => "event: " + e));
        string state = r.Clean ? "CLEAN" : !r.Complete ? "INCOMPLETE" : "NOT CLEAN";
        return new ReportRow(r.Date, state, r.Summary(), details);
    }
}
