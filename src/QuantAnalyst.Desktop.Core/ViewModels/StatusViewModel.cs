using System.Collections.ObjectModel;
using System.Globalization;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Calendar;
using QuantAnalyst.Desktop.Core.Charts;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Mvvm;
using QuantAnalyst.Desktop.Core.Presentation;
using QuantAnalyst.Trading.Accounts;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Reports;
using QuantAnalyst.Trading.Risk;
using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>One status line: its mark (ok, todo, warn, FAIL), what it is about, and what was found.</summary>
public sealed record StatusLine(string Mark, string Label, string Text)
{
    public bool IsOk => Mark == "ok";

    public bool IsTodo => Mark == "todo";

    public bool IsWarning => Mark == "warn";

    public bool IsFailure => Mark == "FAIL";
}

/// <summary>
/// The start page, the Overview (docs/plans/11-app-redesign.md): the next session with a countdown, the Confirm gate as
/// dots, the live-trading account and the kill switch; the paper account's value day by day; <c>qa status</c> as a
/// checklist (the same <c>StatusCommand.Build</c>, read-only), the numbered next steps, and one button for the most
/// useful next thing.
/// </summary>
public sealed class StatusViewModel : PageViewModel
{
    private readonly Workspace _workspace;
    private readonly TimeProvider _time;
    private readonly Action<PageKind> _navigate;
    private readonly IUserEnvironment _environment;
    private string _gateProgress = string.Empty;
    private string _nextSessionText = string.Empty;
    private string _nextSessionIn = string.Empty;
    private string _liveAccountText = string.Empty;
    private string _liveAccountTone = "neutral";
    private string _killText = string.Empty;
    private string _killTone = "ok";
    private string _heading = "Status";
    private string _nextActionLabel = string.Empty;
    private PageKind? _nextActionPage;
    private bool _startSessionNext;
    private ChartData _paperChart = ChartData.Empty;
    private string _paperValue = string.Empty;
    private string _paperChange = string.Empty;
    private string _paperDirection = "flat";
    private string _paperNote = string.Empty;

    public StatusViewModel(Workspace workspace, QaEngine engine, TimeProvider time, Action<PageKind> navigate, IUserEnvironment environment)
        : base(PageKind.Status, "Overview", "How the paper account is doing, when the next session is, and what to do next.", engine)
    {
        _workspace = workspace;
        _time = time;
        _navigate = navigate;
        _environment = environment;
        NextActionCommand = new RelayCommand(() =>
        {
            if (_nextActionPage is { } page)
            {
                _navigate(page);
                if (_startSessionNext)
                {
                    StartSessionAsked?.Invoke();
                }
            }
        }, () => _nextActionPage is not null);
    }

    /// <summary>Raised when the next action is "start the session" (the shell starts it on the Session page).</summary>
    public event Action? StartSessionAsked;

    public ObservableCollection<StatusLine> Lines { get; } = [];

    public ObservableCollection<string> Steps { get; } = [];

    /// <summary>Gets "Status, Saturday 2026-09-26 12:00 (Stockholm)".</summary>
    public string Heading
    {
        get => _heading;
        private set => Set(ref _heading, value);
    }

    /// <summary>Gets the label of the one button for the next thing to do, or empty when there is nothing to press.</summary>
    public string NextActionLabel
    {
        get => _nextActionLabel;
        private set => Set(ref _nextActionLabel, value);
    }

    public RelayCommand NextActionCommand { get; }

    /// <summary>Gets one entry per day the Confirm gate needs: true for each clean Paper day counted.</summary>
    public ObservableCollection<bool> GateDots { get; } = [];

    /// <summary>Gets "3 of 10 clean Paper days".</summary>
    public string GateProgress
    {
        get => _gateProgress;
        private set => Set(ref _gateProgress, value);
    }

    /// <summary>Gets the next session, e.g. "Mon 28 Sep · decides at 09:10".</summary>
    public string NextSessionText
    {
        get => _nextSessionText;
        private set => Set(ref _nextSessionText, value);
    }

    /// <summary>Gets how long until then, e.g. "in 21 h 10 min".</summary>
    public string NextSessionIn
    {
        get => _nextSessionIn;
        private set => Set(ref _nextSessionIn, value);
    }

    /// <summary>Gets the account live trading may use, masked ("***193"), or that none is chosen.</summary>
    public string LiveAccountText
    {
        get => _liveAccountText;
        private set => Set(ref _liveAccountText, value);
    }

