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
    private string _phase = "idle";
    private string _phaseText = "Not running";
    private string? _manualTicker;
    private string _manualSide = "Buy";
    private string _manualQuantity = string.Empty;
    private string _manualLimit = string.Empty;

    public SessionViewModel(Workspace workspace, QaEngine engine, TimeProvider time, Func<string> login)
        : base(PageKind.Session, "Trading", "Today's Paper session: the saved strategy on live Avanza prices, with simulated orders. Nothing is sent to Avanza.", engine)
    {
        _workspace = workspace;
        _time = time;
        _login = login;
        engine.LineWritten += OnLine;
        Live = new LiveSession(engine.Ui);
        StartCommand = new AsyncCommand(StartAsync, () => !IsBusy && !KillActive, ex => Say(ex.Message, isError: true));
        StopCommand = new RelayCommand(Engine.Cancel, () => IsRunning);
        ResetKillCommand = new AsyncCommand(ResetKillAsync, () => !IsBusy && KillActive && ResetReason.Trim().Length > 0, ex => Say(ex.Message, isError: true));
        PlaceManualCommand = new AsyncCommand(PlaceManualAsync, () => ManualOrder() is not null, ex => Say(ex.Message, isError: true));
        ReleaseManualCommand = new AsyncCommand(p => ReleaseManualAsync(p as string), p => p is string, ex => Say(ex.Message, isError: true));
        RefreshManualCommand = new RelayCommand(LoadManual);
    }

    public ObservableCollection<string> Log { get; } = [];

    /// <summary>Gets today's session as it runs: KPIs, charts, instrument tiles and orders (docs/plans/11-app-redesign.md).</summary>
    public LiveSession Live { get; }

    /// <summary>Gets the session's state word for its chip: "running", "waiting" or "idle".</summary>
    public string Phase
    {
        get => _phase;
        private set => Set(ref _phase, value);
    }

    /// <summary>Gets the state as the chip reads it, e.g. "Waiting for 09:10" or "Trading".</summary>
    public string PhaseText
    {
        get => _phaseText;
        private set => Set(ref _phaseText, value);
    }

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

    // ---- Trade by hand (plan 23): requests the Paper session sends through the same risk checks ----------------

    /// <summary>Gets the shares a manual order may be for: the list's, then the exiting ones (sell only).</summary>
    public ObservableCollection<string> ManualTickers { get; } = [];

    public IReadOnlyList<string> ManualSides { get; } = ["Buy", "Sell"];

    public string? ManualTicker
    {
        get => _manualTicker;
        set
        {
            if (Set(ref _manualTicker, value))
            {
                PlaceManualCommand.Refresh();
            }
        }
    }

    public string ManualSide
    {
        get => _manualSide;
        set
        {
            if (Set(ref _manualSide, value ?? "Buy"))
            {
                PlaceManualCommand.Refresh();
            }
        }
    }

    /// <summary>Gets or sets the number of shares, as typed.</summary>
    public string ManualQuantity
    {
        get => _manualQuantity;
        set
        {
            if (Set(ref _manualQuantity, value ?? string.Empty))
            {
                PlaceManualCommand.Refresh();
            }
        }
    }

    /// <summary>Gets or sets the limit as typed (empty: the ask for a buy, the bid for a sell).</summary>
    public string ManualLimit
    {
        get => _manualLimit;
        set
        {
            if (Set(ref _manualLimit, value ?? string.Empty))
            {
                PlaceManualCommand.Refresh();
            }
        }
    }

    /// <summary>Gets the waiting requests, today's outcomes and the manual shares, as <c>qa paper orders</c> prints them.</summary>
    public ObservableCollection<string> ManualLines { get; } = [];

    /// <summary>Gets the shares the strategy leaves to you (each with Release).</summary>
    public ObservableCollection<string> ManualShares { get; } = [];

    public AsyncCommand PlaceManualCommand { get; }

    /// <summary>Gets the command that gives a manual share (its ticker the parameter) back to the strategy.</summary>
    public AsyncCommand ReleaseManualCommand { get; }

    public RelayCommand RefreshManualCommand { get; }

    public RelayCommand StopCommand { get; }

    public AsyncCommand ResetKillCommand { get; }

    public override Task RefreshAsync()
    {
        LoadKill();
        LoadAccount();
        LoadSchedule();
        LoadPhase();
        LoadManual();
        return Task.CompletedTask;
    }

    protected override void OnBusyChanged()
    {
        OnPropertyChanged(nameof(IsRunning));
        StartCommand.Refresh();
        StopCommand.Refresh();
        ResetKillCommand.Refresh();
        LoadSchedule();
        LoadPhase();
    }

    private async Task StartAsync()
    {
        Log.Clear();
        Say("Starting: log in with BankID when the QR code appears …");
        _capturing = true;
        CommandResult result;
        try
        {
            Live.Reset();
            result = await Engine.RunAsync(SessionTitle, CommandLines.PaperRun(_workspace, _login()), Live);
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

    /// <summary>The order as typed, or null while it is incomplete: a share, a whole number of shares above zero, an optional limit above zero.</summary>
    private (ManualAction Side, string Ticker, long Quantity, decimal? Limit)? ManualOrder()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        if (ManualTicker is not { } ticker || !long.TryParse(ManualQuantity.Trim(), NumberStyles.None, c, out long quantity) || quantity <= 0)
        {
            return null;
        }

        string limitText = ManualLimit.Trim().Replace(',', '.');
        decimal? limit = null;
        if (limitText.Length > 0)
        {
            if (!decimal.TryParse(limitText, NumberStyles.AllowDecimalPoint, c, out decimal l) || l <= 0)
            {
                return null;
            }

            limit = l;
        }

        return (ManualSide == "Sell" ? ManualAction.Sell : ManualAction.Buy, ticker, quantity, limit);
    }

    private Task PlaceManualAsync()
    {
        if (ManualOrder() is not { } order)
        {
            return Task.CompletedTask;
        }

        ManualOrderRequest request = ManualTrading.Place(_workspace.ConfigDir, _workspace.StateDir, order.Side, order.Ticker, order.Quantity, order.Limit, "app", _time);
        Say(ManualTrading.Placed(request, _workspace.StateDir).Replace("'qa paper release", "Release ('qa paper release", StringComparison.Ordinal));
        ManualQuantity = string.Empty;
        ManualLimit = string.Empty;
        LoadManual();
        return Task.CompletedTask;
    }

    private Task ReleaseManualAsync(string? ticker)
    {
        if (ticker is null)
        {
            return Task.CompletedTask;
        }

        ManualOrderRequest request = ManualTrading.Place(_workspace.ConfigDir, _workspace.StateDir, ManualAction.Release, ticker, 0, null, "app", _time);
        Say(ManualTrading.Placed(request, _workspace.StateDir));
        LoadManual();
        return Task.CompletedTask;
    }

    /// <summary>The shares to trade by hand, the requests and the manual shares (plan 23); local files only.</summary>
    private void LoadManual()
    {
        string? selected = ManualTicker;
        ManualTickers.Clear();
        ManualLines.Clear();
        ManualShares.Clear();
        try
        {
            Universe universe = Universe.Load(Path.Combine(_workspace.ConfigDir, Universe.FileName));
            foreach (UniverseEntry e in universe.Entries.Concat(universe.Exiting))
            {
                ManualTickers.Add(e.Ticker);
            }

            foreach (string line in ManualTrading.Lines(_workspace.StateDir, _workspace.ConfigDir, _time))
            {
                ManualLines.Add(line);
            }

            foreach (string ticker in ManualTrading.ManualShares(Path.Combine(_workspace.StateDir, TradingCommands.PaperDirName), _workspace.ConfigDir))
            {
                ManualShares.Add(ticker);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TradingConfigException or PaperBookException or System.Text.Json.JsonException)
        {
            ManualLines.Add($"The manual orders could not be read: {ex.Message}");
        }

        ManualTicker = selected is not null && ManualTickers.Contains(selected) ? selected : ManualTickers.FirstOrDefault();
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

    /// <summary>Running and before today's decision: waiting; running after it: trading; otherwise not running.</summary>
    private void LoadPhase()
    {
        if (!IsRunning)
        {
            (Phase, PhaseText) = ("idle", KillActive ? "Stopped by the kill switch" : "Not running");
            return;
        }

        (Phase, PhaseText) = Live.DecisionUtc is { } d && _time.GetUtcNow() < d
            ? ("waiting", "Waiting for " + MarketTime.ToStockholm(d).ToString("HH:mm", CultureInfo.InvariantCulture))
            : ("running", "Trading");
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
            (TradingSchedule stockholm, IReadOnlyList<TradingSchedule> foreign) = StatusCommand.SessionSchedules(_workspace.ConfigDir, _workspace.Store);
            Schedule = StatusCommand.NextSession(_time.GetUtcNow(), stockholm, foreign).Replace("qa paper run", "press Start", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is TradingConfigException or CalendarConfigException or IOException or ArgumentException or InvalidOperationException)
        {
            Schedule = $"The schedule could not be read: {ex.Message}";
        }
    }
}
