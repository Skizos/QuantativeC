using System.Net;
using System.Text.Json;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Logging;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Avanza.Tests;

public sealed class GatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EndToEnd_ReadsMapThroughThePipeline()
    {
        using var rig = new TestRig();
        await rig.Connection.Authenticator.LoginAsync(Ct);
        IBrokerGateway g = rig.Connection.Gateway;

        Assert.True((await g.GetSessionHealthAsync(Ct)).LoggedIn);
        Assert.Equal(2, (await g.GetAccountsAsync(Ct)).Count);
        Assert.Equal(12345.67m, Assert.Single(await g.GetTradingAccountsAsync(Ct)).AvailableForPurchase);
        Assert.Single((await g.GetPositionsAsync(new AccountId("9990001"), Ct)).Positions);
        Assert.Single(await g.GetOpenOrdersAsync(Ct));
        Assert.Single(await g.GetTransactionsAsync(new DateOnly(2026, 8, 26), new DateOnly(2026, 9, 25), Ct));
        Assert.Equal(2, (await g.SearchStocksAsync("ERIC", 10, Ct)).Count);
        Assert.Equal(70.84m, (await g.GetMarketSnapshotAsync(new OrderbookId("5240"), Ct)).Bid);
        Assert.Equal(2, (await g.GetPriceHistoryAsync(new OrderbookId("5240"), ChartPeriod.OneMonth, ChartResolution.Day, Ct)).Count);

        Assert.All(rig.Server.Requests.Skip(2).Where(r => !r.PathAndQuery.Contains("search", StringComparison.Ordinal) && !r.PathAndQuery.Contains("price-chart", StringComparison.Ordinal)),
            r => Assert.Equal(FakeSecrets.SecurityToken, r.Headers["X-SecurityToken"]));
    }

    [Fact]
    public async Task QueryStrings_UseTheClientsWireFormat()
    {
        using var rig = new TestRig();
        await rig.Connection.Authenticator.LoginAsync(Ct);
        await rig.Connection.Gateway.GetPriceHistoryAsync(new OrderbookId("5240"), ChartPeriod.ThreeMonths, ChartResolution.FiveMinutes, Ct);
        await rig.Connection.Gateway.GetTransactionsAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 25), Ct);
        await rig.Connection.Gateway.SearchStocksAsync("  ERIC B ", 7, Ct);

        RecordedRequest[] r = [.. rig.Server.Requests.Skip(2)];
        Assert.Equal(AvanzaRoutes.PriceChart.Path("5240") + "?timePeriod=three_months&resolution=five_minutes", r[0].PathAndQuery);
        Assert.Equal(AvanzaRoutes.Transactions.Path() + "?from=2026-09-01&to=2026-09-25&includeResult=false", r[1].PathAndQuery);
        using JsonDocument search = JsonDocument.Parse(r[2].Body!);
        Assert.Equal("ERIC B", search.RootElement.GetProperty("query").GetString());
        Assert.Equal("STOCK", search.RootElement.GetProperty("searchFilter").GetProperty("types")[0].GetString());
        Assert.Equal(7, search.RootElement.GetProperty("pagination").GetProperty("size").GetInt32());
    }

    [Theory]
    [InlineData("ERIC-B", "5240")]
    [InlineData("eric b", "5240")]
    [InlineData("ERIC_A", "5239")]
    public async Task TickerResolver_ConfirmsTheTickerOnTheOrderbook(string ticker, string expected)
    {
        using var rig = new TestRig();
        await rig.Connection.Authenticator.LoginAsync(Ct);
        InstrumentTradingParams p = await TickerResolver.ResolveAsync(rig.Connection.Gateway, ticker, Ct);
        Assert.Equal(new OrderbookId(expected), p.OrderbookId);
    }

    [Fact]
    public async Task TickerResolver_UnknownTickerFailsClearly()
    {
        using var rig = new TestRig();
        await rig.Connection.Authenticator.LoginAsync(Ct);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => TickerResolver.ResolveAsync(rig.Connection.Gateway, "VOLV-B", Ct));
        Assert.Contains("--id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deals_AreNotModelledYet()
    {
        using var rig = new TestRig();
        await Assert.ThrowsAsync<EndpointNotModelledException>(() => rig.Connection.Gateway.GetDealsAsync(Ct));
        Assert.Empty(rig.Server.Requests);
    }

    [Fact]
    public void Route_PathRejectsInjection()
    {
        Assert.Throws<ArgumentException>(() => AvanzaRoutes.Orderbook.Path("5240/../../transfer"));
        Assert.Throws<ArgumentException>(() => AvanzaRoutes.Orderbook.Path("5240?x=1"));
        Assert.Equal(AvanzaRoutes.Orderbook.PathTemplate.Replace("{0}", "5240", StringComparison.Ordinal), AvanzaRoutes.Orderbook.Path("5240"));
    }

    [Fact]
    public async Task Probe_ReportsEveryRoute_AndContinuesPastDrift()
    {
        var server = new FakeAvanza();
        server.On(AvanzaRoutes.Positions, _ => FakeAvanza.Json(Fixtures.Mutate("positions.json", n => n["brandNewField"] = 1)));
        using var rig = new TestRig(server);
        IReadOnlyList<ProbeResult> results = await rig.Connection.Probe.RunAsync("ERIC-B", Ct);

        Assert.Equal(
            ["login", "session-info", "accounts-overview", "trading-accounts", "positions", "orders", "deals", "transactions", "search+orderbook", "marketdata", "price-chart"],
            results.Select(r => r.Route));
        ProbeResult positions = results.Single(r => r.Route == "positions");
        Assert.Equal(ProbeStatus.Drift, positions.Status);
        Assert.Contains("$.brandNewField", positions.Detail, StringComparison.Ordinal);
        Assert.Equal(ProbeStatus.Recorded, results.Single(r => r.Route == "deals").Status);
        Assert.All(results.Where(r => r.Route is not ("positions" or "deals")), r => Assert.Equal(ProbeStatus.Ok, r.Status));
    }

    [Fact]
    public async Task Probe_StopsOnSessionExpiry_WithoutReLogin()
    {
        var server = new FakeAvanza();
        server.On(AvanzaRoutes.TradingAccounts, _ => FakeAvanza.Status(HttpStatusCode.Unauthorized));
        using var rig = new TestRig(server);
        IReadOnlyList<ProbeResult> results = await rig.Connection.Probe.RunAsync("ERIC-B", Ct);

        Assert.Equal(ProbeStatus.Stopped, results[^1].Status);
        Assert.Equal("trading-accounts", results[^1].Route);
        Assert.Equal(1, server.CountFor(AvanzaRoutes.UserCredentials));
        Assert.Equal(0, server.CountFor(AvanzaRoutes.Positions));
    }

    [Fact]
    public async Task Probe_LoginFailure_StopsImmediately()
    {
        var server = new FakeAvanza();
        server.On(AvanzaRoutes.UserCredentials, _ => FakeAvanza.Status(HttpStatusCode.Unauthorized));
        using var rig = new TestRig(server);
        ProbeResult only = Assert.Single(await rig.Connection.Probe.RunAsync("ERIC-B", Ct));
        Assert.Equal(ProbeStatus.Stopped, only.Status);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Recording_KeepsStructureButNeverCredentialsTokensOrCookieValues()
    {
        using var rig = new TestRig(record: true);
        await rig.Connection.Probe.RunAsync("ERIC-B", Ct);

        string dir = rig.Connection.RecordingDirectory!;
        string[] files = [.. Directory.GetFiles(dir).Order(StringComparer.Ordinal)];
        Assert.Contains(files, f => f.EndsWith("001-auth.usercredentials.json", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("-positions.json", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("-deals.json", StringComparison.Ordinal));

        using (JsonDocument login = JsonDocument.Parse(File.ReadAllText(files[0])))
        {
            Assert.Equal("<omitted: authentication request>", login.RootElement.GetProperty("request").GetProperty("body").GetString());
        }

        using (JsonDocument totp = JsonDocument.Parse(File.ReadAllText(files[1])))
        {
            JsonElement response = totp.RootElement.GetProperty("response");
            Assert.Contains("X-SecurityToken", response.GetProperty("headerNames").EnumerateArray().Select(e => e.GetString()));
            Assert.Contains("AZACSRF", response.GetProperty("setCookieNames").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal("<redacted>", response.GetProperty("body").GetProperty("authenticationSession").GetString());
            Assert.True(response.GetProperty("body").GetProperty("registrationComplete").GetBoolean()); // structure kept
        }

        string positions = File.ReadAllText(files.Single(f => f.EndsWith("-positions.json", StringComparison.Ordinal)));
        Assert.Contains("SE0000108656", positions, StringComparison.Ordinal); // raw until sanitized

        foreach (string f in files)
        {
            string text = File.ReadAllText(f);
            foreach (string secret in FakeSecrets.All)
            {
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task LogScan_TraceLogsOfAFullRunContainNoSecretsOrFullAccountIds()
    {
        var sink = new StringWriter();
        var redactor = new Redactor();
        var logger = new RedactingLogger(sink, redactor, Microsoft.Extensions.Logging.LogLevel.Trace);
        var server = new FakeAvanza();
        // Failures put exception messages into the logs too.
        server.On(AvanzaRoutes.Orders, _ => FakeAvanza.Status(HttpStatusCode.ServiceUnavailable));
        using var rig = new TestRig(server, record: true, logger: logger);
        using var connection = AvanzaConnection.CreateForTest(rig.Options, rig.Secrets, logger, redactor, rig.Time, server);

        IReadOnlyList<ProbeResult> results = await rig.Run(() => connection.Probe.RunAsync("ERIC-B", Ct));
        foreach (ProbeResult r in results)
        {
            string line = $"{r.Route} {r.Status} {r.Detail}"; // what the CLI prints from the probe
            logger.Log(Microsoft.Extensions.Logging.LogLevel.Information, default, line, null, (s, _) => s);
        }

        string logs = sink.ToString();
        Assert.Contains("login: succeeded; security token from header", logs, StringComparison.Ordinal);
        Assert.Contains("recording positions", logs, StringComparison.Ordinal);
        foreach (string secret in FakeSecrets.All.Append("9990001").Append("9990002"))
        {
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Redactor_MasksRegisteredValuesAndHeaderPatterns()
    {
        var r = new Redactor();
        r.AddSecret(FakeSecrets.Password);
        r.AddAccountId("9990001");
        r.AddSecret("abc"); // too short to be treated as a secret
        string text = r.Redact(
            $"pw={FakeSecrets.Password} acct 9990001 X-SecurityToken: tok123 Cookie: a=b; c=d\nSet-Cookie: AZACSRF=zzz; Path=/\nshort value abc");
        Assert.DoesNotContain(FakeSecrets.Password, text, StringComparison.Ordinal);
        Assert.Contains("acct ***001", text, StringComparison.Ordinal);
        Assert.Contains("X-SecurityToken: ***", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tok123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("a=b", text, StringComparison.Ordinal);
        Assert.DoesNotContain("zzz", text, StringComparison.Ordinal);
        Assert.EndsWith("abc", text, StringComparison.Ordinal);
    }
}
