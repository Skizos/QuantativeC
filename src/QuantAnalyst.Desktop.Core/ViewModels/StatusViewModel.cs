using System.Collections.ObjectModel;
using System.Globalization;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Desktop.Core.Charts;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Mvvm;
using QuantAnalyst.Desktop.Core.Presentation;
using QuantAnalyst.Trading.Paper;

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
/// The start page: <c>qa status</c> as a checklist (the same <c>StatusCommand.Build</c>, read-only), the numbered next
/// steps, and one button for the most useful next thing.
/// </summary>
public sealed class StatusViewModel : PageViewModel
{
    private readonly Workspace _workspace;
    private readonly TimeProvider _time;
    private readonly Action<PageKind> _navigate;
    private string _heading = "Status";
    private string _nextActionLabel = string.Empty;
    private PageKind? _nextActionPage;
    private bool _startSessionNext;
    private ChartData _paperChart = ChartData.Empty;
    private string _paperValue = string.Empty;
    private string _paperChange = string.Empty;
    private string _paperDirection = "flat";
    private string _paperNote = string.Empty;

    public StatusViewModel(Workspace workspace, QaEngine engine, TimeProvider time, Action<PageKind> navigate)
        : base(PageKind.Status, "Status", "What is set up, what is missing, and what to do next.", engine)
    {
        _workspace = workspace;
        _time = time;
        _navigate = navigate;
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

        (string actionLabel, PageKind? page, bool start) = NextAction(Lines);
        NextActionLabel = actionLabel;
        _nextActionPage = page;
        _startSessionNext = start;
        NextActionCommand.Refresh();
        Say(string.Empty);
    }

    private async Task LoadPaperHistoryAsync()
    {
        try
        {
            IReadOnlyList<PaperDay> days = await Task.Run(() => ChartSources.PaperDays(_workspace, _time));
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
