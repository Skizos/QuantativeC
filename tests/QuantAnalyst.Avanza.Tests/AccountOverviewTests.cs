using System.Text.Json;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Trading.Accounts;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// The Windows app's account overview (docs/plans/11-app-redesign.md) over the recorded 2026-09-25 answers, through the
/// same query path the app uses: one login, then the accounts, trading accounts and holdings, each with R1's verdict.
/// </summary>
public sealed class AccountOverviewTests : IDisposable
{
    private static readonly string Recording = Path.Combine(AppContext.BaseDirectory, "fixtures", "avanza", "2026-09-25");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-accounts", Guid.NewGuid().ToString("N"));
    private readonly FakeAvanza _server = new();

    public AccountOverviewTests()
    {
        _server.On(AvanzaRoutes.AccountsOverview, _ => FakeAvanza.Json(Body("accounts-overview")));
        _server.On(AvanzaRoutes.TradingAccounts, _ => FakeAvanza.Json(Body("trading-accounts")));
        _server.On(AvanzaRoutes.Positions, _ => FakeAvanza.Json(Body("positions")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static string Body(string route)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(Recording, $"*-{route}.json").Single()));
        return doc.RootElement.GetProperty("response").GetProperty("body").GetRawText();
    }

    private AvanzaCliServices Services(string? liveAccount) => new(
        (options, secrets, prompt, logger, redactor) => AvanzaConnection.CreateForTest(
            new AvanzaOptions { StateDirectory = options.StateDirectory, LoginMethod = options.LoginMethod, RequestsPerSecond = 10, Burst = 20 },
            secrets, logger, redactor, TimeProvider.System, _server, prompt),
        _ => FakeSecrets.Store())
    {
        GetVariable = name => name == AccountAllowlist.Variable ? liveAccount : null,
    };

    [Fact]
    public async Task OneLogin_GivesEveryAccount_ItsHoldings_AndR1sVerdict()
    {
        AccountOverview o = await AccountOverview.LoadAsync(Services("900003193"), Path.Combine(_root, "state"), "totp");

        Assert.Equal(["***019", "***193", "***987"], o.Accounts.Select(a => a.Id.Masked).Order(StringComparer.Ordinal));
        AccountSummary isk = o.Accounts.Single(a => a.Id.Masked == "***193");
        Assert.True(isk.CanTradeLive, string.Join(" ", isk.LiveProblems));
        Assert.True(isk.IsLiveAccount);
        Assert.All(o.Accounts.Where(a => a.Id != isk.Id), a =>
        {
            Assert.False(a.CanTradeLive);
            Assert.False(a.IsLiveAccount);
            Assert.NotEmpty(a.LiveProblems);
        });
        Assert.Contains(o.Accounts.Single(a => a.Id.Masked == "***987").LiveProblems, p => p.Contains("not an ISK", StringComparison.Ordinal));
        Assert.Contains(o.Portfolio.Positions, p => p.Account == isk.Id && p.Volume == 435);
        Assert.True(o.Allowlist.Allowed);

        // Only reads were made after the login.
        Assert.All(_server.Requests.Where(r => r.Method != "GET"), r => Assert.Contains(r.PathAndQuery, new[] { AvanzaRoutes.UserCredentials.Path(), AvanzaRoutes.Totp.Path() }));
    }

    [Fact]
    public async Task WithoutAChoice_NoAccountIsTheLiveOne_ButTheIskCouldBe()
    {
        AccountOverview o = await AccountOverview.LoadAsync(Services(null), Path.Combine(_root, "state"), "totp");
        Assert.DoesNotContain(o.Accounts, a => a.IsLiveAccount);
        Assert.True(o.Accounts.Single(a => a.Id.Masked == "***193").CanTradeLive);
        Assert.False(o.Allowlist.Allowed);
    }
}
