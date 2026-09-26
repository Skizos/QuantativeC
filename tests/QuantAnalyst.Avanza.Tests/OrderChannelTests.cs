using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Orders;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// The Avanza order channel against the in-process fake server and provisional fixtures only (CLAUDE.md: no network,
/// never a live order endpoint). Every case checks that exactly one request was made: orders are never retried.
/// </summary>
public sealed class OrderChannelTests
{
    private const string Account = "9990001"; // synthetic, as in the provisional fixtures
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApprovedOrder Order(OrderSide side = OrderSide.Buy) =>
        new(Guid.CreateVersion7(), new AccountId(Account), new OrderbookId("5240"), side, 10, 70.84m, Today);

    private static async Task<(TestRig Rig, AvanzaOrderChannel Channel)> LoggedIn(bool record = false)
    {
        var rig = new TestRig(record: record);
        await rig.Connection.Authenticator.LoginAsync(Ct);
        return (rig, rig.Connection.CreateOrderChannel());
    }

    private static RecordedRequest[] OrderRequests(TestRig rig) =>
        [.. rig.Server.Requests.Where(r => AvanzaOrderRoutes.All.Any(route => r.PathAndQuery == route.Path()))];

    [Fact]
    public async Task Place_SendsTheProvisionalBodyOnce_AndSuccessIsAccepted()
    {
        (TestRig rig, AvanzaOrderChannel channel) = await LoggedIn();
        using (rig)
        {
            rig.Server.On(AvanzaOrderRoutes.Place, _ => FakeAvanza.Json(Fixtures.Bytes("order-request-success.json")));
            OrderSubmitResult r = await channel.PlaceAsync(Order(), Ct);

            Assert.Equal(SubmitOutcome.Accepted, r.Outcome);
            Assert.Equal(new OrderId("700000002"), r.BrokerOrderId);
            Assert.Equal(BrokerFault.None, r.Fault);

            RecordedRequest sent = Assert.Single(OrderRequests(rig));
            Assert.Equal("POST", sent.Method);
            Assert.Equal(FakeSecrets.SecurityToken, sent.Headers["X-SecurityToken"]);
            using JsonDocument body = JsonDocument.Parse(sent.Body!);
            Assert.Equal(
                ["accountId", "orderbookId", "side", "condition", "price", "validUntil", "volume"],
                body.RootElement.EnumerateObject().Select(p => p.Name));
            JsonElement b = body.RootElement;
            Assert.Equal((Account, "5240", "BUY", "NORMAL", "2026-09-25"),
                (b.GetProperty("accountId").GetString(), b.GetProperty("orderbookId").GetString(), b.GetProperty("side").GetString(),
                 b.GetProperty("condition").GetString(), b.GetProperty("validUntil").GetString()));
            Assert.Equal(70.84m, b.GetProperty("price").GetDecimal());
            Assert.Equal(10, b.GetProperty("volume").GetInt64());
        }
    }

    [Fact]
    public async Task Error_IsRejected_WithAvanzasMessage()
    {
        (TestRig rig, AvanzaOrderChannel channel) = await LoggedIn();
        using (rig)
        {
            rig.Server.On(AvanzaOrderRoutes.Place, _ => FakeAvanza.Json(Fixtures.Bytes("order-request-error.json")));
            OrderSubmitResult r = await channel.PlaceAsync(Order(OrderSide.Sell), Ct);
            Assert.Equal(SubmitOutcome.Rejected, r.Outcome);
            Assert.Contains("not enough buying power", r.Message, StringComparison.Ordinal);
            Assert.Equal("SELL", JsonDocument.Parse(Assert.Single(OrderRequests(rig)).Body!).RootElement.GetProperty("side").GetString());
        }
    }

    public static TheoryData<int, SubmitOutcome, BrokerFault> Statuses => new()
    {
        { 500, SubmitOutcome.Unknown, BrokerFault.None },
        { 502, SubmitOutcome.Unknown, BrokerFault.None },
        { 503, SubmitOutcome.Unknown, BrokerFault.None },
        { 504, SubmitOutcome.Unknown, BrokerFault.None },
        { 408, SubmitOutcome.Unknown, BrokerFault.None },
        { 302, SubmitOutcome.Unknown, BrokerFault.None },
        { 404, SubmitOutcome.Unknown, BrokerFault.EndpointGone },
        { 401, SubmitOutcome.Unknown, BrokerFault.SessionExpired },
        { 403, SubmitOutcome.Unknown, BrokerFault.SessionExpired },
        { 400, SubmitOutcome.Rejected, BrokerFault.None },
        { 429, SubmitOutcome.Rejected, BrokerFault.None },
    };