    public string LiveAccountTone
    {
        get => _liveAccountTone;
        private set => Set(ref _liveAccountTone, value);
    }

    /// <summary>Gets "Off" or "ON" for the kill switch.</summary>
    public string KillText
    {
        get => _killText;
        private set => Set(ref _killText, value);
    }

    public string KillTone
    {
        get => _killTone;
        private set => Set(ref _killTone, value);
    }

    /// <summary>Gets the paper account's value at each Paper day's close (from the end-of-day reports).</summary>
    public ChartData PaperChart
    {
        get => _paperChart;
        private set => Set(ref _paperChart, value);
    }

    /// <summary>Gets the paper account's value at the last close, e.g. "5 012,40 kr".</summary>
    public string PaperValue
    {
        get => _paperValue;
        private set => Set(ref _paperValue, value);
    }

    /// <summary>Gets the change since Paper started, e.g. "+12,40 kr (+0,25 %) since 28 Sep".</summary>
    public string PaperChange
    {
        get => _paperChange;
        private set => Set(ref _paperChange, value);
    }

    /// <summary>Gets "up", "down" or "flat" for <see cref="PaperChange"/> (its colour).</summary>
    public string PaperDirection
    {
        get => _paperDirection;
        private set => Set(ref _paperDirection, value);
    }

    public string PaperNote
    {
        get => _paperNote;
        private set => Set(ref _paperNote, value);
    }

    public override async Task RefreshAsync()
    {
        StatusCommand.StatusReport report;
        try
        {
            report = await Task.Run(() => StatusCommand.Build(_workspace.StatusPaths, _time));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or InvalidDataException)
        {
            Say($"Status could not be read: {ex.Message}", isError: true);
            return;
        }

        Heading = string.Create(CultureInfo.InvariantCulture, $"Status, {MarketTime.ToStockholm(report.Now):dddd yyyy-MM-dd HH:mm} (Stockholm)");
        Lines.Clear();
        foreach ((StatusCommand.Mark mark, string label, string text) in report.Lines)
        {
            Lines.Add(new StatusLine(mark switch { StatusCommand.Mark.Ok => "ok", StatusCommand.Mark.Todo => "todo", StatusCommand.Mark.Warn => "warn", _ => "FAIL" }, label, text));
        }

        Steps.Clear();
        int n = 1;
        foreach (string step in report.Steps)
        {
            Steps.Add(string.Create(CultureInfo.InvariantCulture, $"{n++}. {step}"));
        }

        await LoadPaperHistoryAsync();
        LoadNextSession();
        LoadLiveAccount();
        bool killOn = Lines.Any(l => l.Label == "Kill switch" && l.IsFailure);
        (KillText, KillTone) = killOn ? ("ON", "FAIL") : ("Off", "ok");

        (string actionLabel, PageKind? page, bool start) = NextAction(Lines);
        NextActionLabel = actionLabel;
        _nextActionPage = page;
        _startSessionNext = start;
        NextActionCommand.Refresh();
        Say(string.Empty);
    }

    private void LoadNextSession()
    {
        try
        {
            RiskLimits limits = RiskLimits.Load(Path.Combine(_workspace.ConfigDir, RiskLimits.FileName));
            PaperConfig paper = PaperConfig.Load(Path.Combine(_workspace.ConfigDir, PaperConfig.FileName));
            MarketCalendar calendar = MarketCalendarLoader.LoadDirectory(_workspace.ConfigDir);
            DateTimeOffset now = _time.GetUtcNow();
            DateTimeOffset decision = new TradingSchedule(calendar, limits, paper.DecisionTime).NextDecision(now).DecisionUtc;
            NextSessionText = string.Create(CultureInfo.InvariantCulture, $"{MarketTime.ToStockholm(decision):ddd d MMM} · decides at {MarketTime.ToStockholm(decision):HH:mm}");
            NextSessionIn = Until(decision - now);
        }
        catch (Exception ex) when (ex is TradingConfigException or CalendarConfigException or IOException or ArgumentException or InvalidOperationException)
        {
            (NextSessionText, NextSessionIn) = ("Not known", ex.Message);
        }
    }

