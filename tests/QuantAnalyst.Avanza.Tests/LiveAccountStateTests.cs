using System.Text.Json;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Trading.Accounts;
using QuantAnalyst.Trading.Model;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// Phase 7 step 2 on the owner's sanitized 2026-09-25 recording, through the real read pipeline: R1 picks the ISK and
/// refuses the other two accounts, and the live account state reads the ISK's cash, position and value.
/// </summary>
public sealed class LiveAccountStateTests
{
    private static readonly string Recording = Path.Combine(AppContext.BaseDirectory, "fixtures", "avanza", "2026-09-25");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Body(string route)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(Recording, $"*-{route}.json").Single()));
        return doc.RootElement.GetProperty("response").GetProperty("body").GetRawText();
    }

    [Fact]
    public async Task TheRecordedAccounts_ResolveR1_AndGiveTheLiveAccountState()
    {
        using var rig = new TestRig();
        rig.Server.On(AvanzaRoutes.TradingAccounts, _ => FakeAvanza.Json(Body("trading-accounts")), _ => FakeAvanza.Json(Body("trading-accounts")));
        rig.Server.On(AvanzaRoutes.Positions, _ => FakeAvanza.Json(Body("positions")));
        await rig.Connection.Authenticator.LoginAsync(Ct);
        IBrokerGateway gateway = rig.Connection.Gateway;

        IReadOnlyList<TradingAccount> accounts = await gateway.GetTradingAccountsAsync(Ct);
        AllowlistResult isk = AccountAllowlist.Resolve("900003193", accounts);
        Assert.True(isk.Allowed, isk.Describe());
        Assert.Equal("Live trading account (R1, AVANZA__ALLOWEDACCOUNTIDS): ***193, ISK, tradable, not managed, no credit: OK.", isk.Describe());

        string depot = AccountAllowlist.Resolve("900001987", accounts).Describe();
        Assert.Contains("account ***987 is a AKTIEFONDKONTO, not an ISK", depot, StringComparison.Ordinal);
        string savings = AccountAllowlist.Resolve("900002019", accounts).Describe();
        Assert.Contains("account ***019 is not tradable at Avanza.", savings, StringComparison.Ordinal);
        Assert.Contains("is a SPARKONTO, not an ISK", savings, StringComparison.Ordinal);

        var state = new GatewayAccountState(gateway, isk.Account!.Id, quotes: null, rig.Time, Path.Combine(rig.Root, "state"));
        AccountSnapshot s = await state.GetAsync(Ct);

        var fastator = new OrderbookId("346549");
        Assert.Equal(3.45m, s.AvailableCash); // availableForPurchase; below the credit-free 9.54
        Assert.Equal(435, Assert.Single(s.Positions).Value);
        Assert.Equal(461.334m, s.PositionValues[fastator]); // the broker's value: no composed quote in this test
        Assert.Equal(461.334m + 5.2959m, s.AccountValue); // plus the ISK's cash position
        Assert.Equal(s.AccountValue, s.StartOfDayValue);
        Assert.Equal(2, rig.Server.CountFor(AvanzaRoutes.TradingAccounts));
        Assert.Equal(1, rig.Server.CountFor(AvanzaRoutes.Positions));
    }
}
