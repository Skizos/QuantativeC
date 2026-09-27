using System.Collections.ObjectModel;
using System.Globalization;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Mvvm;
using QuantAnalyst.Desktop.Core.Presentation;
using QuantAnalyst.Trading.Accounts;
using QuantAnalyst.Trading.Paper;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>One account card: the Paper account or one of your Avanza accounts (the number always masked).</summary>
public sealed record AccountRow(
    string Key,
    string Title,
    string Subtitle,
    string Value,
    bool IsPaper,
    bool CanTradeLive,
    bool IsLive,
    IReadOnlyList<string> Problems)
{
    /// <summary>Gets the chip on the card: "Live trading", "Can trade live", "Simulated" or "Can't trade live".</summary>
    public string Badge => IsPaper ? "Simulated" : IsLive ? "Live trading" : CanTradeLive ? "Can trade live" : "Can't trade live";

    /// <summary>Gets the badge's tone word (see <see cref="Tone"/>).</summary>
    public string BadgeTone => IsPaper ? "waiting" : IsLive ? "live" : CanTradeLive ? "info" : "neutral";
}

/// <summary>A holding of the selected account, with its gain like a bank app shows it.</summary>
public sealed record HoldingRow(string Name, string Volume, string Value, string Gain, string GainPct, string Direction);

/// <summary>
/// Your accounts (docs/plans/11-app-redesign.md): the Paper account, and after <b>Load</b> (one Avanza login) your
/// Avanza accounts with value, buying power and holdings. The one account live trading may use (R1,
/// <c>AVANZA__ALLOWEDACCOUNTIDS</c>) is chosen here with <b>Use for live trading</b>, confirmed by typing the account's
/// last 3 digits; <b>Stop live trading</b> clears it. Account numbers are shown masked; the full number exists only in
/// memory and in that one variable. Nothing here trades: Confirm still runs every startup check in the terminal.
/// </summary>
public sealed class AccountsViewModel : PageViewModel
{
    private const string PaperKey = "PAPER";

    private readonly Workspace _workspace;
    private readonly IAccountSource _source;
    private readonly IUserEnvironment _environment;
    private readonly TimeProvider _time;
    private readonly Func<string> _login;
    private readonly Dictionary<string, AccountId> _ids = new(StringComparer.Ordinal);
    private AccountOverview? _overview;
    private AccountRow? _selected;
    private string _selectedTitle = string.Empty;
    private string _selectedValue = string.Empty;
    private string _selectedDetail = string.Empty;
    private string _liveAccountText = string.Empty;
    private string _liveAccountTone = "neutral";
    private string _confirmDigits = string.Empty;
    private string _loadedAt = "Not loaded yet: Load asks for one BankID login and reads your accounts (read-only).";
    private IReadOnlyList<string> _selectedProblems = [];

    public AccountsViewModel(Workspace workspace, QaEngine engine, IAccountSource source, IUserEnvironment environment, TimeProvider time, Func<string> login)
        : base(PageKind.Accounts, "Accounts", "Your paper account and your Avanza accounts; choose the one live trading may use.", engine)
    {
        _workspace = workspace;
        _source = source;
        _environment = environment;
        _time = time;
        _login = login;
        LoadCommand = new AsyncCommand(LoadAsync, () => !IsBusy, ex => Say(ex.Message, isError: true));
        UseForLiveCommand = new RelayCommand(UseForLive, () => _selected is { IsPaper: false, CanTradeLive: true, IsLive: false } && !IsBusy);
        StopLiveCommand = new RelayCommand(StopLive, () => !string.IsNullOrWhiteSpace(_environment.Read(AccountAllowlist.Variable)) && !IsBusy);
    }

    public ObservableCollection<AccountRow> Accounts { get; } = [];

    public ObservableCollection<HoldingRow> Holdings { get; } = [];

