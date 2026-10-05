using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Reconciliation;
using QuantAnalyst.Trading.Reports;
using QuantAnalyst.Trading.Risk;
using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Trading.Tests;

/// <summary>Plan 23: the owner's buys and sells by hand in Paper, through the same gateway and risk checks.</summary>
public sealed class ManualOrdersTests : IDisposable
{
    private static readonly OrderbookId Eric = RiskEngineTests.Eric;
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 7, 9, 0, TimeSpan.Zero)); // 09:09 Stockholm, Monday
    private readonly SettableQuotes _quotes = new();
    private readonly StringWriter _output = new();
    private readonly AuditLog _audit;
    private readonly OrderManager _oms;
    private readonly PaperBook _book;
    private readonly OrderGateway _gateway;
    private readonly KillSwitch _kill;
    private readonly ManualOrderInbox _inbox;
    private readonly PaperSession _session;
    private readonly List<OrderIntent> _strategy = [];

    public ManualOrdersTests()
    {
        _audit = new AuditLog(_dir.File("audit"), _time);
        var halts = new HaltController(_audit, _time);
        _oms = new OrderManager(_audit, halts, _time);
        _book = PaperBook.InMemory(100_000m, "test-mini", _quotes, _time);
        var instruments = new InstrumentCatalog([OrderPreparationTests.Spec()]);
        var channel = new PaperOrderChannel(_book, PaperOrderChannelTests.Mini, _quotes, instruments, _time);
        MarketCalendar calendar = OrderGatewayTests.Calendar();
        var risk = new PreTradeRiskEngine(RiskLimits.AdrDefaults);
        _gateway = new OrderGateway(channel, new GatewayEnvironment
        {
            Mode = TradingMode.Paper,
            Instruments = instruments,
            Quotes = _quotes,
            Account = _book,
            Calendar = calendar,
            Universe = new Universe([new UniverseEntry(Eric, "ERIC B", "Ericsson B")]),
            AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { PaperConfig.AccountId },
            Fees = (p, s) => channel.EstimateFees(p.Value, s.Currency),
            CourtageVerified = false,
        }, risk, _oms, halts, _audit, _time);
        _kill = new KillSwitch(_gateway, halts, _audit, _time, _dir.File("KILL"), _dir.File("state"), _book, RiskLimits.AdrDefaults, watch: false);
        var schedule = new TradingSchedule(calendar, RiskLimits.AdrDefaults, new TimeOnly(9, 10));
        _inbox = new ManualOrderInbox(_dir.File("paper"), _time);
        _session = new PaperSession(_gateway, channel, _book, _kill, new Reconciler(_oms, halts, _audit, _time, _book.Account), halts, schedule, _audit, _time, Decide, _output)
        {
            Manual = new ManualOrderDesk(_inbox, _book, _quotes, instruments, risk, _audit, _output),
        };
    }

    public void Dispose()
    {
        _kill.Dispose();
        _gateway.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void TheInbox_KeepsRequestsUntilTaken_AndCancelsAWaitingOne()
    {
        ManualOrderRequest buy = _inbox.Place(ManualAction.Buy, Eric, "ERIC B", 7, null, "cli");
        ManualOrderRequest sell = _inbox.Place(ManualAction.Sell, Eric, "ERIC B", 3, 101.5m, "app");

        Assert.Matches(@"^M260928-[0-9a-f]{4}$", buy.Id);
        Assert.Equal([buy, sell], _inbox.Pending());
        Assert.Equal("Buy 7 ERIC B (at the ask)", buy.Describe());
        Assert.Equal("Sell 3 ERIC B (limit 101.5)", sell.Describe());

        Assert.True(_inbox.Cancel(sell.Id));
        Assert.False(_inbox.Cancel(sell.Id));
        Assert.Equal([buy], _inbox.Pending());
        ManualOrderOutcome cancelled = Assert.Single(_inbox.Done(new DateOnly(2026, 9, 28)));
        Assert.Equal(("cancelled", sell), (cancelled.Outcome, cancelled.Request));
        Assert.Empty(_inbox.Done(new DateOnly(2026, 9, 29)));

        Assert.Throws<ArgumentException>(() => _inbox.Place(ManualAction.Buy, Eric, "ERIC B", 0, null, "cli"));
        Assert.Throws<ArgumentException>(() => _inbox.Place(ManualAction.Buy, Eric, "ERIC B", 5, -1m, "cli"));
    }

    [Fact]
    public async Task AManualBuy_GoesFirst_AtTheAsk_AndTheStrategyLeavesTheShareAlone()
    {
        ManualOrderRequest r = _inbox.Place(ManualAction.Buy, Eric, "ERIC B", 7, null, "cli");

        await StepFor(TimeSpan.FromSeconds(80)); // 09:09 to 09:10:20: the manual buy, then the decision

        OmsOrder manual = Assert.Single(_oms.All); // the strategy's two buys for ERIC B were dropped
        Assert.Equal((OrderSide.Buy, 7L, 100.6m), (manual.Side, manual.Volume, manual.LimitPrice));
        Assert.Equal(7, _book.Position(Eric));
        Assert.Contains(Eric, _book.ManualHolds);
        Assert.Empty(_inbox.Pending());
        ManualOrderOutcome sent = Assert.Single(_inbox.Done());
        Assert.Equal(("sent", r.Id), (sent.Outcome, sent.Request.Id));
        Assert.Contains("ERIC B is now manual", sent.Message, StringComparison.Ordinal);
        string log = _output.ToString();
        Assert.Contains("manual Buy 7 ERIC B: Accepted (Filled, filled 7/7 @ 100.6)", log, StringComparison.Ordinal);

        EodReport report = EodReport.Build(_audit.Directory, new DateOnly(2026, 9, 28), _time);
        Assert.Equal(1, report.ManualOrdersSent);
        Assert.StartsWith($"[{r.Id}] Buy 7 ERIC B: sent (Filled, filled 7/7 @ 100.6;", Assert.Single(report.ManualOrders), StringComparison.Ordinal);
        Assert.Contains("Manual: 1 request(s), 1 sent.", report.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TakingAShareByHand_CancelsTheStrategysWorkingOrders_ForIt()
    {
        await StepFor(TimeSpan.FromSeconds(62)); // 09:10:02: the decision's first resting buy works, the second waits for the pace
        OmsOrder strategy = Assert.Single(_oms.All);
        Assert.Equal(OmsState.Working, strategy.State);

        _inbox.Place(ManualAction.Buy, Eric, "ERIC B", 3, 100.0m, "app");
        await StepFor(TimeSpan.FromSeconds(30));

        Assert.Equal(OmsState.Cancelled, strategy.State);
        Assert.Contains(_oms.All, o => o.Volume == 3 && o.LimitPrice == 100.0m);
        Assert.Equal(2, _oms.All.Count); // the strategy's second buy was dropped, never sent
    }

    [Fact]
    public async Task AManualOrderRightAfterTheCancelsItCaused_WaitsOutR12_InsteadOfBeingRefused()
    {
        // Review: the strategy's two buys rest (09:10:00 and 09:10:13); at 09:10:30 the pace allows an order at once, but
        // the cancels count as actions on ERIC B, so R12 wants 5 s before the manual buy.
        await StepFor(TimeSpan.FromSeconds(90));
        Assert.Equal(2, _oms.All.Count(o => o.State == OmsState.Working));

        _inbox.Place(ManualAction.Buy, Eric, "ERIC B", 3, null, "app");
        await StepFor(TimeSpan.FromSeconds(10));

        Assert.All(_oms.All.Where(o => o.Volume != 3), o => Assert.Equal(OmsState.Cancelled, o.State));
        OmsOrder manual = _oms.All.Single(o => o.Volume == 3);
        Assert.Equal(OmsState.Filled, manual.State);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 7, 10, 35, TimeSpan.Zero), manual.CreatedUtc); // 5 s after the cancels
        Assert.Equal("sent", Assert.Single(_inbox.Done()).Outcome);
        Assert.DoesNotContain("R12", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestPlacedAfterTheWindowClosed_IsForTheNextTradingDay()
    {
        // Friday 17:25 Stockholm: after the trading window (17:20), before the close (17:30). Monday sends it.
        var friday = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 15, 25, 0, TimeSpan.Zero));
        ManualOrderRequest late = new ManualOrderInbox(_dir.File("paper"), friday).Place(ManualAction.Buy, Eric, "ERIC B", 2, null, "cli");

        await StepFor(TimeSpan.FromSeconds(3));

        Assert.Equal(("sent", late.Id), (Assert.Single(_inbox.Done()).Outcome, Assert.Single(_inbox.Done()).Request.Id));
        Assert.Equal(2, _book.Position(Eric));
    }

    [Fact]
    public async Task ARequestStillWaitingAtTheClose_ExpiresThen_InTheDaysReport()
    {
        // No live price all day: the buy waits; at the close it expires (the report shows it), but a request placed after
        // the window closed is for the next trading day.
        ManualOrderRequest r = _inbox.Place(ManualAction.Buy, Eric, "ERIC B", 2, null, "cli");
        await _session.StepAsync(CancellationToken.None);
        Assert.Single(_inbox.Pending());

        _time.SetUtcNow(new DateTimeOffset(2026, 9, 28, 15, 25, 0, TimeSpan.Zero)); // 17:25
        ManualOrderRequest tomorrow = _inbox.Place(ManualAction.Sell, Eric, "ERIC B", 1, 101m, "cli");
        _time.SetUtcNow(new DateTimeOffset(2026, 9, 28, 15, 30, 5, TimeSpan.Zero)); // 17:30:05, after the close
        await _session.StepAsync(CancellationToken.None);

        ManualOrderOutcome expired = Assert.Single(_inbox.Done());
        Assert.Equal(("expired", r.Id), (expired.Outcome, expired.Request.Id));
        Assert.Equal("the trading window closed at 17:20 before it was sent (no live price came); a request is for one trading day", expired.Message);
        Assert.Equal([tomorrow], _inbox.Pending());
        EodReport report = EodReport.Build(_audit.Directory, new DateOnly(2026, 9, 28), _time);
        Assert.StartsWith($"[{r.Id}] Buy 2 ERIC B: expired", Assert.Single(report.ManualOrders), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutcomeThatCannotBeWritten_DoesNotStopTheSession_TheAuditHasIt()
    {
        // Review: a CLI cancel may hold done.jsonl for a moment; a file that stays unwritable must not crash the session.
        var desk = new ManualOrderDesk(_inbox, _book, _quotes, new InstrumentCatalog([OrderPreparationTests.Spec()]), new PreTradeRiskEngine(RiskLimits.AdrDefaults), _audit, _output);
        _quotes.Set(Eric, _time.GetUtcNow(), 100.4m, 500, 100.6m, 700, 100.5m, 10_000);
        _inbox.Place(ManualAction.Buy, Eric, "ERIC B", 2, null, "cli");
        Assert.Single(desk.Due(_time.GetUtcNow(), _ => new ManualWindow(true, DateTimeOffset.MinValue)));
        Directory.CreateDirectory(Path.Combine(_inbox.Directory, ManualOrderInbox.DoneFileName)); // a folder where the file should be

        desk.Recover();

        Assert.Contains("(not written to manual/done.jsonl:", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("manual-order", AuditLog.Read(Directory.GetFiles(_audit.Directory, "*.jsonl").Single()).Select(x => x.GetProperty("kind").GetString()));
        Assert.Empty(desk.Due(_time.GetUtcNow(), _ => new ManualWindow(true, DateTimeOffset.MinValue)));
    }

    [Fact]
    public async Task ARefusedManualOrder_LeavesTheShareToTheStrategy()
    {
        _inbox.Place(ManualAction.Buy, Eric, "ERIC B", 1_000, null, "cli"); // 100,600 SEK: R6 allows 10 % of the account

        await StepFor(TimeSpan.FromSeconds(5));

        ManualOrderOutcome refused = Assert.Single(_inbox.Done());
        Assert.Equal("rejected", refused.Outcome);
        Assert.Contains("R6", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Eric, _book.ManualHolds);
    }

    [Fact]
    public async Task ARequestThatWaitedPastAClose_Expires_AndAReleaseGivesTheShareBack()
    {
        // Placed Friday afternoon (no session ran); Monday's session must not send it.
        var friday = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 13, 0, 0, TimeSpan.Zero));
        ManualOrderRequest stale = new ManualOrderInbox(_dir.File("paper"), friday).Place(ManualAction.Buy, Eric, "ERIC B", 5, null, "cli");
        _book.HoldManually(Eric);
        _inbox.Place(ManualAction.Release, Eric, "ERIC B", 0, null, "cli");

        await StepFor(TimeSpan.FromSeconds(3));

        Assert.Empty(_oms.All);
        IReadOnlyList<ManualOrderOutcome> done = _inbox.Done();
        Assert.Equal("expired", done.Single(d => d.Request.Id == stale.Id).Outcome);
        Assert.Contains("placed before the trading window of 2026-09-25 closed", done.Single(d => d.Request.Id == stale.Id).Message, StringComparison.Ordinal);
        Assert.Equal("released", done.Single(d => d.Request.Action == ManualAction.Release).Outcome);
        Assert.DoesNotContain(Eric, _book.ManualHolds);
    }

    [Fact]
    public async Task OutsideTheTradingWindow_ARequestWaits_AndWithoutAPriceToo()
    {
        var early = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 6, 30, 0, TimeSpan.Zero)); // 08:30: before the window
        var desk = new ManualOrderDesk(_inbox, _book, _quotes, new InstrumentCatalog([OrderPreparationTests.Spec()]), new PreTradeRiskEngine(RiskLimits.AdrDefaults), _audit, _output);
        _inbox.Place(ManualAction.Sell, Eric, "ERIC B", 2, null, "cli");

        Assert.Empty(desk.Due(early.GetUtcNow(), _ => new ManualWindow(false, DateTimeOffset.MinValue)));
        Assert.Empty(desk.Due(_time.GetUtcNow(), _ => new ManualWindow(true, DateTimeOffset.MinValue))); // no quote yet
        Assert.Single(_inbox.Pending());
        Assert.Contains("manual Sell 2 ERIC B (at the bid): waiting for a live price", _output.ToString(), StringComparison.Ordinal);

        _quotes.Set(Eric, _time.GetUtcNow(), 100.4m, 500, 100.6m, 700, 100.5m, 10_000);
        (ManualOrderRequest _, OrderIntent intent, InstrumentSpec _) = Assert.Single(desk.Due(_time.GetUtcNow(), _ => new ManualWindow(true, DateTimeOffset.MinValue)));
        Assert.Equal((OrderSide.Sell, 100.4m, ManualOrderDesk.StrategyId), (intent.Side, intent.LimitPrice!.Value, intent.StrategyId)); // at the bid
    }

    [Fact]
    public void ARequestASessionTookButNeverFinished_IsReported_NotSentAgain()
    {
        var desk = new ManualOrderDesk(_inbox, _book, _quotes, new InstrumentCatalog([OrderPreparationTests.Spec()]), new PreTradeRiskEngine(RiskLimits.AdrDefaults), _audit, _output);
        _quotes.Set(Eric, _time.GetUtcNow(), 100.4m, 500, 100.6m, 700, 100.5m, 10_000);
        _inbox.Place(ManualAction.Buy, Eric, "ERIC B", 2, null, "cli");
        Assert.Single(desk.Due(_time.GetUtcNow(), _ => new ManualWindow(true, DateTimeOffset.MinValue))); // taken; the session "stops" here

        desk.Recover();

        Assert.Equal("interrupted", Assert.Single(_inbox.Done()).Outcome);
        Assert.Empty(_inbox.Pending());
        Assert.Empty(desk.Due(_time.GetUtcNow(), _ => new ManualWindow(true, DateTimeOffset.MinValue)));
    }

    [Fact]
    public void TheBook_KeepsItsManualShares()
    {
        string dir = _dir.File("book");
        var config = new PaperConfig(PaperOrderChannelTests.Mini.Name, 50_000m, new TimeOnly(9, 10));
        PaperBook book = PaperBook.OpenOrCreate(dir, config, _quotes, _time, out _);
        Assert.True(book.HoldManually(Eric));
        Assert.False(book.HoldManually(Eric));

        Assert.Equal([Eric], PaperBook.OpenOrCreate(dir, config, _quotes, _time, out _).ManualHolds);
        Assert.Equal([Eric], PaperBook.ManualIn(dir));
        Assert.True(book.ReleaseManual(Eric));
        Assert.Empty(PaperBook.ManualIn(dir));
    }

    /// <summary>The strategy's decision: two resting buys of ERIC B, as in PaperSessionTests.</summary>
    private Task<PlanResult> Decide(CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();
        OrderIntent Buy(long qty, decimal limit) => new(Eric, "ERIC B", OrderSide.Buy, qty, limit, "test", limit, now, "test");
        _strategy.AddRange([Buy(10, 100.2m), Buy(5, 100.1m)]);
        return Task.FromResult(new PlanResult([Buy(10, 100.2m), Buy(5, 100.1m)], ["two resting buys"]));
    }

    private async Task StepFor(TimeSpan span)
    {
        DateTimeOffset until = _time.GetUtcNow() + span;
        while (_time.GetUtcNow() < until)
        {
            _quotes.Set(Eric, _time.GetUtcNow(), 100.4m, 500, 100.6m, 700, 100.5m, 10_000);
            await _session.StepAsync(CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(1));
        }
    }
}
