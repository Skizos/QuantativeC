using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Risk;
using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Trading.Tests;

public sealed class KillSwitchTests : IDisposable
{
    private static readonly OrderbookId Test = new("1001"); // fake id

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now);
    private readonly FakeSimulatedChannel _channel = new();
    private readonly FixedAccount _account;
    private readonly AuditLog _audit;
    private readonly HaltController _halts;
    private readonly OrderManager _oms;
    private readonly OrderGateway _gateway;
    private readonly List<string> _alerts = [];
    private KillSwitch? _kill;

    public KillSwitchTests()
    {
        _audit = new AuditLog(_dir.File("audit"), _time);
        _halts = new HaltController(_audit, _time);
        _oms = new OrderManager(_audit, _halts, _time);
        _account = new FixedAccount(new AccountSnapshot(
            new AccountId("PAPER"), 100_000m, 50_000m, new Dictionary<OrderbookId, long>(), new Dictionary<OrderbookId, decimal>(), 100_000m));
        _gateway = new OrderGateway(_channel, new GatewayEnvironment
        {
            Mode = TradingMode.Paper,
            Instruments = new InstrumentCatalog([
                OrderPreparationTests.Spec(),
                OrderPreparationTests.Spec() with { OrderbookId = Test, Ticker = "TEST B" },
            ]),
            Quotes = new FreshQuotes(_time),
            Account = _account,
            Calendar = OrderGatewayTests.Calendar(),
            Universe = new Universe([new UniverseEntry(RiskEngineTests.Eric, "ERIC B", "Ericsson B"), new UniverseEntry(Test, "TEST B", "Test B")]),
            AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { "PAPER" },
            Fees = (_, _) => 0m,
            CourtageVerified = true,
        }, new PreTradeRiskEngine(RiskLimits.AdrDefaults), _oms, _halts, _audit, _time);
    }

    private string KillFile => _dir.File(KillSwitch.KillFileName);

    [Fact]
    public void AnOfflineReset_BeforeAnySessionEverRan_ClearsTheFlag_AndIsAudited()
    {
        // Found by the Windows app's tests: with no state folder yet, the reset deleted KILL and then threw before
        // writing its audit record.
        KillSwitch.Request(KillFile, "before any session", _time);
        Assert.False(Directory.Exists(StateDir));

        KillResetResult r = KillSwitch.ResetOffline(KillFile, StateDir, _audit, "checked");
        Assert.True(r.Reset, r.Message);
        Assert.False(File.Exists(KillFile));
        Assert.Contains("kill-reset", string.Concat(Directory.GetFiles(_dir.File("audit")).Select(File.ReadAllText)), StringComparison.Ordinal);
    }

    private string StateDir => _dir.File("state");

    private KillSwitch Kill(bool watch = false)
    {
        _kill?.Dispose();
        _kill = new KillSwitch(_gateway, _halts, _audit, _time, KillFile, StateDir, _account, 0.02m, watch);
        _kill.Alerted += _alerts.Add;
        return _kill;
    }

    public void Dispose()
    {
        _kill?.Dispose();
        _gateway.Dispose();
        _dir.Dispose();
    }

    private async Task<OmsOrder> Working(OrderbookId? id = null)
    {
        OrderbookId which = id ?? RiskEngineTests.Eric;
        SubmitResult r = await _gateway.SubmitAsync(
            new OrderIntent(which, which == Test ? "TEST B" : "ERIC B", OrderSide.Buy, 10, 100.3m, "test", 100.3m, _time.GetUtcNow(), "test"), CancellationToken.None);
        Assert.True(r.Status == SubmitStatus.Accepted, r.Message);
        return r.Order!;
    }

    [Fact]
    public async Task QaKill_WritesTheFlag_AndTheNextTickHaltsAndCancelsEverythingThroughTheGateway()
    {
        KillSwitch kill = Kill();
        OmsOrder a = await Working();
        OmsOrder b = await Working(Test);

        KillSwitch.Request(KillFile, "owner: qa kill", _time);
        Assert.Equal(0, await kill.TickAsync(CancellationToken.None));

        Assert.True(kill.IsKilled);
        Assert.Equal("file KILL", kill.Record!.Source);
        Assert.Contains("owner: qa kill", kill.Record.Reason, StringComparison.Ordinal);
        Assert.True(_halts.IsActive(HaltReason.KillSwitch));
        Assert.Equal(2, _channel.Cancelled.Count); // via OrderGateway.CancelAsync, audited there
        Assert.Equal([OmsState.Cancelled, OmsState.Cancelled], new[] { a.State, b.State });
        Assert.True(File.Exists(Path.Combine(StateDir, KillSwitch.StateFileName)));
        Assert.Contains(_alerts, m => m.Contains("KILL SWITCH", StringComparison.Ordinal));

        string[] kinds = [.. Directory.GetFiles(_dir.File("audit")).SelectMany(AuditLog.Read).Select(r => r.GetProperty("kind").GetString()!)];
        Assert.Equal(2, kinds.Count(k => k == "cancel"));
        Assert.Contains("kill", kinds);
        Assert.True(AuditLog.Verify(_dir.File("audit")).Valid);
    }

    [Fact]
    public async Task AKilledSession_RefusesNewOrders()
    {
        KillSwitch kill = Kill();
        kill.Trigger("test", "stop");
        _time.Advance(TimeSpan.FromMinutes(1));
        SubmitResult r = await _gateway.SubmitAsync(
            new OrderIntent(RiskEngineTests.Eric, "ERIC B", OrderSide.Buy, 10, 100.3m, "test", 100.3m, _time.GetUtcNow(), "test"), CancellationToken.None);
        Assert.Equal(["R17"], r.Risk!.Failures.Select(f => f.Id));
        Assert.Empty(_channel.Placed);
    }

    [Fact]
    public async Task TheFileWatcher_FiresWithoutATick()
    {
        KillSwitch kill = Kill(watch: true);
        KillSwitch.Request(KillFile, "watched", _time);
        for (int i = 0; i < 100 && !kill.IsKilled; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(kill.IsKilled);
    }

    [Fact]
    public async Task ThreeConsecutiveBrokerRejects_FireIt()
    {
        KillSwitch kill = Kill();
        _channel.OnPlace = _ => OrderSubmitResult.Rejected("rejected by the broker");
        for (int i = 0; i < 3; i++)
        {
            Assert.False(kill.IsKilled);
            await _gateway.SubmitAsync(
                new OrderIntent(RiskEngineTests.Eric, "ERIC B", OrderSide.Buy, 10 + i, 100.3m, "test", 100.3m, _time.GetUtcNow(), "test"), CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(6));
        }

        Assert.True(kill.IsKilled);
        Assert.Contains("3 consecutive broker rejects", kill.Record!.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HaltReason.SchemaDrift, true)]
    [InlineData(HaltReason.EndpointGone, true)]
    [InlineData(HaltReason.StaleData, false)]
    [InlineData(HaltReason.Session, false)]
    public void DriftOrAGoneOrderEndpoint_FireIt_OtherHaltsDoNot(HaltReason reason, bool fires)
    {
        KillSwitch kill = Kill();
        _halts.Raise(reason, "test");
        Assert.Equal(fires, kill.IsKilled);
    }

    [Fact]
    public async Task AnUnknownOrderOlderThanTwoMinutes_FiresIt()
    {
        KillSwitch kill = Kill();
        _channel.OnPlace = _ => OrderSubmitResult.Unknown("timeout");
        await _gateway.SubmitAsync(
            new OrderIntent(RiskEngineTests.Eric, "ERIC B", OrderSide.Buy, 10, 100.3m, "test", 100.3m, _time.GetUtcNow(), "test"), CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(2));
        await kill.TickAsync(CancellationToken.None);
        Assert.False(kill.IsKilled); // exactly 2 minutes is not more than 2
        _time.Advance(TimeSpan.FromSeconds(1));
        await kill.TickAsync(CancellationToken.None);
        Assert.True(kill.IsKilled);
        Assert.Contains("Unknown", kill.Record!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReconciliationMismatchLongerThanAMinute_FiresIt()
    {
        KillSwitch kill = Kill();
        _halts.Raise(HaltReason.Reconciliation, "book and OMS disagree");
        _time.Advance(TimeSpan.FromSeconds(60));
        await kill.TickAsync(CancellationToken.None);
        Assert.False(kill.IsKilled);
        _time.Advance(TimeSpan.FromSeconds(1));
        await kill.TickAsync(CancellationToken.None);
        Assert.True(kill.IsKilled);
    }

    [Theory]
    [InlineData(98_001, false)]
    [InlineData(98_000, true)] // exactly -2 %: the stop is hit (R19 passes only above -2 %)
    [InlineData(90_000, true)]
    public async Task TheDailyLossStop_FiresIt(int value, bool fires)
    {
        KillSwitch kill = Kill();
        _account.Snapshot = _account.Snapshot with { AccountValue = value };
        await kill.TickAsync(CancellationToken.None);
        Assert.Equal(fires, kill.IsKilled);
    }

    [Fact]
    public async Task RefusedCancels_AreRetriedEveryTenSeconds_WithAnAlert()
    {
        KillSwitch kill = Kill();
        OmsOrder order = await Working();
        _channel.OnCancel = _ => OrderSubmitResult.Rejected("try later");
        kill.Trigger("test", "stop");

        Assert.Equal(1, await kill.TickAsync(CancellationToken.None));
        Assert.Contains(_alerts, m => m.Contains("still working", StringComparison.Ordinal));
        _time.Advance(TimeSpan.FromSeconds(9));
        await kill.TickAsync(CancellationToken.None);
        Assert.Single(_channel.Cancelled); // not before 10 s

        _time.Advance(TimeSpan.FromSeconds(1));
        _channel.OnCancel = null;
        Assert.Equal(0, await kill.TickAsync(CancellationToken.None));
        Assert.Equal(2, _channel.Cancelled.Count);
        Assert.Equal(OmsState.Cancelled, order.State);

        _time.Advance(TimeSpan.FromSeconds(30));
        await kill.TickAsync(CancellationToken.None);
        Assert.Equal(2, _channel.Cancelled.Count); // done: nothing more to cancel
    }

    [Fact]
    public async Task Reset_IsRefusedWhileOrdersAreOpen_OrReconciliationIsOff_AndThenClearsEverything()
    {
        KillSwitch kill = Kill();
        Assert.False(kill.Reset("owner").Reset);

        await Working();
        _channel.OnCancel = _ => OrderSubmitResult.Rejected("try later");
        KillSwitch.Request(KillFile, "owner", _time);
        await kill.TickAsync(CancellationToken.None);
        KillResetResult refused = kill.Reset("owner");
        Assert.False(refused.Reset);
        Assert.Contains("still open", refused.Message, StringComparison.Ordinal);

        _channel.OnCancel = null;
        _time.Advance(TimeSpan.FromSeconds(10));
        await kill.TickAsync(CancellationToken.None);
        _halts.Raise(HaltReason.Reconciliation, "test");
        Assert.Contains("reconciliation", kill.Reset("owner").Message, StringComparison.Ordinal);
        _halts.Clear(HaltReason.Reconciliation, "resolved");

        KillResetResult ok = kill.Reset("owner checked");
        Assert.True(ok.Reset, ok.Message);
        Assert.False(kill.IsKilled);
        Assert.False(_halts.IsHalted);
        Assert.False(File.Exists(KillFile));
        Assert.False(File.Exists(Path.Combine(StateDir, KillSwitch.StateFileName)));

        await kill.TickAsync(CancellationToken.None);
        Assert.False(kill.IsKilled); // the flag file is gone, so it stays reset
    }

    [Fact]
    public void AKillSurvivesARestart()
    {
        Kill().Trigger("test", "before the restart");
        var halts = new HaltController(_audit, _time);
        using var restarted = new KillSwitch(_gateway, halts, _audit, _time, KillFile, StateDir, null, 0.02m, watch: false);
        Assert.True(restarted.IsKilled);
        Assert.Equal("before the restart", restarted.Record!.Reason);
        Assert.True(halts.IsActive(HaltReason.KillSwitch));

        File.WriteAllText(Path.Combine(StateDir, KillSwitch.StateFileName), "garbage");
        var halts2 = new HaltController(_audit, _time);
        using var damaged = new KillSwitch(_gateway, halts2, _audit, _time, KillFile, StateDir, null, 0.02m, watch: false);
        Assert.True(damaged.IsKilled); // unreadable still means killed
        Assert.True(halts2.IsActive(HaltReason.KillSwitch));
    }

    [Fact]
    public void TheFirstTriggerIsKept()
    {
        KillSwitch kill = Kill();
        kill.Trigger("first", "one");
        kill.Trigger("second", "two");
        Assert.Equal(("first", "one"), (kill.Record!.Source, kill.Record.Reason));
        Assert.Single(_alerts);
    }
}

public sealed class TradingScheduleTests
{
    private static readonly TimeOnly Decision = new(9, 10);

    private static MarketCalendar Calendar(DateOnly? verifiedOn = null) => new([
        new CalendarYear("XSTO", 2026, new TimeOnly(9, 0), new TimeOnly(17, 30), new TimeOnly(9, 0), new TimeOnly(13, 0),
            [new CalendarEntry(new DateOnly(2026, 12, 24), "Christmas Eve"), new CalendarEntry(new DateOnly(2026, 12, 25), "Christmas Day")],
            [new CalendarEntry(new DateOnly(2026, 12, 30), "Test half day")],
            "test", verifiedOn),
    ]);

    private static TradingSchedule Schedule(DateOnly? verifiedOn = null) => new(Calendar(verifiedOn), RiskLimits.AdrDefaults, Decision);

    private static DateTimeOffset Utc(int month, int day, int hour, int minute) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void AFullDay_InSummerTime()
    {
        SessionPlan p = Schedule().Plan(new DateOnly(2026, 9, 28))!;
        Assert.Equal(Utc(9, 28, 7, 0), p.OpenUtc);
        Assert.Equal(Utc(9, 28, 7, 5), p.WindowOpenUtc);
        Assert.Equal(Utc(9, 28, 7, 10), p.DecisionUtc);
        Assert.Equal(Utc(9, 28, 15, 20), p.WindowCloseUtc);
        Assert.Equal(Utc(9, 28, 15, 30), p.CloseUtc);
    }

    [Fact]
    public void AFullDay_AfterTheClocksGoBack()
    {
        // Summer time ends on Sunday 2026-10-25; Monday is UTC+1.
        SessionPlan p = Schedule().Plan(new DateOnly(2026, 10, 26))!;
        Assert.Equal(Utc(10, 26, 8, 10), p.DecisionUtc);
        Assert.Equal(Utc(10, 26, 16, 30), p.CloseUtc);
    }

    [Fact]
    public void AHalfDay_ClosesTheWindowAt1250()
    {
        SessionPlan p = Schedule().Plan(new DateOnly(2026, 12, 30))!;
        Assert.Equal(TradingDayKind.Half, p.Day.Kind);
        Assert.Equal(Utc(12, 30, 11, 50), p.WindowCloseUtc);
        Assert.Equal(Utc(12, 30, 12, 0), p.CloseUtc);
    }

    [Theory]
    [InlineData(2026, 9, 26)] // Saturday
    [InlineData(2026, 12, 24)] // holiday
    public void NoSession_HasNoPlan(int y, int m, int d) => Assert.Null(Schedule().Plan(new DateOnly(y, m, d)));

    [Theory]
    [InlineData(6, 59, SessionPhase.BeforeWindow)]
    [InlineData(7, 4, SessionPhase.BeforeWindow)]
    [InlineData(7, 5, SessionPhase.Window)]
    [InlineData(15, 19, SessionPhase.Window)]
    [InlineData(15, 20, SessionPhase.AfterWindow)]
    [InlineData(15, 30, SessionPhase.Closed)]
    public void PhaseAt_FollowsTheWindow(int hour, int minute, SessionPhase expected) =>
        Assert.Equal(expected, Schedule().PhaseAt(Utc(9, 28, hour, minute)));

    [Fact]
    public void PhaseAt_OnAWeekend_IsNoSession() => Assert.Equal(SessionPhase.NoSession, Schedule().PhaseAt(Utc(9, 27, 10, 0)));

    [Fact]
    public void NextDecision_IsTodayUntilItPasses_ThenTheNextTradingDay()
    {
        TradingSchedule s = Schedule();
        Assert.Equal(new DateOnly(2026, 9, 28), s.NextDecision(Utc(9, 28, 6, 0)).Date);
        Assert.Equal(new DateOnly(2026, 9, 28), s.NextDecision(Utc(9, 28, 7, 10)).Date);
        Assert.Equal(new DateOnly(2026, 9, 29), s.NextDecision(Utc(9, 28, 7, 11)).Date);
        Assert.Equal(new DateOnly(2026, 9, 28), s.NextDecision(Utc(9, 25, 16, 0)).Date); // Friday evening -> Monday
        Assert.Equal(new DateOnly(2026, 12, 28), s.NextDecision(Utc(12, 23, 9, 0)).Date); // over Christmas
    }

    [Theory]
    [InlineData(9, 0)] // before the window opens
    [InlineData(12, 50)] // would miss half days
    [InlineData(16, 0)]
    public void ADecisionTimeOutsideTheWindow_IsRefused(int hour, int minute) =>
        Assert.Throws<TradingConfigException>(() => new TradingSchedule(Calendar(), RiskLimits.AdrDefaults, new TimeOnly(hour, minute)));

    [Fact]
    public void Verification_IsReportedPerYear()
    {
        Assert.False(Schedule().IsVerified(new DateOnly(2026, 9, 28)));
        Assert.True(Schedule(new DateOnly(2026, 9, 30)).IsVerified(new DateOnly(2026, 9, 28)));
    }
}
