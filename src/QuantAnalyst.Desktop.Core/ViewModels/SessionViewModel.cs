using System.Collections.ObjectModel;
using System.Globalization;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Calendar;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Mvvm;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Risk;
using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>One paper position at cost.</summary>
public sealed record PositionRow(string Ticker, long Quantity, string Cost, string LastFill);

/// <summary>
/// The day's Paper session: <b>Start</b> runs <c>qa paper run</c> (one read-only Avanza login, history update, the
/// decision at 09:10, simulated fills on live prices until the close), <b>Stop</b> is its Ctrl+C (working orders are
/// cancelled and the partial report is written), and the kill switch can be cleared here when no session runs.
/// </summary>
public sealed class SessionViewModel : PageViewModel
{
    /// <summary>The engine title of a Paper session.</summary>
    public const string SessionTitle = "Paper session";

    private const int MaxLogLines = 5000;

    private readonly Workspace _workspace;
    private readonly TimeProvider _time;
    private readonly Func<string> _login;
    private bool _capturing;
    private string _schedule = string.Empty;
    private string _account = string.Empty;
    private string _killState = string.Empty;
    private bool _killActive;
    private string _resetReason = string.Empty;

    public SessionViewModel(Workspace workspace, QaEngine engine, TimeProvider time, Func<string> login)
        : base(PageKind.Session, "Paper session", "Trade the saved strategy on paper, on live Avanza prices. Nothing is sent to Avanza.", engine)
    {
        _workspace = workspace;
        _time = time;
        _login = login;
        engine.LineWritten += OnLine;
        StartCommand = new AsyncCommand(StartAsync, () => !IsBusy && !KillActive, ex => Say(ex.Message, isError: true));
        StopCommand = new RelayCommand(Engine.Cancel, () => IsRunning);
        ResetKillCommand = new AsyncCommand(ResetKillAsync, () => !IsBusy && KillActive && ResetReason.Trim().Length > 0, ex => Say(ex.Message, isError: true));
    }

    public ObservableCollection<string> Log { get; } = [];

    public ObservableCollection<PositionRow> Positions { get; } = [];

    /// <summary>Gets a value indicating whether a Paper session is running in this app now.</summary>
    public bool IsRunning => Engine.CurrentCommand == SessionTitle;

    /// <summary>Gets when to start: "Next session: Monday 2026-09-28 …", or that one is running.</summary>
    public string Schedule
    {
        get => _schedule;
        private set => Set(ref _schedule, value);
    }

    /// <summary>Gets the paper account in one line: cash, realised P&amp;L, fees.</summary>
    public string Account
    {
        get => _account;
        private set => Set(ref _account, value);
    }

    public bool KillActive
    {
        get => _killActive;
        private set
        {
            if (Set(ref _killActive, value))
            {
                StartCommand.Refresh();
                ResetKillCommand.Refresh();
            }
        }
    }

    public string KillState
    {
        get => _killState;
        private set => Set(ref _killState, value);
    }

    /// <summary>Gets or sets why you clear the kill switch (kept in the audit log).</summary>
    public string ResetReason
    {
        get => _resetReason;
        set
        {
            if (Set(ref _resetReason, value ?? string.Empty))
            {
                ResetKillCommand.Refresh();
            }
        }
    }

    public AsyncCommand StartCommand { get; }

    public RelayCommand StopCommand { get; }

    public AsyncCommand ResetKillCommand { get; }

    public override Task RefreshAsync()
    {
        LoadKill();
        LoadAccount();
        LoadSchedule();
        return Task.CompletedTask;
    }

    protected override void OnBusyChanged()
    {
        OnPropertyChanged(nameof(IsRunning));
        StartCommand.Refresh();
        StopCommand.Refresh();
        ResetKillCommand.Refresh();
        LoadSchedule();
    }

