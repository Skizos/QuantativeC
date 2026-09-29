using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Trading.Accounts;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Confirm;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Reports;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

/// <summary>Avanza's pre-trade checks as the test wants them; records every request.</summary>
internal sealed class FakePreflight : IBrokerPreflight
{
    public List<PreflightRequest> Requests { get; } = [];

    public Queue<PreflightOutcome> Answers { get; } = new();

    public PreflightOutcome Default { get; set; } = Valid(0m);

    public static PreflightOutcome Valid(decimal fee) => new(
        new PreflightValidation([new PreflightCheck("commissionWarning", true), new PreflightCheck("priceRampingWarning", true)]),
        new PreliminaryFee("SEK", fee, 0m, fee, 1_003m + fee, 1_003m, null, 1m, 0m),
        BrokerFault.None,
        null);

    public static PreflightOutcome Invalid(string check) => new(
        new PreflightValidation([new PreflightCheck("commissionWarning", true), new PreflightCheck(check, false)]), null, BrokerFault.None, null);

    public Task<PreflightOutcome> CheckAsync(PreflightRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Answers.TryDequeue(out PreflightOutcome? answer) ? answer : Default);
    }
}

/// <summary>The Confirm mode gate with answers the test scripts; records every card and every line told.</summary>
internal sealed class ScriptedConfirmation : IOrderConfirmation
{
    public static readonly ConfirmationAnswer Yes = new(ConfirmationVerdict.Confirmed, "confirmed", TimeSpan.FromSeconds(5));

    public List<OrderCard> Cards { get; } = [];

    public List<string> Told { get; } = [];

    public Func<OrderCard, CancellationToken, Task<ConfirmationAnswer>> Answer { get; set; } = (_, _) => Task.FromResult(Yes);

    public Task<ConfirmationAnswer> ConfirmAsync(OrderCard card, CancellationToken ct)
    {
        Cards.Add(card);
        return Answer(card, ct);
    }

    public void Tell(string line) => Told.Add(line);
}

/// <summary>An account state that counts reads and invalidations, and can fail.</summary>
internal sealed class CountingAccount(AccountSnapshot snapshot) : IAccountState
{
    public AccountSnapshot Snapshot { get; set; } = snapshot;

    public Exception? Failure { get; set; }

    public int Reads { get; private set; }

    public int Invalidations { get; private set; }

    public Task<AccountSnapshot> GetAsync(CancellationToken ct)
    {
        Reads++;
        return Failure is null ? Task.FromResult(Snapshot) : Task.FromException<AccountSnapshot>(Failure);
    }

    public void Invalidate() => Invalidations++;
}