    /// <summary>"in 2 d 3 h", "in 21 h 10 min", "in 5 min", or "now".</summary>
    internal static string Until(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "now",
        { TotalHours: < 1 } => string.Create(CultureInfo.InvariantCulture, $"in {span.Minutes} min"),
        { TotalDays: < 1 } => string.Create(CultureInfo.InvariantCulture, $"in {span.Hours} h {span.Minutes} min"),
        _ => string.Create(CultureInfo.InvariantCulture, $"in {span.Days} d {span.Hours} h"),
    };

    private void LoadLiveAccount()
    {
        string? raw = _environment.Read(AccountAllowlist.Variable);
        (LiveAccountText, LiveAccountTone) = string.IsNullOrWhiteSpace(raw)
            ? ("Not chosen", "neutral")
            : AccountAllowlist.TryParse(raw, out QuantAnalyst.Core.AccountId id, out _) ? (id.Masked, "live") : ("Not usable", "FAIL");
    }

    private async Task LoadPaperHistoryAsync()
    {
        try
        {
            (IReadOnlyList<EodReport> reports, AuditVerification? chain) = await Task.Run(() =>
            {
                IReadOnlyList<EodReport> all = ChartSources.Reports(_workspace, _time);
                return (all, Directory.Exists(_workspace.AuditDir) ? AuditLog.Verify(_workspace.AuditDir) : null);
            });
            int clean = chain is null ? 0 : PromotionGate.Confirm(reports, chain).Evidence.Count;
            GateDots.Clear();
            for (int i = 0; i < PromotionGate.MinPaperDays; i++)
            {
                GateDots.Add(i < clean);
            }

            GateProgress = string.Create(CultureInfo.InvariantCulture, $"{clean} of {PromotionGate.MinPaperDays} clean Paper days");
            IReadOnlyList<PaperDay> days = ChartSources.PaperDays(reports);
            if (days.Count == 0)
            {
                decimal cash = PaperConfig.Load(Path.Combine(_workspace.ConfigDir, PaperConfig.FileName)).Cash;
                PaperChart = ChartData.Empty;
                PaperValue = Fmt.Sek(cash);
                PaperChange = string.Empty;
                PaperDirection = "flat";
                PaperNote = "The paper account's starting cash. The chart starts after the first Paper session.";
                return;
            }

            decimal start = days[0].StartOfDay;
            decimal end = days[^1].End;
            decimal change = end - start;
            PaperChart = new ChartData
            {
                Main = ChartSources.PaperValuePoints(days),
                Baseline = (double)start,
                Axis = TimeAxis.Daily,
                FormatValue = v => Fmt.Amount((decimal)v, 0),
            };
            PaperValue = Fmt.Sek(end);
            PaperChange = string.Create(CultureInfo.InvariantCulture,
                $"{Fmt.ChangeSek(change)} ({Fmt.ChangePct(start != 0 ? change / start : 0m)}) since {days[0].Date:d MMM}");
            PaperDirection = Tone.Direction(change);
            PaperNote = string.Create(CultureInfo.InvariantCulture, $"Value at each close, {days.Count} Paper day(s).");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or Trading.Risk.TradingConfigException)
        {
            PaperNote = $"The paper history could not be read: {ex.Message}";
        }
    }

    /// <summary>The one button: fix what blocks first, in the order qa status lists its steps, else start the session.</summary>
    internal static (string Label, PageKind? Page, bool StartSession) NextAction(IReadOnlyCollection<StatusLine> lines)
    {
        bool Has(string label, Func<StatusLine, bool> test) => lines.Any(l => l.Label == label && test(l));

        if (Has("Native engine", l => l.IsFailure) || Has("Config", l => l.IsFailure) || Has("Audit log", l => l.IsFailure))
        {
            return ("Fix the problem above first (see the next steps)", null, false);
        }

        if (Has("Kill switch", l => l.IsFailure))
        {
            return ("Go to the session page to clear the kill switch", PageKind.Session, false);
        }

        if (Has("Allowlist", l => l.IsTodo || l.IsFailure))
        {
            return ("Add instruments", PageKind.Instruments, false);
        }

        if (Has("Strategy", l => l.IsTodo || l.IsFailure))
        {
            return ("Choose a strategy", PageKind.Strategy, false);
        }

        if (lines.Any(l => l.IsFailure))
        {
            return ("Fix the problem above first (see the next steps)", null, false);
        }

        return Has("Session", l => l.Text.StartsWith("running", StringComparison.Ordinal))
            ? ("Watch the running session", PageKind.Session, false)
            : ("Start the Paper session", PageKind.Session, true);
    }
}
