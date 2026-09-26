using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Orders;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// Avanza's two read-only pre-trade checks (Phase 7 step 1) against the fake server and the provisional fixtures:
/// the exact bodies, strict Tier A parsing, and how every failure becomes an outcome the gateway can act on.
/// </summary>
public sealed class PreflightTests
{
    private const string Account = "9990001"; // synthetic, as in the provisional fixtures
    private static readonly string[] NullFields = ["requestId", "orderRequestParameters", "openVolume", "metadata"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PreflightRequest Buy(long volume = 6, decimal limit = 70.850m) =>
        new(new AccountId(Account), new OrderbookId("5240"), "SE0000108656", "SEK", "XSTO", OrderSide.Buy, volume, limit);

    private static async Task<(TestRig Rig, AvanzaPreflight Preflight)> LoggedIn()
    {
        var rig = new TestRig();
        await rig.Connection.Authenticator.LoginAsync(Ct);
        return (rig, rig.Connection.CreatePreflight());
    }

    private static RecordedRequest[] PreflightRequests(TestRig rig) =>
        [.. rig.Server.Requests.Where(r => AvanzaPreflightRoutes.All.Any(route => r.PathAndQuery == route.Path()))];

    [Fact]
    public async Task ValidateThenFee_SendTheGoSdkBodies_AndMapToAnOutcome()
    {
        (TestRig rig, AvanzaPreflight preflight) = await LoggedIn();
        using (rig)
        {
            PreflightOutcome outcome = await preflight.CheckAsync(Buy(), Ct);

            Assert.Equal(BrokerFault.None, outcome.Fault);
            Assert.Null(outcome.Problem);
            Assert.True(outcome.Validation!.AllValid);
            Assert.Equal(
                ["commissionWarning", "employeeValidation", "largeInScaleWarning", "orderValueLimitWarning", "priceRampingWarning", "canadaOddLotWarning"],
                outcome.Validation.Checks.Select(c => c.Name));
            PreliminaryFee fee = outcome.Fee!;
            Assert.Equal(("SEK", 0m, 0m, 425.1m, 425.1m, 1m, 0m), (fee.Currency, fee.Commission, fee.AllFees, fee.TotalSum, fee.TotalSumWithoutFees, fee.FxRate, fee.FxFee));
            Assert.Null(fee.TransactionTax);

            RecordedRequest[] sent = PreflightRequests(rig);
            Assert.Equal([AvanzaPreflightRoutes.Validate.Path(), AvanzaPreflightRoutes.PreliminaryFee.Path()], sent.Select(r => r.PathAndQuery));
            Assert.All(sent, r => Assert.Equal("POST", r.Method));
            Assert.All(sent, r => Assert.Equal(FakeSecrets.SecurityToken, r.Headers["X-SecurityToken"]));

            using JsonDocument validate = JsonDocument.Parse(sent[0].Body!);
            JsonElement v = validate.RootElement;
            Assert.Equal(
                ["isDividendReinvestment", "requestId", "orderRequestParameters", "price", "volume", "openVolume", "accountId", "side", "orderbookId",
                 "validUntil", "metadata", "condition", "isin", "currency", "marketPlace"],
                v.EnumerateObject().Select(p => p.Name));
            Assert.Equal((Account, "BUY", "5240", "2026-09-25", "NORMAL", "SE0000108656", "SEK", "XSTO"),
                (v.GetProperty("accountId").GetString(), v.GetProperty("side").GetString(), v.GetProperty("orderbookId").GetString(),
                 v.GetProperty("validUntil").GetString(), v.GetProperty("condition").GetString(), v.GetProperty("isin").GetString(),
                 v.GetProperty("currency").GetString(), v.GetProperty("marketPlace").GetString()));
            Assert.Equal(JsonValueKind.Number, v.GetProperty("price").ValueKind);
            Assert.Equal(70.85m, v.GetProperty("price").GetDecimal());
            Assert.Equal(6, v.GetProperty("volume").GetInt64());
            Assert.False(v.GetProperty("isDividendReinvestment").GetBoolean());
            Assert.All(NullFields, n => Assert.Equal(JsonValueKind.Null, v.GetProperty(n).ValueKind));

            using JsonDocument fee2 = JsonDocument.Parse(sent[1].Body!);
            Assert.Equal(
                """{"accountId":"9990001","orderbookId":"5240","price":"70.85","volume":"6","side":"BUY"}""",
                fee2.RootElement.GetRawText()); // all strings, "70.85" not "70.850"
        }
    }

    [Fact]
    public async Task AValidFalse_IsNamed_AndTheFeeIsStillAsked()
    {
        (TestRig rig, AvanzaPreflight preflight) = await LoggedIn();
        using (rig)
        {
            rig.Server.On(AvanzaPreflightRoutes.Validate, _ => FakeAvanza.Json(Fixtures.Mutate("preflight-validate.json", n =>
            {
                n["commissionWarning"]!["valid"] = false;
                n["priceRampingWarning"]!["valid"] = false;
            })));
            PreflightOutcome outcome = await preflight.CheckAsync(Buy(), Ct);

            Assert.False(outcome.Validation!.AllValid);
            Assert.Equal(["commissionWarning", "priceRampingWarning"], outcome.Validation.Failures);
            Assert.NotNull(outcome.Fee);
            Assert.Equal(2, PreflightRequests(rig).Length);
        }
    }

    public static TheoryData<string, Action<JsonNode>> ValidateDrift => new()
    {
        { "an unknown check", n => n["newWarning"] = new JsonObject { ["valid"] = true } },
        { "a missing check", n => n.AsObject().Remove("employeeValidation") },
        { "valid as a string", n => n["commissionWarning"]!["valid"] = "true" },
        { "an extra field in a check", n => n["commissionWarning"]!["message"] = "x" },
    };

    [Theory]
    [MemberData(nameof(ValidateDrift))]
    public async Task ValidateDrift_IsASchemaDriftFault_AndTheFeeIsNotAsked(string what, Action<JsonNode> mutate)
    {
        (TestRig rig, AvanzaPreflight preflight) = await LoggedIn();
        using (rig)
        {
            rig.Server.On(AvanzaPreflightRoutes.Validate, _ => FakeAvanza.Json(Fixtures.Mutate("preflight-validate.json", mutate)));
            PreflightOutcome outcome = await preflight.CheckAsync(Buy(), Ct);

            Assert.True(outcome.Fault == BrokerFault.SchemaDrift, what);
            Assert.Null(outcome.Validation);
            Assert.Null(outcome.Fee);
            Assert.StartsWith("validate: ", outcome.Problem, StringComparison.Ordinal);
            Assert.Single(PreflightRequests(rig));
        }
    }

    public static TheoryData<string, Action<JsonNode>> FeeDrift => new()
    {
        { "an unknown field", n => n["brokerage"] = "0" },
        { "a missing total", n => n.AsObject().Remove("totalFees") },
        { "a number instead of a string", n => n["commission"] = 0 },
        { "an unreadable amount", n => n["commission"] = "1.0.6" },
        { "a negative amount", n => n["totalFees"] = "-1" },
        { "a lower-case currency", n => n["orderbookCurrency"] = "sek" },
        { "an unreadable FX fee", n => n["currencyExchangeFee"]!["sum"] = "n/a" },
    };

    [Theory]
    [MemberData(nameof(FeeDrift))]
    public async Task FeeDrift_KeepsTheValidation_AndIsASchemaDriftFault(string what, Action<JsonNode> mutate)
    {
        (TestRig rig, AvanzaPreflight preflight) = await LoggedIn();
        using (rig)
        {
            rig.Server.On(AvanzaPreflightRoutes.PreliminaryFee, _ => FakeAvanza.Json(Fixtures.Mutate("preflight-fee-5240.json", mutate)));
            PreflightOutcome outcome = await preflight.CheckAsync(Buy(), Ct);

            Assert.True(outcome.Fault == BrokerFault.SchemaDrift, what);
            Assert.True(outcome.Validation!.AllValid);
            Assert.Null(outcome.Fee);
            Assert.StartsWith("preliminary fee: ", outcome.Problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SwedishFormattedAmounts_ReadAsDecimals_AndEmptyOptionalsAreNull()
    {
        (TestRig rig, AvanzaPreflight preflight) = await LoggedIn();
        using (rig)
        {
            rig.Server.On(AvanzaPreflightRoutes.PreliminaryFee, _ => FakeAvanza.Json(Fixtures.Mutate("preflight-fee-5240.json", n =>
            {
                n["commission"] = "1,06";
                n["totalFees"] = "1,06";
                n["totalSum"] = "3 426,16";
                n["transactionTax"] = "";
                n["currencyExchangeFee"]!["rate"] = "";
                n["currencyExchangeFee"]!["sum"] = "";
            })));
            PreliminaryFee fee = (await preflight.CheckAsync(Buy(), Ct)).Fee!;

            Assert.Equal((1.06m, 1.06m, 3426.16m), (fee.Commission, fee.TotalFees, fee.TotalSum));
            Assert.Equal((null, null, 0m), (fee.TransactionTax, fee.FxRate, fee.FxFee));
        }
    }

    public static TheoryData<int, BrokerFault> ValidateStatuses => new()
    {
        { 401, BrokerFault.SessionExpired },
        { 403, BrokerFault.SessionExpired },
        { 404, BrokerFault.EndpointGone },
        { 400, BrokerFault.None },
        { 429, BrokerFault.None },
    };

    [Theory]
    [MemberData(nameof(ValidateStatuses))]
    public async Task AFailedValidate_IsAnOutcome_WithTheRightFault(int status, BrokerFault fault)
    {
        (TestRig rig, AvanzaPreflight preflight) = await LoggedIn();
        using (rig)
        {
            // Repeated, because the read pipeline retries 429 and 5xx before giving up.
            rig.Server.On(AvanzaPreflightRoutes.Validate, [.. Enumerable.Repeat<Func<HttpRequestMessage, HttpResponseMessage>>(_ => FakeAvanza.Status((HttpStatusCode)status), 10)]);
            PreflightOutcome outcome = await rig.Run(() => preflight.CheckAsync(Buy(), Ct));

            Assert.Equal(fault, outcome.Fault);
            Assert.Null(outcome.Validation);
            Assert.Null(outcome.Fee);
            Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), outcome.Problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AFeeThatStaysUnavailable_LeavesTheValidation_WithoutAFault()
    {
        (TestRig rig, AvanzaPreflight preflight) = await LoggedIn();
        using (rig)
        {
            rig.Server.On(AvanzaPreflightRoutes.PreliminaryFee, [.. Enumerable.Repeat<Func<HttpRequestMessage, HttpResponseMessage>>(_ => FakeAvanza.Status(HttpStatusCode.ServiceUnavailable), 10)]);
            PreflightOutcome outcome = await rig.Run(() => preflight.CheckAsync(Buy(), Ct));

            Assert.Equal(BrokerFault.None, outcome.Fault);
            Assert.True(outcome.Validation!.AllValid);
            Assert.Null(outcome.Fee);
            Assert.StartsWith("preliminary fee: ", outcome.Problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnIncompleteRequest_IsRefused_BeforeAnythingIsSent()
    {
        (TestRig rig, AvanzaPreflight preflight) = await LoggedIn();
        using (rig)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => preflight.CheckAsync(Buy(volume: 0), Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => preflight.CheckAsync(Buy(limit: 0m), Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => preflight.CheckAsync(Buy() with { Isin = " " }, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => preflight.CheckAsync(Buy() with { Side = (OrderSide)7 }, Ct));
            Assert.Empty(PreflightRequests(rig));
        }
    }

    [Fact]
    public async Task TheProbe_AsksThePreflight_OnlyWhenAsked_AndPlacesNothing()
    {
        // One login per connection (CLAUDE.md), so one rig per probe run.
        using (var plainRig = new TestRig())
        {
            IReadOnlyList<ProbeResult> plain = await plainRig.Connection.Probe.RunAsync("ERIC-B", Ct);
            Assert.DoesNotContain(plain, r => r.Route == "preflight");
            Assert.Empty(PreflightRequests(plainRig));
        }

        using var rig = new TestRig();
        IReadOnlyList<ProbeResult> results = await rig.Connection.Probe.RunAsync("ERIC-B", new ProbeOptions(Preflight: true), Ct);
        ProbeResult row = Assert.Single(results, r => r.Route == "preflight");
        Assert.Equal(ProbeStatus.Ok, row.Status);
        Assert.StartsWith("validate: all 6 valid; fee: commission 0.00, total fees 0.00, total 425.10 SEK (hypothetical BUY 1 ERIC B @ ", row.Detail, StringComparison.Ordinal);
        Assert.EndsWith(" on ***001; nothing placed)", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Account, row.Detail, StringComparison.Ordinal);
        Assert.Equal(2, PreflightRequests(rig).Length);
        Assert.DoesNotContain(rig.Server.Requests, r => AvanzaOrderRoutes.All.Any(route => r.PathAndQuery.StartsWith(route.PathTemplate, StringComparison.Ordinal)));

        using var otherRig = new TestRig();
        IReadOnlyList<ProbeResult> wrongAccount = await otherRig.Connection.Probe.RunAsync("ERIC-B", new ProbeOptions(true, "002"), Ct);
        ProbeResult skipped = Assert.Single(wrongAccount, r => r.Route == "preflight");
        Assert.Equal(ProbeStatus.Skipped, skipped.Status);
        Assert.Contains("end with '002'", skipped.Detail, StringComparison.Ordinal);
    }
}
