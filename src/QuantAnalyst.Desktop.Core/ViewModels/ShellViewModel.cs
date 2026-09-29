using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Desktop.Core.Charts;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Mvvm;
using QuantAnalyst.Trading.Kill;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>A choice in the page header's <b>Beside</b> list: a page to show on the right, or nothing.</summary>
public sealed record PageChoice(string Title, PageKind? Kind)
{
    public override string ToString() => Title;
}

/// <summary>
/// The window: the pages, the login method, the activity log of every command, the BankID overlay and the red
/// KILL button. KILL writes the kill flag directly (like <c>qa kill</c>), so it works even while a session runs.
/// Several pages can be seen at once (docs/plans/13-pages-side-by-side.md): one <see cref="BesidePage"/> on the right
/// of the selected one, and any page in windows of its own (<see cref="OpenInWindow"/>). Charts is the exception: beside
/// or in a window it is a chart of its own, so two charts can show two names.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private const int MaxActivityLines = 5000;

    private readonly TimeProvider _time;
    private PageViewModel _selected;
    private string _loginMethod;
    private readonly HistoryCandles _history = new();
    private readonly List<PageViewModel> _windowPages = [];
    private string _notice = string.Empty;
    private PageViewModel? _beside;
    private PageChoice _besideChoice;
    private bool _navCollapsed;

    /// <param name="accounts">Where the Accounts page loads your accounts (default: the CLI's reads, one login each).</param>
    /// <param name="environment">Your user environment (default: the real one; the live-trading account lives there).</param>
    /// <param name="search">Where the Instruments page searches Avanza's market (default: the CLI's search, one login until Done).</param>
    public ShellViewModel(
        Workspace workspace, QaEngine engine, TimeProvider time, IAccountSource? accounts = null, IUserEnvironment? environment = null, IMarketSearch? search = null)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _loginMethod = DefaultLogin();
        IUserEnvironment env = environment ?? new UserEnvironment();

        Status = new StatusViewModel(workspace, engine, time, Navigate, env);
        Instruments = new InstrumentsViewModel(workspace, engine, () => LoginMethod, search ?? new EngineMarketSearch(engine, workspace, time, () => LoginMethod), time);
        Strategy = new StrategyViewModel(workspace, engine);
        Session = new SessionViewModel(workspace, engine, time, () => LoginMethod);
        Reports = new ReportsViewModel(workspace, engine, time);
        Accounts = new AccountsViewModel(workspace, engine, accounts ?? new EngineAccountSource(engine, workspace), env, time, () => LoginMethod);
        Charts = new ChartsViewModel(workspace, engine, time, Session.Live, _history, OpenInWindow);
        Pages = [Status, Session, Charts, Accounts, Instruments, Strategy, Reports];
        _selected = Status;
        BesideChoices = [new PageChoice("Nothing", null), .. Pages.Select(p => new PageChoice(p.Title, p.Kind))];
        _besideChoice = BesideChoices[0];
        CloseBesideCommand = new RelayCommand(() => SelectedBeside = BesideChoices[0], () => _beside is not null);
        OpenWindowCommand = new RelayCommand(p =>
        {
            if (p is PageViewModel page)
            {
                OpenInWindow(page);
            }
        });
        ToggleNavCommand = new RelayCommand(() => NavCollapsed = !NavCollapsed);

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

    /// <summary>Gets the Charts page; <see cref="NewCharts"/> makes one for another window or the right side.</summary>
    public ChartsViewModel Charts { get; }

    /// <summary>
    /// Raised when a page should get a window of its own (<see cref="OpenInWindow"/>). The window shows the page it is
    /// given, refreshes it when it opens, and calls <see cref="WindowClosed"/> when it closes.
    /// </summary>
    public event Action<PageViewModel>? PageWindowRequested;

    public IReadOnlyList<PageViewModel> Pages { get; }

    public PageViewModel SelectedPage
    {
        get => _selected;
        set
        {
            PageViewModel before = _selected;
            if (value is not null && Set(ref _selected, value))
            {
                if (ReferenceEquals(value, _beside))
                {
                    SelectedBeside = BesideChoices[0]; // the page moved to the left
                }

                Release(before);
                Refreshing = value.RefreshAsync();
            }
        }
    }

    /// <summary>Gets the choices of the Beside list: nothing, or one of the pages.</summary>
    public IReadOnlyList<PageChoice> BesideChoices { get; }

    /// <summary>
    /// Gets or sets what is shown on the right of the selected page. Choosing the page that is already on the left does
    /// nothing (the list goes back), except Charts: that opens a second chart.
    /// </summary>
    public PageChoice SelectedBeside
    {
        get => _besideChoice;
        set
        {
            if (value is null || value == _besideChoice)
            {
                return;
            }

            if (value.Kind is { } kind && kind != PageKind.Charts && kind == SelectedPage.Kind)
            {
                Engine.Ui.Post(() => OnPropertyChanged(nameof(SelectedBeside)));
                return;
            }

            _besideChoice = value;
            OnPropertyChanged();
            ShowBeside(value.Kind);
        }
    }

    /// <summary>Gets the page on the right of the selected one, or null.</summary>
    public PageViewModel? BesidePage
    {
        get => _beside;
        private set
        {
            if (Set(ref _beside, value))
            {
                OnPropertyChanged(nameof(HasBeside));
                CloseBesideCommand.Refresh();
            }
        }
    }

    public bool HasBeside => _beside is not null;

    public RelayCommand CloseBesideCommand { get; }

    /// <summary>Gets the command that opens the page given as its parameter in a window of its own.</summary>
    public RelayCommand OpenWindowCommand { get; }

    /// <summary>Gets the pages shown in windows of their own (a page opened twice is here twice).</summary>
    public IReadOnlyList<PageViewModel> WindowPages => _windowPages;

    /// <summary>
    /// Gets the refresh a page got when it was shown (not awaited by the setter, which the UI calls). The app's
    /// continuations run on the UI thread; tests await this so the refresh can't interleave with what they do next.
    /// </summary>
    internal Task Refreshing { get; private set; } = Task.CompletedTask;

    /// <summary>Gets or sets whether the navigation rail shows icons only (more room for the pages).</summary>
    public bool NavCollapsed
    {
        get => _navCollapsed;
        set => Set(ref _navCollapsed, value);
    }

    public RelayCommand ToggleNavCommand { get; }

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

    /// <summary>
    /// Refreshes every page on screen: the selected one, the one beside it and those in windows, each once. While a
    /// command runs only the Trading page is refreshed (the other pages read files a session may be writing).
    /// </summary>
    public async Task RefreshVisibleAsync()
    {
        PageViewModel[] visible = [.. new[] { SelectedPage, _beside }.OfType<PageViewModel>().Concat(_windowPages).Distinct()];
        foreach (PageViewModel page in visible)
        {
            if (!Engine.IsBusy || page is SessionViewModel)
            {
                await page.RefreshAsync();
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="page"/> in a window of its own. The window shows the same page as the main window (the
    /// same numbers and buttons), except a chart, which gets an independent copy starting like it.
    /// </summary>
    public void OpenInWindow(PageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        PageViewModel shown = page is ChartsViewModel charts ? NewCharts(charts) : page;
        _windowPages.Add(shown);
        PageWindowRequested?.Invoke(shown);
    }

    /// <summary>A page's window closed: it is no longer refreshed, and a chart of its own lets go of the session.</summary>
    public void WindowClosed(PageViewModel page)
    {
        _windowPages.Remove(page);
        Release(page);
    }

    /// <summary>
    /// A charts page for another window, starting like <paramref name="like"/>. It shares the stored candles and the
    /// running session with every other charts page; dispose it when its window closes.
    /// </summary>
    public ChartsViewModel NewCharts(ChartsViewModel? like = null)
    {
        var charts = new ChartsViewModel(Workspace, Engine, _time, Session.Live, _history, OpenInWindow);
        if (like is not null)
        {
            charts.CopySettings(like);
        }

        return charts;
    }

    private void ShowBeside(PageKind? kind)
    {
        PageViewModel? before = _beside;
        PageViewModel? next = kind switch
        {
            null => null,
            PageKind.Charts => NewCharts(Charts),
            { } k => Pages.First(p => p.Kind == k),
        };
        BesidePage = next;
        if (before is not null)
        {
            Release(before);
        }

        if (next is not null)
        {
            Refreshing = next.RefreshAsync();
        }
    }

    /// <summary>
    /// A page is shown in one place less. Once nothing shows it: a chart of its own is disposed, and the Instruments
    /// page's search lets its login go (nothing else can run while it is open). The app's own pages live as long as it does.
    /// </summary>
    private void Release(PageViewModel page)
    {
        if (Shows(page))
        {
            return;
        }

        if (page is ChartsViewModel charts && !ReferenceEquals(charts, Charts))
        {
            charts.Dispose();
        }
        else if (ReferenceEquals(page, Instruments))
        {
            Instruments.CloseSearch();
        }
    }

    private bool Shows(PageViewModel page) =>
        ReferenceEquals(page, _selected) || ReferenceEquals(page, _beside) || _windowPages.Contains(page);

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
