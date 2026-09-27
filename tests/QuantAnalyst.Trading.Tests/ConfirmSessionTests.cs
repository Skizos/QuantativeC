using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Core.Orders;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Confirm;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Live;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Reconciliation;
using QuantAnalyst.Trading.Risk;
using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Trading.Tests;

/// <summary>The broker's open orders and deals as the test wants them, or a failure.</summary>
internal sealed class FakeBrokerState(TimeProvider time) : IBrokerStateSource
{
    public Exception? Failure { get; set; }

    public int Reads { get; private set; }

    public Task<BrokerSnapshot> GetAsync(CancellationToken ct)
    {
        Reads++;
        return Failure is null ? Task.FromResult(new BrokerSnapshot([], [], time.GetUtcNow())) : Task.FromException<BrokerSnapshot>(Failure);
    }
}

/// <summary>
/// Phase 7 step 5: the Confirm session loop on a simulated channel (the Confirm spy runs the real CLI over the fake
/// server). It re-plans before every card, shows each instrument at most once a day, halts when a live read fails, and
/// cancels its working orders when stopped.
/// </summary>
public sealed class ConfirmSessionTests : IDisposable
{
    private static readonly AccountId Isk = AccountStateTests.IskId;
    private static readonly OrderbookId Eric = RiskEngineTests.Eric;
    private static readonly OrderbookId Test = new("1001");

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now); // Monday 10:00 Stockholm: after the 09:10 decision
    private readonly FakeSimulatedChannel _channel = new();
    private readonly SettableQuotes _quotes = new();
    private readonly ScriptedConfirmation _confirm = new();
    private readonly CountingAccount _account = new(new AccountSnapshot(Isk, 100_000m, 50_000m, new Dictionary<OrderbookId, long>(), new Dictionary<OrderbookId, decimal>(), 100_000m));
    private readonly FakeBrokerState _broker;
    private readonly AuditLog _audit;
    private readonly HaltController _halts;
    private readonly OrderGateway _gateway;
    private readonly KillSwitch _kill;
    private readonly StringWriter _output = new();
    private readonly ConfirmSession _session;
    private int _plans;

    public ConfirmSessionTests()
    {
        _broker = new FakeBrokerState(_time);
        _audit = new AuditLog(_dir.File("audit"), _time);
        _halts = new HaltController(_audit, _time);
        var oms = new OrderManager(_audit, _halts, _time);
        MarketCalendar calendar = OrderGatewayTests.Calendar(new DateOnly(2026, 9, 1));
        InstrumentSpec eric = OrderCardTests.Spec;
        InstrumentSpec test = eric with { OrderbookId = Test, Ticker = "TEST B", Name = "Test B" };
        var env = new GatewayEnvironment
        {
            Mode = TradingMode.Confirm,
            Instruments = new InstrumentCatalog([eric, test]),
            Quotes = _quotes,
            Account = _account,
            Calendar = calendar,
            Universe = new Universe([new UniverseEntry(Eric, "ERIC B", "Ericsson B"), new UniverseEntry(Test, "TEST B", "Test B")]),
            AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { Isk.Value },
            Fees = (_, _) => 0m,
            CourtageVerified = true,
            Preflight = new FakePreflight(),
            Confirmation = _confirm,
        };
        _gateway = new OrderGateway(_channel, env, new PreTradeRiskEngine(RiskLimits.AdrDefaults), oms, _halts, _audit, _time);
        _kill = new KillSwitch(_gateway, _halts, _audit, _time, _dir.File("KILL"), _dir.File("state"), _account, 0.02m, watch: false);
        _session = new ConfirmSession(_gateway, _account, _kill, new Reconciler(oms, _halts, _audit, _time, Isk), _broker, _halts,
            new TradingSchedule(calendar, RiskLimits.AdrDefaults, new TimeOnly(9, 10)), _audit, _time, Plan, _output);
        Quotes();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _kill.Dispose();
        _gateway.Dispose();
        _dir.Dispose();
    }

    private void Quotes()
    {
        _quotes.Set(Eric, _time.GetUtcNow(), 100.4m, 500, 100.6m, 700, 100.5m, 1_000_000);
        _quotes.Set(Test, _time.GetUtcNow(), 100.4m, 500, 100.6m, 700, 100.5m, 1_000_000);
    }

    // The strategy always wants both names; the session proposes each once.
    private Task<PlanResult> Plan(CancellationToken ct)
    {
        _plans++;
        DateTimeOffset now = _time.GetUtcNow();
        return Task.FromResult(new PlanResult(
            [
                new OrderIntent(Eric, "ERIC B", OrderSide.Buy, 10, 100.37m, "test", 100.5m, now, "test"),
                new OrderIntent(Test, "TEST B", OrderSide.Buy, 10, 100.37m, "test", 100.5m, now, "test"),
            ],
            ["ERIC B: buy 10", "TEST B: buy 10"]));
    }

    private async Task Step(double afterSeconds = ConfirmSessionPace)
    {
        _time.Advance(TimeSpan.FromSeconds(afterSeconds));
        Quotes();
        await _session.StepAsync(Ct);
    }

    private const double ConfirmSessionPace = 13;

    [Fact]
    public async Task ItRePlansBeforeEveryCard_AndShowsEachInstrumentOnce()
    {
        _confirm.Answer = (card, _) => Task.FromResult(card.Ticker == "ERIC B"
            ? new ConfirmationAnswer(ConfirmationVerdict.Declined, "another ticker", TimeSpan.FromSeconds(3))
            : ScriptedConfirmation.Yes);

        await Step(0);
        await Step();
        await Step();
        await Step();

        Assert.Equal(["ERIC B", "TEST B"], _confirm.Cards.Select(c => c.Ticker));
        ApprovedOrder sent = Assert.Single(_channel.Placed);
        Assert.Equal(Test, sent.OrderbookId);
        Assert.Equal(3, _plans); // a plan before each card, and one that finds nothing left
        string text = _output.ToString();
        Assert.Contains("decision: 2 order(s), one card each.", text, StringComparison.Ordinal);
        Assert.Contains("Buy 10 ERIC B: Skipped (another ticker)", text, StringComparison.Ordinal);
        Assert.Contains("Buy 10 TEST B: Accepted", text, StringComparison.Ordinal);
        Assert.Contains("nothing more to trade today.", text, StringComparison.Ordinal);
        Assert.Equal([Eric, Test], _session.Handled.OrderBy(id => id.Value, StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public async Task CardsAreAtLeast13SecondsApart()
    {
        await Step(0);
        await Step(5);
        Assert.Single(_confirm.Cards);

        await Step(8);
        Assert.Equal(2, _confirm.Cards.Count);
    }

    [Theory]
    [InlineData("deals not modelled", HaltReason.SchemaDrift)]
    [InlineData("session expired", HaltReason.Session)]
    public async Task AReconciliationThatCannotRead_Halts_AndNoCardFollows(string failure, HaltReason expected)
    {
        _broker.Failure = failure == "deals not modelled"
            ? new EndpointNotModelledException("deals", "1 deal(s) returned, but the deal fields have not been recorded yet")
            : new SessionExpiredException("orders", 401);

        await Step(0);

        Assert.True(_halts.IsActive(expected));
        Assert.Empty(_confirm.Cards);
        string text = _output.ToString();
        Assert.Contains($"RECONCILIATION FAILED ({expected})", text, StringComparison.Ordinal);
        Assert.Contains("no more cards today: trading is halted", text, StringComparison.Ordinal);
        Assert.True(_account.Invalidations >= 1); // the next account read is fresh either way
    }

    [Fact]
    public async Task APassingReadFailure_IsAuditedButDoesNotHalt()
    {
        _broker.Failure = new BrokerUnavailableException("orders", "timed out");

        await Step(0);

        Assert.False(_halts.IsHalted);
        Assert.Single(_confirm.Cards); // the day goes on; the next reconciliation tries again
    }

    [Fact]
    public async Task StoppingTheSession_CancelsItsWorkingOrders()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _confirm.Answer = (_, _) =>
        {
            if (_confirm.Cards.Count == 2)
            {
                stop.Cancel(); // stop while the second card waits
            }

            return Task.FromResult(ScriptedConfirmation.Yes);
        };

        Task<ConfirmSessionSummary> run = _session.RunAsync(RiskEngineTests.Now.AddHours(1), stop.Token);
        for (int i = 0; i < 200 && !run.IsCompleted; i++)
        {
            await Task.Delay(5, Ct);
            _time.Advance(TimeSpan.FromSeconds(1));
            Quotes();
        }

        ConfirmSessionSummary summary = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(1, summary.Accepted);
        Assert.Equal(_channel.Placed[0].ClientOrderId, Assert.Single(_channel.Cancelled).ClientOrderId);
        Assert.Contains("confirm-skip", Directory.GetFiles(_dir.File("audit")).SelectMany(AuditLog.Read).Select(e => e.GetProperty("kind").GetString()));
    }
}
