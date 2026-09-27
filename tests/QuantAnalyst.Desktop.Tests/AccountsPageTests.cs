using System.Reflection;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.ViewModels;
using QuantAnalyst.Trading.Accounts;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>
/// The Accounts page (docs/plans/11-app-redesign.md step 4) with a fake account source and a fake user environment:
/// account numbers only ever appear masked, and the live-trading account (R1) is chosen only with its last 3 digits.
/// </summary>
public sealed class AccountsPageTests : IDisposable
{
    private const string Isk = "900003193";
    private const string Kf = "700001987";
    private const string Managed = "500002019";
    private const string TwinIsk = "800004193"; // ends in the same 3 digits as the ISK

    private readonly TempWorkspace _ws = new();
    private readonly FakeEnvironment _env = new();
    private readonly FakeAccounts _source;

    public AccountsPageTests() => _source = new FakeAccounts(_env);

    public void Dispose() => _ws.Dispose();

    private ShellViewModel Shell() => new(_ws.Workspace, new QaEngine(new ImmediateDispatcher()), _ws.Time, _source, _env);

    [Fact]
    public async Task BeforeLoading_OnlyThePaperAccountIsShown_AndNothingCanBeChosen()
    {
        AccountsViewModel page = Shell().Accounts;
        await page.RefreshAsync();

        AccountRow paper = Assert.Single(page.Accounts);
        Assert.True(paper.IsPaper);
        Assert.Same(paper, page.Selected);
        Assert.Equal("Simulated", paper.Badge);
        Assert.Equal("5 000,00 kr", paper.Value); // the starting cash in config/paper.json
        Assert.StartsWith("Opens with the cash", page.SelectedDetail, StringComparison.Ordinal);
        Assert.StartsWith("Not loaded yet", page.LoadedAt, StringComparison.Ordinal);
        Assert.Equal(("No account may trade live yet", "neutral"), (page.LiveAccountText, page.LiveAccountTone));
        Assert.False(page.CanChooseSelected);
        Assert.False(page.UseForLiveCommand.CanExecute(null));
        Assert.False(page.StopLiveCommand.CanExecute(null));
        Assert.Equal(0, _source.Loads);
    }

    [Fact]
    public async Task Load_MakesOneLoginWithTheChosenMethod_AndShowsEveryAccountMasked_WithItsHoldings()
    {
        ShellViewModel shell = Shell();
        shell.LoginMethod = "totp";
        AccountsViewModel page = shell.Accounts;
        await page.RefreshAsync();
        await page.LoadCommand.ExecuteAsync();

        Assert.Equal(["totp"], _source.Logins);
        Assert.False(page.MessageIsError, page.Message);
        Assert.Equal("Loaded 3 account(s).", page.Message);
        Assert.StartsWith("Read from Avanza at 12:00", page.LoadedAt, StringComparison.Ordinal);
        Assert.Equal(["Paper account", "Aktiehandel ISK", "Kapitalförsäkring", "Förvaltat ISK"], page.Accounts.Select(a => a.Title));
        Assert.Equal(["Simulated", "Can trade live", "Can't trade live", "Can't trade live"], page.Accounts.Select(a => a.Badge));
        Assert.Equal("ISK · ***193", page.Accounts[1].Subtitle);

        page.Selected = page.Accounts[1];
        Assert.Equal("5 012,40 kr", page.SelectedValue);
        Assert.Equal("Cash 1 200,00 kr · buying power 1 200,00 kr · available 1 200,00 kr", page.SelectedDetail);
        HoldingRow eric = Assert.Single(page.Holdings);
        Assert.Equal(("Ericsson B", "10", "800,00 kr"), (eric.Name, eric.Volume, eric.Value));
        Assert.Equal(("+50,00 kr", "+6,67 %", "up"), (eric.Gain, eric.GainPct, eric.Direction));
        Assert.Empty(page.SelectedProblems);
        Assert.True(page.CanChooseSelected);

        page.Selected = page.Accounts[2];
        Assert.Contains(page.SelectedProblems, p => p.Contains("not an ISK", StringComparison.Ordinal));
        Assert.False(page.CanChooseSelected);
        Assert.False(page.UseForLiveCommand.CanExecute(null));

        NoFullAccountNumberIn(page);
    }