    public AccountRow? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                ConfirmDigits = string.Empty;
                ShowSelected();
                UseForLiveCommand.Refresh();
                OnPropertyChanged(nameof(CanChooseSelected));
            }
        }
    }

    /// <summary>Gets a value indicating whether the selected account can be made the live-trading account (and isn't yet).</summary>
    public bool CanChooseSelected => _selected is { IsPaper: false, CanTradeLive: true, IsLive: false };

    public string SelectedTitle
    {
        get => _selectedTitle;
        private set => Set(ref _selectedTitle, value);
    }

    /// <summary>Gets the selected account's value, e.g. "5 012,40 kr".</summary>
    public string SelectedValue
    {
        get => _selectedValue;
        private set => Set(ref _selectedValue, value);
    }

    /// <summary>Gets cash and buying power, or what the paper account is.</summary>
    public string SelectedDetail
    {
        get => _selectedDetail;
        private set => Set(ref _selectedDetail, value);
    }

    /// <summary>Gets why the selected account can't be the live-trading account (empty when it can).</summary>
    public IReadOnlyList<string> SelectedProblems
    {
        get => _selectedProblems;
        private set => Set(ref _selectedProblems, value);
    }

    /// <summary>Gets which account live trading may use, e.g. "***193 · ISK", or that none is chosen.</summary>
    public string LiveAccountText
    {
        get => _liveAccountText;
        private set => Set(ref _liveAccountText, value);
    }

    /// <summary>Gets "live" when an account is chosen, else "neutral" (the chip's colour).</summary>
    public string LiveAccountTone
    {
        get => _liveAccountTone;
        private set => Set(ref _liveAccountTone, value);
    }

    /// <summary>Gets or sets the last 3 digits you type to confirm the live-trading account.</summary>
    public string ConfirmDigits
    {
        get => _confirmDigits;
        set => Set(ref _confirmDigits, value ?? string.Empty);
    }

    /// <summary>Gets when the Avanza accounts were read, or how to read them.</summary>
    public string LoadedAt
    {
        get => _loadedAt;
        private set => Set(ref _loadedAt, value);
    }

    public AsyncCommand LoadCommand { get; }

    public RelayCommand UseForLiveCommand { get; }

    public RelayCommand StopLiveCommand { get; }

    public override Task RefreshAsync()
    {
        Fill(_selected?.Key);
        return Task.CompletedTask;
    }

    protected override void OnBusyChanged()
    {
        LoadCommand.Refresh();
        UseForLiveCommand.Refresh();
        StopLiveCommand.Refresh();
    }

    /// <summary>Rebuilds the cards, keeping <paramref name="keep"/> selected; else the live account, else your first Avanza account.</summary>
    private void Fill(string? keep)
    {
        Accounts.Clear();
        Accounts.Add(PaperRow());
        if (_overview is { } o)
        {
            for (int i = 0; i < o.Accounts.Count; i++)
            {
                Accounts.Add(Row(KeyOf(i), o.Accounts[i]));
            }
        }

        Selected = Accounts.FirstOrDefault(r => r.Key == keep)
                   ?? Accounts.FirstOrDefault(r => r.IsLive)
                   ?? Accounts.FirstOrDefault(r => !r.IsPaper)
                   ?? Accounts[0];
        ShowSelected();
        ShowLiveAccount();
        StopLiveCommand.Refresh();
    }

    private async Task LoadAsync()
    {
        Say("Loading your accounts: approve the login in BankID when the QR code appears …");
        try
        {
            _overview = await _source.LoadAsync(_login());
        }
        catch (Exception ex) when (ex is BrokerException or IOException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            Say($"Your accounts could not be loaded: {ex.Message}", isError: true);
            return;
        }

        _ids.Clear();
        for (int i = 0; i < _overview.Accounts.Count; i++)
        {
            _ids[KeyOf(i)] = _overview.Accounts[i].Id;
        }

        LoadedAt = string.Create(CultureInfo.InvariantCulture,
            $"Read from Avanza at {MarketTime.ToStockholm(_overview.Portfolio.RetrievedAtUtc):HH:mm} (one login; values as Avanza reports them).");
        Fill(keep: null);
        Say(string.Create(CultureInfo.InvariantCulture, $"Loaded {_overview.Accounts.Count} account(s)."));
    }

    private void UseForLive()
    {
        if (_selected is not { IsPaper: false, CanTradeLive: true } row || !_ids.TryGetValue(row.Key, out AccountId id))
        {
            return;
        }

        string digits = id.Masked[^3..];
        if (ConfirmDigits.Trim() != digits)
        {
            Say($"Type the last 3 digits of the account ({id.Masked}) to confirm. Nothing was changed.", isError: true);
            return;
        }

        _environment.Write(AccountAllowlist.Variable, id.Value);
        ConfirmDigits = string.Empty;
        Rebuild();
        Say($"{id.Masked} is now the one account live trading may use. Confirm still runs every startup check, in the terminal.");
    }

    private void StopLive()
    {
        _environment.Write(AccountAllowlist.Variable, null);
        Rebuild();
        Say("No account may trade live now. Confirm refuses to start until you choose one again.");
    }

    /// <summary>After the choice changed: the loaded accounts get their new live flag (no new login needed).</summary>
    private void Rebuild()
    {
        if (_overview is { } o)
        {
            AccountId? live = CurrentLiveId();
            _overview = o with
            {
                Accounts = [.. o.Accounts.Select(a => a with { IsLiveAccount = live is { } l && l == a.Id && a.CanTradeLive })],
            };
        }

        _ = RefreshAsync();
    }

    private AccountId? CurrentLiveId() =>
        AccountAllowlist.TryParse(_environment.Read(AccountAllowlist.Variable), out AccountId id, out _) ? id : null;

    private void ShowLiveAccount()
    {
        string? raw = _environment.Read(AccountAllowlist.Variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            (LiveAccountText, LiveAccountTone) = ("No account may trade live yet", "neutral");
            return;
        }

        if (!AccountAllowlist.TryParse(raw, out AccountId id, out string problem))
        {
            (LiveAccountText, LiveAccountTone) = ($"The setting is not usable: {problem}", "FAIL");
            return;
        }

        AccountSummary? known = _overview?.Accounts.FirstOrDefault(a => a.Id == id);
        LiveAccountText = known is null ? $"{id.Masked} (load your accounts to check it)" : $"{id.Masked} · {known.Type}";
        LiveAccountTone = known is null || known.CanTradeLive ? "live" : "FAIL";
    }

    private void ShowSelected()
    {
        Holdings.Clear();
        AccountRow? row = _selected;
        SelectedTitle = row?.Title ?? string.Empty;
        SelectedValue = row?.Value ?? string.Empty;
        SelectedProblems = row?.Problems ?? [];
        if (row is null)
        {
            SelectedDetail = string.Empty;
            return;
        }

        if (row.IsPaper)
        {
            ShowPaperHoldings();
            return;
        }

        if (_overview is not { } o || !_ids.TryGetValue(row.Key, out AccountId id))
        {
            return;
        }

        AccountSummary a = o.Accounts.First(x => x.Id == id);
        decimal cash = o.Portfolio.Cash.Where(c => c.Account == id).Sum(c => c.Balance);
        SelectedDetail = string.Create(CultureInfo.InvariantCulture,
            $"Cash {Fmt.Sek(cash)} · buying power {Fmt.Sek(a.BuyingPower)}{(a.AvailableForPurchase is { } av ? $" · available {Fmt.Sek(av)}" : string.Empty)}");
        foreach (Position p in o.Portfolio.Positions.Where(p => p.Account == id).OrderByDescending(p => p.Value))
        {
            decimal? gain = p.AcquiredValue is { } cost ? p.Value - cost : null;
            decimal? pct = gain is { } g && p.AcquiredValue is > 0 ? g / p.AcquiredValue.Value : null;
            Holdings.Add(new HoldingRow(
                p.InstrumentName,
                p.Volume == decimal.Truncate(p.Volume) ? Fmt.Count((long)p.Volume) : Fmt.Amount(p.Volume, 4),
                p.Currency == "SEK" ? Fmt.Sek(p.Value) : Fmt.Amount(p.Value) + " " + p.Currency,
                gain is { } gv ? Fmt.ChangeSek(gv) : "–",
                pct is { } pv ? Fmt.ChangePct(pv) : "–",
                Tone.Direction(gain ?? 0m)));
        }
    }

    private void ShowPaperHoldings()
    {
        PaperBook? book = OpenBook();
        if (book is null)
        {
            SelectedDetail = "Opens with the cash in config/paper.json at the first Paper session.";
            return;
        }

        SelectedDetail = string.Create(CultureInfo.InvariantCulture,
            $"Cash {Fmt.Sek(book.Cash)} · started with {Fmt.Sek(book.StartingCash)} · realised {Fmt.ChangeSek(book.RealizedPnl)} · fees {Fmt.Sek(book.FeesPaid)}");
        foreach (PaperPosition p in book.Positions)
        {
            Holdings.Add(new HoldingRow(p.Ticker, Fmt.Count(p.Quantity), Fmt.Sek(p.CostBasis) + " at cost", "–", "–", "flat"));
        }
    }

    private AccountRow PaperRow()
    {
        PaperBook? book = OpenBook();
        decimal value = book is null ? PaperCash() : book.Cash + book.Positions.Sum(p => p.CostBasis);
        return new AccountRow(PaperKey, "Paper account", "Simulated orders on live prices", Fmt.Sek(value), IsPaper: true, CanTradeLive: false, IsLive: false, []);
    }

    /// <summary>A row's key: its place in Avanza's list, so two accounts ending in the same 3 digits stay apart.</summary>
    private static string KeyOf(int index) => string.Create(CultureInfo.InvariantCulture, $"avanza-{index}");

    private static AccountRow Row(string key, AccountSummary a) =>
        new(key, string.IsNullOrWhiteSpace(a.Name) ? a.Type : a.Name, $"{a.Type} · {a.Id.Masked}", Fmt.Sek(a.TotalValue), IsPaper: false,
            a.CanTradeLive, a.IsLiveAccount, a.LiveProblems);

    private PaperBook? OpenBook()
    {
        string dir = Path.Combine(_workspace.StateDir, "paper");
        if (!File.Exists(Path.Combine(dir, PaperBook.FileName)))
        {
            return null;
        }

        try
        {
            return PaperBook.OpenOrCreate(dir, new PaperConfig("?", 1m, new TimeOnly(9, 10)), null, _time, out _);
        }
        catch (Exception ex) when (ex is PaperBookException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private decimal PaperCash()
    {
        try
        {
            return PaperConfig.Load(Path.Combine(_workspace.ConfigDir, PaperConfig.FileName)).Cash;
        }
        catch (Exception ex) when (ex is Trading.Risk.TradingConfigException or IOException or ArgumentException)
        {
            return 0m;
        }
    }
}
