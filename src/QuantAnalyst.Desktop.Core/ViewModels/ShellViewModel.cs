using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Mvvm;
using QuantAnalyst.Trading.Kill;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>
/// The window: the pages, the login method, the activity log of every command, the BankID overlay and the red
/// KILL button. KILL writes the kill flag directly (like <c>qa kill</c>), so it works even while a session runs.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private const int MaxActivityLines = 5000;

    private readonly TimeProvider _time;
    private PageViewModel _selected;
    private string _loginMethod;
    private string _notice = string.Empty;

    /// <param name="accounts">Where the Accounts page loads your accounts (default: the CLI's reads, one login each).</param>
    /// <param name="environment">Your user environment (default: the real one; the live-trading account lives there).</param>
    public ShellViewModel(Workspace workspace, QaEngine engine, TimeProvider time, IAccountSource? accounts = null, IUserEnvironment? environment = null)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _loginMethod = DefaultLogin();
        IUserEnvironment env = environment ?? new UserEnvironment();

        Status = new StatusViewModel(workspace, engine, time, Navigate, env);
        Instruments = new InstrumentsViewModel(workspace, engine, () => LoginMethod);
        Strategy = new StrategyViewModel(workspace, engine);
        Session = new SessionViewModel(workspace, engine, time, () => LoginMethod);
        Reports = new ReportsViewModel(workspace, engine, time);
        Accounts = new AccountsViewModel(workspace, engine, accounts ?? new EngineAccountSource(engine, workspace), env, time, () => LoginMethod);
        Pages = [Status, Session, Accounts, Instruments, Strategy, Reports];
        _selected = Status;

        Status.StartSessionAsked += () => Session.StartCommand.Execute(null);
        KillCommand = new RelayCommand(Kill);
        CancelCommand = new RelayCommand(Engine.Cancel, () => Engine.IsBusy);
        Engine.LineWritten += line =>
        {
            Activity.Add(line);
            while (Activity.Count > MaxActivityLines)
            {
                Activity.RemoveAt(0);
            }
        };
        Engine.PropertyChanged += OnEnginePropertyChanged;
    }

    public Workspace Workspace { get; }

    public QaEngine Engine { get; }

    public StatusViewModel Status { get; }

    public InstrumentsViewModel Instruments { get; }

    public StrategyViewModel Strategy { get; }

    public SessionViewModel Session { get; }

    public ReportsViewModel Reports { get; }

    public AccountsViewModel Accounts { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

    public PageViewModel SelectedPage
    {
        get => _selected;
        set
        {
            if (value is not null && Set(ref _selected, value))
            {
                _ = value.RefreshAsync();
            }
        }
    }

    /// <summary>Gets the banner that is always shown: what mode the app trades in.</summary>
    public string ModeBanner { get; } = "PAPER · simulated orders on live prices";

    public IReadOnlyList<string> LoginMethods { get; } = ["bankid", "totp"];

    /// <summary>Gets or sets how Avanza logins are made: bankid (you approve on the phone) or totp (unattended, needs stored credentials).</summary>
    public string LoginMethod
    {
        get => _loginMethod;
        set => Set(ref _loginMethod, LoginMethods.Contains(value) ? value : _loginMethod);
    }

    /// <summary>Gets every line every command printed, oldest first.</summary>
    public ObservableCollection<OutputLine> Activity { get; } = [];

    /// <summary>Gets what the busy indicator shows, e.g. "Running: Import ERIC-B".</summary>
    public string BusyText => Engine.CurrentCommand is { } c ? $"Running: {c}" : "Ready";

    /// <summary>Gets the last notice from the KILL button.</summary>
    public string Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    public RelayCommand KillCommand { get; }

    /// <summary>Gets the command that stops whatever runs (also the BankID overlay's Cancel).</summary>
    public RelayCommand CancelCommand { get; }

    public void Navigate(PageKind kind) => SelectedPage = Pages.First(p => p.Kind == kind);

    public Task RefreshCurrentAsync() => SelectedPage.RefreshAsync();

    private void Kill()
    {
        try
        {
            KillSwitch.Request(Workspace.KillFile, "KILL button in the Windows app", _time);
            Notice = string.Create(CultureInfo.InvariantCulture,
                $"KILL written at {MarketTime.ToStockholm(_time.GetUtcNow()):HH:mm:ss}. A running session halts within a second and cancels every working order. Clear it on the session page.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = $"KILL could not be written ({ex.Message}). Create a file named KILL in {Workspace.Root} by hand.";
        }

        _ = Session.RefreshAsync();
    }

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QaEngine.CurrentCommand))
        {
            OnPropertyChanged(nameof(BusyText));
            CancelCommand.Refresh();
        }
    }

    private static string DefaultLogin() =>
        Environment.GetEnvironmentVariable(Cli.Commands.AvanzaCommands.LoginMethodVariable) is { Length: > 0 } v && v.Trim().Equals("totp", StringComparison.OrdinalIgnoreCase)
            ? "totp"
            : "bankid";
}