    private async Task StartAsync()
    {
        Log.Clear();
        Say("Starting: log in with BankID when the QR code appears …");
        _capturing = true;
        CommandResult result;
        try
        {
            result = await Engine.RunAsync(SessionTitle, CommandLines.PaperRun(_workspace, _login()));
        }
        finally
        {
            _capturing = false;
        }

        Say(result switch
        {
            { Cancelled: true } => "Stopped. Working orders were cancelled and the partial report was written (see Reports).",
            { ExitCode: 0 } => "The session is over. Its report is on the Reports page.",
            { ExitCode: 3 } => "HALTED: the kill switch fired or was on. Read the log and the day's report before clearing it.",
            { ExitCode: 4 } => "Login is locked after a failed attempt. Check that BankID login works on avanza.se, then clear the lock in the terminal (qa login --clear-lock).",
            _ => $"The session did not run: {Why(result)}",
        }, !result.Succeeded);
        await RefreshAsync();
    }

    private async Task ResetKillAsync()
    {
        CommandResult result = await Engine.RunAsync("Clear the kill switch", CommandLines.KillReset(_workspace, ResetReason.Trim()));
        Say(result.Succeeded ? "The kill switch is cleared." : $"Not cleared: {Why(result)}", !result.Succeeded);
        if (result.Succeeded)
        {
            ResetReason = string.Empty;
        }

        await RefreshAsync();
    }

    private void OnLine(OutputLine line)
    {
        if (!_capturing)
        {
            return;
        }

        Log.Add(line.Text);
        while (Log.Count > MaxLogLines)
        {
            Log.RemoveAt(0);
        }
    }

    private void LoadKill()
    {
        try
        {
            KillRecord? kill = KillSwitch.RecordedKill(_workspace.KillFile, _workspace.StateDir);
            KillActive = kill is not null;
            KillState = kill is null
                ? "Kill switch: off. The red KILL button stops everything at once."
                : string.Create(CultureInfo.InvariantCulture, $"Kill switch: ON since {MarketTime.ToStockholm(kill.SinceUtc):yyyy-MM-dd HH:mm} ({kill.Source}: {kill.Reason}). Nothing trades until it is cleared.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            KillActive = true;
            KillState = $"Kill switch state unreadable: {ex.Message}";
        }
    }

    private void LoadAccount()
    {
        Positions.Clear();
        string dir = Path.Combine(_workspace.StateDir, "paper");
        if (!File.Exists(Path.Combine(dir, PaperBook.FileName)))
        {
            Account = "No paper account yet: the first session opens it with the cash in config/paper.json.";
            return;
        }

        try
        {
            PaperBook book = PaperBook.OpenOrCreate(dir, new PaperConfig("?", 1m, new TimeOnly(9, 10)), null, _time, out _);
            CultureInfo c = CultureInfo.InvariantCulture;
            Account = string.Create(c, $"Cash {book.Cash:N2} SEK · started with {book.StartingCash:N2} · realised P&L {book.RealizedPnl:N2} · fees {book.FeesPaid:N2} · courtage class {book.Costs}");
            foreach (PaperPosition p in book.Positions)
            {
                Positions.Add(new PositionRow(p.Ticker, p.Quantity, string.Create(c, $"{p.CostBasis:N2} SEK"), string.Create(c, $"{p.LastFillPrice:0.####}")));
            }
        }
        catch (Exception ex) when (ex is PaperBookException or IOException or UnauthorizedAccessException)
        {
            Account = $"The paper account could not be read: {ex.Message}";
        }
    }

    private void LoadSchedule()
    {
        if (IsRunning)
        {
            Schedule = "A session is running. Keep this window open until it ends after the close; Stop ends it early.";
            return;
        }

        try
        {
            RiskLimits limits = RiskLimits.Load(Path.Combine(_workspace.ConfigDir, RiskLimits.FileName));
            PaperConfig paper = PaperConfig.Load(Path.Combine(_workspace.ConfigDir, PaperConfig.FileName));
            MarketCalendar calendar = MarketCalendarLoader.LoadDirectory(_workspace.ConfigDir);
            Schedule = StatusCommand.NextSession(_time.GetUtcNow(), new TradingSchedule(calendar, limits, paper.DecisionTime)).Replace("qa paper run", "press Start", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is TradingConfigException or CalendarConfigException or IOException or ArgumentException or InvalidOperationException)
        {
            Schedule = $"The schedule could not be read: {ex.Message}";
        }
    }
}