    [Theory]
    [MemberData(nameof(Statuses))]
    public async Task HttpStatuses_AreClassified_AndNeverRetried(int status, SubmitOutcome outcome, BrokerFault fault)
    {
        (TestRig rig, AvanzaOrderChannel channel) = await LoggedIn();
        using (rig)
        {
            rig.Server.On(AvanzaOrderRoutes.Place, _ =>
            {
                HttpResponseMessage m = FakeAvanza.Status((HttpStatusCode)status);
                m.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1)); // must not invite a retry
                return m;
            });
            OrderSubmitResult r = await channel.PlaceAsync(Order(), Ct);
            Assert.Equal((outcome, fault), (r.Outcome, r.Fault));
            Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Message, StringComparison.Ordinal);
            Assert.Single(OrderRequests(rig));
        }
    }

    public static TheoryData<string, SubmitOutcome, BrokerFault> Bodies => new()
    {
        { "<html>login</html>", SubmitOutcome.Unknown, BrokerFault.SchemaDrift },
        { "", SubmitOutcome.Unknown, BrokerFault.SchemaDrift },
        { """{"orderRequestStatus":"SUCCESS","orderId":"1","newField":1}""", SubmitOutcome.Unknown, BrokerFault.SchemaDrift },
        { """{"message":"no status","orderId":"1"}""", SubmitOutcome.Unknown, BrokerFault.SchemaDrift },
        { """{"orderRequestStatus":"PENDING","orderId":"1"}""", SubmitOutcome.Unknown, BrokerFault.SchemaDrift },
        { """{"orderRequestStatus":"SUCCESS","message":""}""", SubmitOutcome.Unknown, BrokerFault.None },
        { """{"orderRequestStatus":"SUCCESS","orderId":"../../x"}""", SubmitOutcome.Unknown, BrokerFault.None },
        { """{"orderRequestStatus":"ERROR"}""", SubmitOutcome.Rejected, BrokerFault.None },
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public async Task Answers_AreReadStrictly(string body, SubmitOutcome outcome, BrokerFault fault)
    {
        (TestRig rig, AvanzaOrderChannel channel) = await LoggedIn();
        using (rig)
        {
            rig.Server.On(AvanzaOrderRoutes.Place, _ => FakeAvanza.Json(body));
            OrderSubmitResult r = await channel.PlaceAsync(Order(), Ct);
            Assert.Equal((outcome, fault), (r.Outcome, r.Fault));
            Assert.Single(OrderRequests(rig));
        }
    }

    [Fact]
    public async Task NoAnswerWithinTheTimeout_IsUnknown()
    {
        (TestRig rig, AvanzaOrderChannel channel) = await LoggedIn();
        using (rig)
        {
            rig.Server.OnAsync(AvanzaOrderRoutes.Place, async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return FakeAvanza.Json(Fixtures.Bytes("order-request-success.json"));
            });
            OrderSubmitResult r = await rig.Run(() => channel.PlaceAsync(Order(), Ct));
            Assert.Equal(SubmitOutcome.Unknown, r.Outcome);
            Assert.Contains("no answer within 10 s", r.Message, StringComparison.Ordinal);
            Assert.Single(OrderRequests(rig));
        }
    }

    [Fact]
    public async Task ATransportError_OrACallerCancel_IsUnknown()
    {
        (TestRig rig, AvanzaOrderChannel channel) = await LoggedIn();
        using (rig)
        {
            rig.Server.OnAsync(AvanzaOrderRoutes.Place, (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "reset"));
            Assert.Equal(SubmitOutcome.Unknown, (await channel.PlaceAsync(Order(), Ct)).Outcome);

            using var cts = new CancellationTokenSource();
            rig.Server.OnAsync(AvanzaOrderRoutes.Place, async (_, ct) =>
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return FakeAvanza.Json("{}");
            });
            OrderSubmitResult cancelled = await channel.PlaceAsync(Order(), cts.Token);
            Assert.Equal(SubmitOutcome.Unknown, cancelled.Outcome);
            Assert.Contains("cancelled while waiting", cancelled.Message, StringComparison.Ordinal);
            Assert.Equal(2, OrderRequests(rig).Length);
        }
    }

    [Fact]
    public async Task CancelAndModify_SendTheReferenceBodies()
    {
        (TestRig rig, AvanzaOrderChannel channel) = await LoggedIn();
        using (rig)
        {
            rig.Server.On(AvanzaOrderRoutes.Delete, _ => FakeAvanza.Json(Fixtures.Bytes("order-request-success.json")));
            rig.Server.On(AvanzaOrderRoutes.Modify, _ => FakeAvanza.Json(Fixtures.Bytes("order-request-success.json")));
            var id = new OrderId("700000002");

            OrderSubmitResult cancelled = await channel.CancelAsync(new ApprovedCancel(Guid.NewGuid(), new AccountId(Account), id), Ct);
            OrderSubmitResult modified = await channel.ModifyAsync(new ApprovedModify(Guid.NewGuid(), new AccountId(Account), id, 70.5m, 5, Today), Ct);
            Assert.Equal([SubmitOutcome.Accepted, SubmitOutcome.Accepted], new[] { cancelled.Outcome, modified.Outcome });

            RecordedRequest[] sent = OrderRequests(rig);
            Assert.Equal([AvanzaOrderRoutes.Delete.Path(), AvanzaOrderRoutes.Modify.Path()], sent.Select(r => r.PathAndQuery));
            using JsonDocument delete = JsonDocument.Parse(sent[0].Body!);
            Assert.Equal(["accountId", "orderId"], delete.RootElement.EnumerateObject().Select(p => p.Name));
            using JsonDocument modify = JsonDocument.Parse(sent[1].Body!);
            JsonElement m = modify.RootElement;
            Assert.Equal("STANDARD", m.GetProperty("metadata").GetProperty("orderEntryMode").GetString());
            Assert.Equal(JsonValueKind.Null, m.GetProperty("openVolume").ValueKind);
            Assert.Equal(("700000002", 70.5m, 5L, "2026-09-25"),
                (m.GetProperty("orderId").GetString(), m.GetProperty("price").GetDecimal(), m.GetProperty("volume").GetInt64(), m.GetProperty("validUntil").GetString()));
        }
    }

    [Fact]
    public async Task OrderRequests_AreNotRecorded_AndTheAccountIdIsNotLogged()
    {
        (TestRig rig, AvanzaOrderChannel channel) = await LoggedIn(record: true);
        using (rig)
        {
            rig.Server.On(AvanzaOrderRoutes.Place, _ => FakeAvanza.Json(Fixtures.Bytes("order-request-success.json")));
            await channel.PlaceAsync(Order(), Ct);

            string recordings = Path.Combine(rig.Root, "recordings");
            string recorded = Directory.Exists(recordings)
                ? string.Join('\n', Directory.EnumerateFiles(recordings, "*", SearchOption.AllDirectories).Select(File.ReadAllText))
                : string.Empty;
            Assert.DoesNotContain("order.place", recorded, StringComparison.Ordinal);
            Assert.DoesNotContain(Account, recorded, StringComparison.Ordinal);
            Assert.DoesNotContain(Account, ((CapturingLogger)rig.Logger).All, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ThePhase6Gateway_RefusesTheAvanzaChannel()
    {
        (TestRig rig, AvanzaOrderChannel channel) = await LoggedIn();
        using (rig)
        {
            string dir = Path.Combine(rig.Root, "audit");
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero));
            var audit = new AuditLog(dir, time);
            var halts = new HaltController(audit, time);
            var env = new GatewayEnvironment
            {
                Mode = TradingMode.Paper,
                Instruments = new InstrumentCatalog([]),
                Quotes = new NoQuotes(),
                Account = new NoAccount(),
                Calendar = new MarketCalendar([new CalendarYear("XSTO", 2026, new TimeOnly(9, 0), new TimeOnly(17, 30), new TimeOnly(9, 0), new TimeOnly(13, 0), [], [], "test", null)]),
                Universe = Universe.Empty,
                AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal),
                Fees = (_, _) => 0m,
                CourtageVerified = false,
            };

            foreach (TradingMode mode in Enum.GetValues<TradingMode>())
            {
                Assert.Throws<ModeNotAllowedException>(() =>
                    new OrderGateway(channel, env with { Mode = mode }, new PreTradeRiskEngine(RiskLimits.AdrDefaults), new OrderManager(audit, halts, time), halts, audit, time));
            }

            Assert.Empty(OrderRequests(rig));
        }
    }

    private sealed class NoQuotes : IQuoteSource
    {
        public Quote? Latest(OrderbookId id) => null;
    }

    private sealed class NoAccount : IAccountState
    {
        public Task<AccountSnapshot> GetAsync(CancellationToken ct) => throw new InvalidOperationException("not used");
    }
}