    [Fact]
    public async Task UseForLive_NeedsTheLastThreeDigits_ThenSetsTheOneVariable_AndStopClearsIt()
    {
        ShellViewModel shell = Shell();
        AccountsViewModel page = shell.Accounts;
        await page.RefreshAsync();
        await page.LoadCommand.ExecuteAsync();
        page.Selected = page.Accounts[1];
        Assert.True(page.UseForLiveCommand.CanExecute(null));

        page.ConfirmDigits = "391";
        page.UseForLiveCommand.Execute(null);
        Assert.True(page.MessageIsError);
        Assert.Equal("Type the last 3 digits of the account (***193) to confirm. Nothing was changed.", page.Message);
        Assert.False(_env.Values.ContainsKey(AccountAllowlist.Variable));

        page.ConfirmDigits = " 193 ";
        page.UseForLiveCommand.Execute(null);
        Assert.False(page.MessageIsError, page.Message);
        Assert.Equal(Isk, _env.Read(AccountAllowlist.Variable)); // the full number, in the variable only
        Assert.Equal(string.Empty, page.ConfirmDigits);
        Assert.StartsWith("***193 is now the one account live trading may use", page.Message, StringComparison.Ordinal);
        Assert.Equal(("***193 · ISK", "live"), (page.LiveAccountText, page.LiveAccountTone));
        Assert.True(page.Selected!.IsLive);
        Assert.Equal("Live trading", page.Selected.Badge);
        Assert.False(page.CanChooseSelected);
        Assert.False(page.UseForLiveCommand.CanExecute(null));
        Assert.Equal(1, _source.Loads); // no new login for the choice
        NoFullAccountNumberIn(page);

        // The Overview shows the same choice, masked.
        await shell.Status.RefreshAsync();
        Assert.Equal(("***193", "live"), (shell.Status.LiveAccountText, shell.Status.LiveAccountTone));

        Assert.True(page.StopLiveCommand.CanExecute(null));
        page.StopLiveCommand.Execute(null);
        Assert.Null(_env.Read(AccountAllowlist.Variable));
        Assert.Equal(("No account may trade live yet", "neutral"), (page.LiveAccountText, page.LiveAccountTone));
        Assert.False(page.Accounts[1].IsLive);
        Assert.False(page.StopLiveCommand.CanExecute(null));
        Assert.StartsWith("No account may trade live now", page.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChoiceMadeInTheTerminal_IsShown_AndOneThatNamesTwoAccountsIsFlagged()
    {
        _env.Write(AccountAllowlist.Variable, Isk);
        AccountsViewModel page = Shell().Accounts;
        await page.RefreshAsync();
        Assert.Equal(("***193 (load your accounts to check it)", "live"), (page.LiveAccountText, page.LiveAccountTone));

        await page.LoadCommand.ExecuteAsync();
        AccountRow live = Assert.Single(page.Accounts, a => a.IsLive);
        Assert.Equal("Aktiehandel ISK", live.Title);
        Assert.Same(live, page.Selected); // the live account is shown first after a load

        _env.Write(AccountAllowlist.Variable, Isk + "," + Kf);
        await page.RefreshAsync();
        Assert.Equal("FAIL", page.LiveAccountTone);
        Assert.StartsWith("The setting is not usable", page.LiveAccountText, StringComparison.Ordinal);
        NoFullAccountNumberIn(page);
    }

    [Fact]
    public async Task TwoAccountsEndingInTheSameDigits_StayApart()
    {
        _source.WithTwin = true;
        AccountsViewModel page = Shell().Accounts;
        await page.RefreshAsync();
        await page.LoadCommand.ExecuteAsync();

        AccountRow twin = page.Accounts.Single(a => a.Title == "Sparkonto ISK");
        Assert.Equal("ISK · ***193", twin.Subtitle);
        page.Selected = twin;
        Assert.Equal("7 000,00 kr", page.SelectedValue);
        Assert.Empty(page.Holdings); // the other ***193 holds the Ericsson shares
        page.ConfirmDigits = "193";
        page.UseForLiveCommand.Execute(null);
        Assert.Equal(TwinIsk, _env.Read(AccountAllowlist.Variable));
        Assert.Equal(["Sparkonto ISK"], page.Accounts.Where(a => a.IsLive).Select(a => a.Title));
    }

    [Fact]
    public async Task AFailedLoad_IsSaidPlainly_AndKeepsThePaperAccount()
    {
        _source.Failure = new LoginFailedException("BankID was cancelled");
        AccountsViewModel page = Shell().Accounts;
        await page.RefreshAsync();
        await page.LoadCommand.ExecuteAsync();

        Assert.True(page.MessageIsError);
        Assert.Equal("Your accounts could not be loaded: Login failed: BankID was cancelled. Not retrying (one attempt per trigger).", page.Message);
        Assert.True(Assert.Single(page.Accounts).IsPaper);
        Assert.StartsWith("Not loaded yet", page.LoadedAt, StringComparison.Ordinal);
    }

    /// <summary>No full account number in anything the page shows: its texts, its rows, its holdings, its problems.</summary>
    private static void NoFullAccountNumberIn(AccountsViewModel page)
    {
        var shown = new List<string>();
        shown.AddRange(Texts(page));
        foreach (AccountRow row in page.Accounts)
        {
            shown.AddRange(Texts(row));
            shown.AddRange(row.Problems);
        }

        foreach (HoldingRow holding in page.Holdings)
        {
            shown.AddRange(Texts(holding));
        }

        shown.AddRange(page.SelectedProblems);
        foreach (string full in new[] { Isk, Kf, Managed, TwinIsk })
        {
            Assert.DoesNotContain(shown, s => s.Contains(full, StringComparison.Ordinal));
            Assert.DoesNotContain(shown, s => s.Contains(full[..^3], StringComparison.Ordinal)); // not even the unmasked prefix
        }
    }

    private static IEnumerable<string> Texts(object o) =>
        o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
            .Select(p => (string?)p.GetValue(o))
            .OfType<string>();

    /// <summary>Three accounts like the recorded 2026-09-25 answers (an ISK that may trade, a KF, a managed ISK).</summary>
    private sealed class FakeAccounts(IUserEnvironment env) : IAccountSource
    {
        public List<string> Logins { get; } = [];

        public int Loads => Logins.Count;

        public Exception? Failure { get; set; }

        public bool WithTwin { get; set; }

        public Task<AccountOverview> LoadAsync(string login)
        {
            Logins.Add(login);
            if (Failure is { } failure)
            {
                return Task.FromException<AccountOverview>(failure);
            }

            var isk = new AccountId(Isk);
            var kf = new AccountId(Kf);
            var managed = new AccountId(Managed);
            var twin = new AccountId(TwinIsk);
            var accounts = new List<Account>
            {
                new(isk, "ISK", "Aktiehandel ISK", "ACTIVE", "SEK", 1200m, 5012.40m, 1200m),
                new(kf, "KF", "Kapitalförsäkring", "ACTIVE", "SEK", 0m, 25000m, 0m),
                new(managed, "ISK", "Förvaltat ISK", "ACTIVE", "SEK", 0m, 10000m, 0m),
            };
            var trading = new List<TradingAccount>
            {
                new(isk, "Aktiehandel ISK", AccountAllowlist.IskType, 1200m, IsTradable: true, HasCredit: false, IsDiscretionary: false, []),
                new(kf, "Kapitalförsäkring", "KAPITALFORSAKRING", 0m, IsTradable: true, HasCredit: false, IsDiscretionary: false, []),
                new(managed, "Förvaltat ISK", AccountAllowlist.IskType, 0m, IsTradable: true, HasCredit: false, IsDiscretionary: true, []),
            };
            if (WithTwin)
            {
                accounts.Add(new(twin, "ISK", "Sparkonto ISK", "ACTIVE", "SEK", 7000m, 7000m, 7000m));
                trading.Add(new(twin, "Sparkonto ISK", AccountAllowlist.IskType, 7000m, IsTradable: true, HasCredit: false, IsDiscretionary: false, []));
            }

            var portfolio = new PortfolioSnapshot(
                [new Position(isk, new OrderbookId("5240"), "Ericsson B", "SE0000108656", "STOCK", "SEK", 10m, 800m, 75m, 750m, 80m)],
                [new CashPosition(isk, 1200m, "SEK")],
                TempWorkspace.Saturday);
            AllowlistResult allowlist = AccountAllowlist.FromEnvironment(trading, env.Read);
            return Task.FromResult(new AccountOverview(AccountOverview.Summaries(accounts, trading, allowlist), portfolio, allowlist));
        }
    }
}