/// <summary>
/// Phase 7 step 3: the gateway's Confirm flow on a simulated channel (a rehearsal: the real channel needs the startup
/// checks of step 4). Our checks first, then Avanza's; a card only for an order that passes all of them; nothing is
/// sent without a confirmed answer AND a passing re-check on fresh data; a skip is audited and is not a reject.
/// </summary>
public sealed class ConfirmGatewayTests : IDisposable
{
    private static readonly AccountId Isk = new("9990001");
    private static readonly OrderbookId Eric = RiskEngineTests.Eric;

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now); // Monday 10:00 Stockholm
    private readonly FakeSimulatedChannel _channel = new();
    private readonly SettableQuotes _quotes = new();
    private readonly FakePreflight _preflight = new();
    private readonly ScriptedConfirmation _confirm = new();
    private readonly CountingAccount _account = new(new AccountSnapshot(
        Isk, 100_000m, 50_000m, new Dictionary<OrderbookId, long> { [Eric] = 100 }, new Dictionary<OrderbookId, decimal> { [Eric] = 10_000m }, 100_000m));

    private readonly AuditLog _audit;
    private readonly HaltController _halts;
    private readonly OrderManager _oms;
    private readonly List<int> _brokerRejects = [];
    private InstrumentSpec _spec = OrderCardTests.Spec;
    private OrderGateway? _gateway;

    public ConfirmGatewayTests()
    {
        _audit = new AuditLog(_dir.Path, _time);
        _halts = new HaltController(_audit, _time);
        _oms = new OrderManager(_audit, _halts, _time);
        Quote(100.5m);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrderGateway Gateway => _gateway ??= New(_channel, TradingMode.Confirm);

    public void Dispose()
    {
        _gateway?.Dispose();
        _dir.Dispose();
    }

    private GatewayEnvironment Env(TradingMode mode) => new()
    {
        Mode = mode,
        Instruments = new InstrumentCatalog([_spec]),
        Quotes = _quotes,
        Account = _account,
        Calendar = OrderGatewayTests.Calendar(new DateOnly(2026, 9, 1)),
        Universe = new Universe([new UniverseEntry(Eric, "ERIC B", "Ericsson B")]),
        AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { Isk.Value },
        Fees = (_, _) => 0m,
        CourtageVerified = true,
        Preflight = _preflight,
        Confirmation = _confirm,
    };

    private OrderGateway New(IBrokerOrderChannel channel, TradingMode mode, Func<GatewayEnvironment, GatewayEnvironment>? change = null)
    {
        GatewayEnvironment env = Env(mode);
        var gateway = new OrderGateway(channel, change is null ? env : change(env), new PreTradeRiskEngine(RiskLimits.AdrDefaults), _oms, _halts, _audit, _time);
        gateway.BrokerRejected += (n, _) => _brokerRejects.Add(n);
        return gateway;
    }

    private void Quote(decimal last) => _quotes.Set(Eric, _time.GetUtcNow(), last - 0.1m, 500, last + 0.1m, 700, last, 1_000_000);

    private static OrderIntent Intent(long qty = 10) =>
        new(Eric, "ERIC B", OrderSide.Buy, qty, 100.37m, "ma-cross: target 20 %", 100.37m, RiskEngineTests.Now, "test");

    private Task<SubmitResult> Submit(long qty = 10, CancellationToken? ct = null) => Gateway.SubmitAsync(Intent(qty), ct ?? Ct);

    private string[] AuditKinds() =>
        [.. Directory.GetFiles(_dir.Path).Order().SelectMany(AuditLog.Read).Select(r => r.GetProperty("kind").GetString()!)];

    private EodReport Report() => EodReport.Build(_dir.Path, new DateOnly(2026, 9, 28), _time);

    [Fact]
    public async Task AConfirmedCard_IsReChecked_ThenSentExactlyOnce()
    {
        SubmitResult r = await Submit();

        Assert.Equal(SubmitStatus.Accepted, r.Status);
        ApprovedOrder sent = Assert.Single(_channel.Placed);
        Assert.Equal((Isk, 10L, 100.3m), (sent.Account, sent.Volume, sent.LimitPrice));

        OrderCard card = Assert.Single(_confirm.Cards);
        Assert.Equal("ERIC-B JA", card.Answer);
        Assert.Equal(21, card.Risk.Checks.Count(c => c.Passed));
        Assert.Equal(0m, card.Fees.AvanzaFees);

        // Avanza is asked twice: before the card, and again after JA.
        Assert.Equal(2, _preflight.Requests.Count);
        Assert.All(_preflight.Requests, q => Assert.Equal(new PreflightRequest(Isk, Eric, "SE0000108656", "SEK", "XSTO", OrderSide.Buy, 10, 100.3m), q));

        Assert.StartsWith("Re-checked: 21 of 21 pass (quote 0 s old). Sending.", _confirm.Told[0], StringComparison.Ordinal);
        Assert.Equal("Sent: order SIM-1, Working.", _confirm.Told[1]);
        Assert.Equal(
            ["gateway-start", "intent", "prepared", "preflight", "risk", "confirm-card", "confirm-answer", "preflight", "recheck", "gate", "oms-new", "submit", "oms-state", "submit-result", "oms-state"],
            AuditKinds());
        Assert.Equal((2, 2), (_account.Reads, _account.Invalidations)); // fresh before the re-check, and after the send
        Assert.True(AuditLog.Verify(_dir.Path).Valid);
    }

    [Fact]
    public async Task AUsShare_IsRefusedBeforeAnyCard_ForeignSharesTradeOnPaperOnly()
    {
        _spec = OrderCardTests.Spec with { Currency = "USD" };
        using OrderGateway gateway = New(_channel, TradingMode.Confirm, e => e with { Fx = new FxTable(new Dictionary<string, decimal> { ["USD"] = 10m }) });

        SubmitResult r = await gateway.SubmitAsync(Intent(), Ct);

        Assert.Equal(SubmitStatus.NotPrepared, r.Status);
        Assert.Equal("ERIC B trades in USD: foreign shares trade on paper only (ADR 0005); Confirm refuses them.", r.Message);
        Assert.Empty(_confirm.Cards);
        Assert.Empty(_preflight.Requests);
        Assert.Empty(_channel.Placed);
        Assert.Equal(["gateway-start", "intent", "not-prepared"], AuditKinds());
    }

    public static TheoryData<ConfirmationVerdict, string> NotConfirmed() => new()
    {
        { ConfirmationVerdict.Declined, "another ticker" },
        { ConfirmationVerdict.TooFast, "answered within 1 s of the card appearing (typed ahead, or meant for the card before)" },
        { ConfirmationVerdict.Expired, "no answer within 30 s" },
        { ConfirmationVerdict.NoInput, "the input is closed, so nobody can confirm" },
    };

    [Theory]
    [MemberData(nameof(NotConfirmed))]
    public async Task AnyOtherAnswer_SkipsTheOrder_SendsNothing_AndIsNotAReject(ConfirmationVerdict verdict, string reason)
    {
        _confirm.Answer = (_, _) => Task.FromResult(new ConfirmationAnswer(verdict, reason, null));

        SubmitResult r = await Submit();

        Assert.Equal((SubmitStatus.Skipped, reason), (r.Status, r.Message));
        Assert.Empty(_channel.Placed);
        Assert.Empty(_oms.All);
        Assert.Single(_preflight.Requests); // no re-check
        Assert.Contains("confirm-skip", AuditKinds());
        Assert.DoesNotContain("recheck", AuditKinds());
        Assert.Empty(_brokerRejects);

        EodReport report = Report();
        Assert.Equal((0, 0, 0), (report.RiskRejected, report.Submitted, report.Violations.Count));
        Assert.Contains("1 order card(s) skipped at the confirmation (not rejections)", report.Events);
    }

    [Fact]
    public async Task APriceMove_WhileTheCardIsShown_FailsTheReCheck_AndSkips()
    {
        _confirm.Answer = (_, _) =>
        {
            _time.Advance(TimeSpan.FromSeconds(8));
            Quote(103m); // 2.6 % above the 100.30 limit: outside the ±2 % collar
            return Task.FromResult(ScriptedConfirmation.Yes);
        };

        SubmitResult r = await Submit();

        Assert.Equal(SubmitStatus.Skipped, r.Status);
        Assert.StartsWith("re-check failed: R5 ", r.Message, StringComparison.Ordinal);
        Assert.Empty(_channel.Placed);
        Assert.StartsWith("Re-check failed: R5 ", Assert.Single(_confirm.Told), StringComparison.Ordinal);
        Assert.Single(_preflight.Requests); // R5 failed first: Avanza is not asked again

        EodReport report = Report();
        Assert.Equal((0, 0), (report.RiskRejected, report.Violations.Count));
    }

    [Fact]
    public async Task AvanzasValidation_FailingAtTheReCheck_Skips()
    {
        _preflight.Answers.Enqueue(FakePreflight.Valid(0m));
        _preflight.Answers.Enqueue(FakePreflight.Invalid("priceRampingWarning"));

        SubmitResult r = await Submit();

        Assert.Equal(SubmitStatus.Skipped, r.Status);
        Assert.Equal("re-check failed: R21 Avanza's own order validation failed", r.Message);
        Assert.Empty(_channel.Placed);
    }

    [Theory]
    [InlineData(HaltReason.KillSwitch)]
    [InlineData(HaltReason.StaleData)]
    public async Task AHalt_WhileTheCardIsShown_EndsItAsASkip(HaltReason reason)
    {
        _confirm.Answer = async (_, ct) =>
        {
            _halts.Raise(reason, "test");
            await Task.Delay(Timeout.Infinite, ct);
            return ScriptedConfirmation.Yes;
        };

        SubmitResult r = await Submit();

        Assert.Equal(SubmitStatus.Skipped, r.Status);
        Assert.Equal($"trading halted while the card was shown ({reason})", r.Message);
        Assert.Empty(_channel.Placed);
    }

    [Fact]
    public async Task StoppingTheSession_DuringACard_SendsNothing()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _confirm.Answer = async (_, ct) =>
        {
            await stop.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return ScriptedConfirmation.Yes;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Submit(ct: stop.Token));

        Assert.Empty(_channel.Placed);
        Assert.Contains("confirm-skip", AuditKinds());
    }

    [Fact]
    public async Task StoppingTheSession_AfterJA_ButBeforeTheSend_SendsNothing()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _confirm.Answer = (_, _) =>
        {
            stop.Cancel(); // Ctrl+C just as JA arrives
            return Task.FromResult(ScriptedConfirmation.Yes);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Submit(ct: stop.Token));

        Assert.Empty(_channel.Placed);
        Assert.Empty(_oms.All);
        Assert.Contains("confirm-skip", AuditKinds());
    }

    [Fact]
    public async Task OurChecksComeFirst_AvanzaIsNotAskedAboutAnOrderThatFailsThem()
    {
        SubmitResult r = await Submit(qty: 1_000); // 100,300 SEK: R6, R7, R8, R9

        Assert.Equal(SubmitStatus.RiskRejected, r.Status);
        Assert.Empty(_preflight.Requests);
        Assert.Empty(_confirm.Cards);
    }

    [Fact]
    public async Task AvanzasValidationFalse_RejectsBeforeAnyCard()
    {
        _preflight.Default = FakePreflight.Invalid("orderValueLimitWarning");

        SubmitResult r = await Submit();

        Assert.Equal(SubmitStatus.RiskRejected, r.Status);
        Assert.Equal("orderValueLimitWarning", r.Risk!["R21"].Observed);
        Assert.Empty(_confirm.Cards);
        Assert.Empty(_channel.Placed);
    }

    [Fact]
    public async Task AvanzasFee_IsWhatR9Counts()
    {
        _account.Snapshot = _account.Snapshot with { AvailableCash = 1_010m }; // enough for 1,003 SEK at the model's 0 fee
        _preflight.Default = FakePreflight.Valid(39m);

        SubmitResult r = await Submit();

        Assert.Equal(SubmitStatus.RiskRejected, r.Status);
        Assert.Equal(["R9"], r.Risk!.Failures.Select(f => f.Id));
        Assert.Contains("incl. fees 39", r.Risk["R9"].Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFeeFarFromTheModel_IsFlaggedOnTheCard()
    {
        _preflight.Default = FakePreflight.Valid(5m);

        await Submit();

        OrderCard card = Assert.Single(_confirm.Cards);
        Assert.Equal((0m, 5m), (card.Fees.ModelFees, card.Fees.AvanzaFees));
        Assert.Contains(card.Render(), l => l.Contains("! Avanza's fee 5.00 SEK differs from the model's 0.00", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APreflightFault_Halts_AndRejects()
    {
        _preflight.Default = PreflightOutcome.Failed(BrokerFault.SessionExpired, "validate: HTTP 401");

        SubmitResult r = await Submit();

        Assert.Equal(SubmitStatus.RiskRejected, r.Status);
        Assert.Equal("not run", r.Risk!["R21"].Observed);
        Assert.True(_halts.IsActive(HaltReason.Session));
        Assert.Empty(_confirm.Cards);
    }

    [Fact]
    public async Task AnInstrumentWithoutAnIsin_CannotBeAskedAbout_SoR21Fails()
    {
        _spec = _spec with { Isin = null };

        SubmitResult r = await Submit();

        Assert.Equal(SubmitStatus.RiskRejected, r.Status);
        Assert.False(r.Risk!["R21"].Passed);
        Assert.Empty(_preflight.Requests);
    }

    public static TheoryData<Exception, HaltReason?> AccountFailures() => new()
    {
        { new AccountStateException("account ***001 may no longer trade (R1): account ***001 has credit."), HaltReason.Account },
        { new SessionExpiredException("positions", 401), HaltReason.Session },
        { new EndpointGoneException("positions"), HaltReason.EndpointGone },
        { new BrokerUnavailableException("positions", "timed out"), null },
    };

    [Theory]
    [MemberData(nameof(AccountFailures))]
    public async Task AnUnreadableAccount_BlocksTheOrder_AndHaltsWhenItMust(Exception failure, HaltReason? halt)
    {
        _account.Failure = failure;

        SubmitResult r = await Submit();

        Assert.Equal(SubmitStatus.Blocked, r.Status);
        Assert.Empty(_preflight.Requests);
        Assert.Empty(_confirm.Cards);
        Assert.Contains("account-unavailable", AuditKinds());
        HaltReason[] expected = halt is null ? [] : [halt.Value];
        Assert.Equal(expected, _halts.Active.Select(h => h.Reason));
    }

    [Fact]
    public async Task AnAccountThatChangesDuringTheCard_SkipsTheOrder_AndHalts()
    {
        _confirm.Answer = (_, _) =>
        {
            _account.Failure = new AccountStateException("account ***001 is no longer among the trading accounts; live orders stop (R1).");
            return Task.FromResult(ScriptedConfirmation.Yes);
        };

        SubmitResult r = await Submit();

        Assert.Equal((SubmitStatus.Skipped, "the re-check could not read the account state"), (r.Status, r.Message));
        Assert.Empty(_channel.Placed);
        Assert.True(_halts.IsActive(HaltReason.Account));
    }

    [Fact]
    public async Task AFill_MakesTheNextAccountReadFresh()
    {
        SubmitResult r = await Submit();
        int before = _account.Invalidations;

        _channel.Fill(r.Order!.ClientOrderId, r.Order.BrokerOrderId!.Value, 10, 100.3m);

        Assert.Equal(before + 1, _account.Invalidations);
    }

    [Fact]
    public void Confirm_NeedsItsCardAndPreflight_AndTheRealChannelItsAuthorization()
    {
        Assert.Contains("needs the order card", Assert.Throws<ModeNotAllowedException>(() => New(_channel, TradingMode.Confirm, e => e with { Confirmation = null })).Message, StringComparison.Ordinal);
        Assert.Contains("Avanza's pre-trade checks", Assert.Throws<ModeNotAllowedException>(() => New(_channel, TradingMode.Confirm, e => e with { Preflight = null })).Message, StringComparison.Ordinal);
        Assert.Contains("needs the live authorization", Assert.Throws<ModeNotAllowedException>(() => New(new FakeLiveChannel(), TradingMode.Confirm)).Message, StringComparison.Ordinal);
        Assert.Contains("Phase 8", Assert.Throws<ModeNotAllowedException>(() => New(_channel, TradingMode.Auto)).Message, StringComparison.Ordinal);
    }

    // ---- Phase 7 step 4: the real channel, only with the startup checks' authorization -----------------------------

    [Fact]
    public async Task WithItsAuthorization_TheRealChannel_SendsAConfirmedCard_ExactlyOnce()
    {
        using var rig = new StartupRig(_time);
        var live = new RecordingLiveChannel();
        _gateway = New(live, TradingMode.Confirm, e => e with { Live = rig.Authorize(live) });

        SubmitResult r = await Submit();

        Assert.Equal(SubmitStatus.Accepted, r.Status);
        Assert.Equal((Isk, 10L, 100.3m), (Assert.Single(live.Placed).Account, live.Placed[0].Volume, live.Placed[0].LimitPrice));
        OrderCard card = Assert.Single(_confirm.Cards);
        Assert.False(card.Simulated);
        Assert.Equal(" CONFIRM MODE · a real order on your Avanza account", card.Render()[1]);
        Assert.Equal("Sent: order AVZ-1, Working.", _confirm.Told[^1]);

        System.Text.Json.JsonElement start = Directory.GetFiles(_dir.Path).SelectMany(AuditLog.Read).First(e => e.GetProperty("kind").GetString() == "gateway-start");
        System.Text.Json.JsonElement recorded = start.GetProperty("data").GetProperty("live");
        Assert.Equal("***001", recorded.GetProperty("account").GetString());
        Assert.Equal(9, recorded.GetProperty("checks").GetArrayLength());
    }

    [Fact]
    public async Task AConfirmedOrder_AndItsReconciledFill_AreInTheExecutionReport()
    {
        using var rig = new StartupRig(_time);
        var live = new RecordingLiveChannel();
        _gateway = New(live, TradingMode.Confirm, e => e with { Live = rig.Authorize(live) });
        _preflight.Default = FakePreflight.Valid(1m);

        SubmitResult r = await Submit(); // decided at 100.37; the market at the send: bid 100.40, ask 100.60
        _oms.ApplyFillValue(r.Order!.ClientOrderId, 10, 10 * 100.3m, 0m, "reconciliation"); // as live reconciliation books a deal

        EodLiveOrder o = Assert.Single(EodReport.Build(_dir.Path, new DateOnly(2026, 9, 28), _time).Live!.Orders);
        Assert.Equal(("ERIC B", "Buy", 10L, 100.3m, "Filled", false), (o.Ticker, o.Side, o.Filled, o.AverageFillPrice, o.State, o.Simulated));
        Assert.Equal((100.37m, 100.5m), (o.DecisionPrice, o.ArrivalMid));
        Assert.Equal((-7.0m, -19.9m), (o.SlippageVsDecisionBps, o.SlippageVsArrivalBps)); // below both: better than either
        Assert.Equal((1m, 0m), (o.AvanzaFee, o.ModelFee));
    }

    [Fact]
    public async Task WithTheRealChannel_ADeclinedCard_SendsNothing()
    {
        using var rig = new StartupRig(_time);
        var live = new RecordingLiveChannel();
        _gateway = New(live, TradingMode.Confirm, e => e with { Live = rig.Authorize(live) });
        _confirm.Answer = (_, _) => Task.FromResult(new ConfirmationAnswer(ConfirmationVerdict.Declined, "JA without the ticker", TimeSpan.FromSeconds(3)));

        Assert.Equal(SubmitStatus.Skipped, (await Submit()).Status);
        Assert.Empty(live.Placed);
    }

    [Fact]
    public void TheRealChannel_IsRefused_WithoutTheRightAuthorization()
    {
        using var rig = new StartupRig(_time);
        var live = new RecordingLiveChannel();
        var other = new RecordingLiveChannel();
        LiveAuthorization forOther = rig.Authorize(other);

        string Refusal(IBrokerOrderChannel channel, TradingMode mode, Func<GatewayEnvironment, GatewayEnvironment> change) =>
            Assert.Throws<ModeNotAllowedException>(() => New(channel, mode, change)).Message;

        Assert.Contains("needs the live authorization that only the Confirm startup checks issue", Refusal(live, TradingMode.Confirm, e => e), StringComparison.Ordinal);
        Assert.Contains("issued for another channel instance", Refusal(live, TradingMode.Confirm, e => e with { Live = forOther }), StringComparison.Ordinal);
        Assert.Contains("accepts only the Paper or Backtest channel", Refusal(other, TradingMode.Paper, e => e with { Live = forOther }), StringComparison.Ordinal);
        Assert.Contains("must be exactly the authorized account ***001", Refusal(other, TradingMode.Confirm, e => e with
        {
            Live = forOther,
            AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { Isk.Value, "9990002" },
        }), StringComparison.Ordinal);
        Assert.Contains("not the simulated 'fake-sim'", Refusal(_channel, TradingMode.Confirm, e => e with { Live = forOther }), StringComparison.Ordinal);
        Assert.Contains("Phase 8", Refusal(other, TradingMode.Auto, e => e with { Live = forOther }), StringComparison.Ordinal);
        Assert.Empty(other.Placed);
    }

    [Fact]
    public async Task Paper_NeverAsksAvanza_NorShowsACard()
    {
        _gateway = New(_channel, TradingMode.Paper);

        SubmitResult r = await Submit();

        Assert.Equal(SubmitStatus.Accepted, r.Status);
        Assert.Empty(_preflight.Requests);
        Assert.Empty(_confirm.Cards);
        Assert.DoesNotContain("preflight", AuditKinds());
    }
}
