using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Orders;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Reconciliation;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

public sealed class ReconcilerTests : IDisposable
{
    private static readonly AccountId Account = new("9990001");
    private static readonly OrderbookId Eric = RiskEngineTests.Eric;

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now);
    private readonly AuditLog _audit;
    private readonly HaltController _halts;
    private readonly OrderManager _oms;
    private readonly Reconciler _reconciler;

    public ReconcilerTests()
    {
        _audit = new AuditLog(_dir.Path, _time);
        _halts = new HaltController(_audit, _time);
        _oms = new OrderManager(_audit, _halts, _time);
        _reconciler = new Reconciler(_oms, _halts, _audit, _time, Account);
    }

    public void Dispose() => _dir.Dispose();

    private OmsOrder Sent(long volume = 10, decimal limit = 100m, OrderSide side = OrderSide.Buy)
    {
        OmsOrder o = _oms.Create(Guid.CreateVersion7(), Account, Eric, "ERIC B", side, volume, limit);
        _oms.Transition(o.ClientOrderId, OmsState.Sent, "test");
        return o;
    }

    private OmsOrder Working(string id, long volume = 10, decimal limit = 100m)
    {
        OmsOrder o = Sent(volume, limit);
        _oms.Transition(o.ClientOrderId, OmsState.Working, "test", new OrderId(id));
        return o;
    }

    private OmsOrder UnknownSubmit(long volume = 10, decimal limit = 100m)
    {
        OmsOrder o = Sent(volume, limit);
        _oms.Transition(o.ClientOrderId, OmsState.Unknown, "timeout");
        return o;
    }

    private BrokerOrder Listed(string id, long remaining = 10, long original = 10, decimal price = 100m, DateTimeOffset? created = null, AccountId? account = null) =>
        new(new OrderId(id), account ?? Account, Eric, "ERIC B", OrderSide.Buy, price, remaining, original, "ACTIVE", "NORMAL",
            created ?? _time.GetUtcNow(), null, true, true);

    private BrokerDeal Deal(string orderId, long volume, decimal price, DateTimeOffset? at = null) =>
        new($"d-{orderId}-{volume}", new OrderId(orderId), Account, Eric, OrderSide.Buy, price, volume, at ?? _time.GetUtcNow());

    private ReconciliationReport Run(BrokerOrder[]? open = null, BrokerDeal[]? deals = null) =>
        _reconciler.Reconcile(new BrokerSnapshot(open ?? [], deals ?? [], _time.GetUtcNow()));

    [Fact]
    public void InAgreement_NothingChanges_AndTheRunIsAudited()
    {
        OmsOrder o = Working("A1");
        ReconciliationReport r = Run([Listed("A1")]);
        Assert.True(r.Clean);
        Assert.Empty(r.Actions);
        Assert.Equal(OmsState.Working, o.State);
        Assert.Contains("reconcile", AuditLog.Read(Directory.GetFiles(_dir.Path).Single()).Select(x => x.GetProperty("kind").GetString()));
    }

    [Fact]
    public void DealsTheOmsMissed_BecomeFills_AtTheirAveragePrice()
    {
        OmsOrder o = Working("A1");
        Run([Listed("A1", remaining: 4)], [Deal("A1", 4, 100m), Deal("A1", 2, 99m)]);
        Assert.Equal(OmsState.PartiallyFilled, o.State);
        Assert.Equal(6, o.FilledVolume);
        Assert.Equal(598m, o.FilledValue);
    }

    [Fact]
    public void AnOrderGoneFromTheBroker_IsCancelled_OrFilledByItsDeals()
    {
        OmsOrder gone = Working("A1");
        OmsOrder filled = Working("A2");
        ReconciliationReport r = Run([], [Deal("A2", 10, 100m)]);
        Assert.Equal(OmsState.Cancelled, gone.State);
        Assert.Equal(OmsState.Filled, filled.State);
        Assert.True(r.Clean);
    }

    [Fact]
    public void AnUnknownSubmit_IsLinkedToItsUniqueMatch()
    {
        OmsOrder o = UnknownSubmit();
        Run([Listed("B7", created: o.CreatedUtc.AddSeconds(1))]);
        Assert.Equal(OmsState.Working, o.State);
        Assert.Equal(new OrderId("B7"), o.BrokerOrderId);
        Assert.False(_halts.IsHalted);
    }

    [Fact]
    public void AnUnknownSubmit_FoundOnlyInTheDeals_IsLinkedAndFilled()
    {
        OmsOrder o = UnknownSubmit();
        Run([], [Deal("B8", 10, 99.9m, o.CreatedUtc.AddSeconds(2))]);
        Assert.Equal(OmsState.Filled, o.State);
        Assert.Equal(new OrderId("B8"), o.BrokerOrderId);
    }

    [Theory]
    [InlineData(-4, true)]
    [InlineData(-5, true)]
    [InlineData(-6, false)] // stamped before we could have sent it
    public void TheMatchAllowsFiveSecondsOfClockSkew(int seconds, bool matched)
    {
        OmsOrder o = UnknownSubmit();
        Run([Listed("B7", created: o.CreatedUtc.AddSeconds(seconds))]);
        Assert.Equal(matched, o.BrokerOrderId is not null);
    }

    [Theory]
    [InlineData(99.9, 10)] // another price
    [InlineData(100.0, 11)] // another volume
    public void OnlyTheSameLimitAndVolumeMatch(double price, long original)
    {
        OmsOrder o = UnknownSubmit();
        ReconciliationReport r = Run([Listed("B7", remaining: original, original: original, price: (decimal)price)]);
        Assert.Null(o.BrokerOrderId);
        Assert.Contains(r.Mismatches, m => m.Contains("did not place", StringComparison.Ordinal)); // and it is a stranger
    }

    [Fact]
    public void TwoCandidates_AreAMismatch_AndHaltTrading()
    {
        OmsOrder o = UnknownSubmit();
        ReconciliationReport r = Run([Listed("B1"), Listed("B2")]);
        Assert.Contains(r.Mismatches, m => m.Contains("matches 2 broker orders", StringComparison.Ordinal));
        Assert.Equal(OmsState.Unknown, o.State);
        Assert.True(_halts.IsActive(HaltReason.Reconciliation));
    }

    [Fact]
    public void AnUnknownSubmit_IsNotPlaced_OnlyAfterTwoEmptyRunsAndTwoMinutes()
    {
        OmsOrder o = UnknownSubmit();
        Run();
        _time.Advance(TimeSpan.FromSeconds(30));
        Run();
        Assert.Equal(OmsState.Unknown, o.State); // two runs, but not 2 minutes yet

        _time.Advance(TimeSpan.FromSeconds(90));
        Run();
        Assert.Equal(OmsState.Rejected, o.State);
        Assert.Contains("not placed", o.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFoundOrderResetsTheNotFoundCount()
    {
        OmsOrder o = UnknownSubmit();
        Run();
        _time.Advance(TimeSpan.FromMinutes(3));
        Run([Listed("B7", created: o.CreatedUtc)]);
        Assert.Equal(OmsState.Working, o.State);
    }

    [Fact]
    public void AnUnknownCancel_IsResolved_ByWhatTheBrokerStillLists()
    {
        OmsOrder stillThere = Working("A1");
        OmsOrder cancelled = Working("A2");
        OmsOrder partlyFilled = Working("A3");
        foreach (OmsOrder o in new[] { stillThere, cancelled, partlyFilled })
        {
            _oms.Transition(o.ClientOrderId, OmsState.Unknown, "cancel reply lost");
        }

        Run([Listed("A1")], [Deal("A3", 3, 100m)]);
        Assert.Equal(OmsState.Working, stillThere.State);
        Assert.Equal(OmsState.Cancelled, cancelled.State);
        Assert.Equal((OmsState.Cancelled, 3L), (partlyFilled.State, partlyFilled.FilledVolume));
        Assert.False(_halts.IsHalted);
    }

    [Fact]
    public void Disagreements_AreMismatches_AndACleanRunClearsTheHalt()
    {
        OmsOrder o = Working("A1");
        _oms.ApplyFill(o.ClientOrderId, 5, 100m, 0m, "test");
        ReconciliationReport r = Run([Listed("A1", remaining: 5)], [Deal("A1", 3, 100m)]);
        Assert.Contains(r.Mismatches, m => m.Contains("OMS has 5 filled, the broker's deals 3", StringComparison.Ordinal));

        r = Run([Listed("A1", remaining: 4)], [Deal("A1", 5, 100m)]);
        Assert.Contains(r.Mismatches, m => m.Contains("4 left at the broker, 5 in the OMS", StringComparison.Ordinal));
        Assert.True(_halts.IsActive(HaltReason.Reconciliation));

        Assert.True(Run([Listed("A1", remaining: 5)], [Deal("A1", 5, 100m)]).Clean);
        Assert.False(_halts.IsActive(HaltReason.Reconciliation));
    }

    [Fact]
    public void OtherAccounts_AreIgnored_ButAStrangerOnOursIsAMismatch()
    {
        Assert.True(Run([Listed("X1", account: new AccountId("1112223"))]).Clean);
        ReconciliationReport r = Run([Listed("X2")]);
        Assert.Contains(r.Mismatches, m => m.Contains("X2", StringComparison.Ordinal) && m.Contains("did not place", StringComparison.Ordinal));
    }
}

/// <summary>The paper channel as the reconciliation source: the Paper path reconciles every day.</summary>
public sealed class PaperReconciliationTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task AGatewaySessionOnPaper_ReconcilesClean()
    {
        var time = new FakeTimeProvider(RiskEngineTests.Now);
        var quotes = new SettableQuotes();
        var audit = new AuditLog(_dir.Path, time);
        var halts = new HaltController(audit, time);
        var oms = new OrderManager(audit, halts, time);
        PaperBook book = PaperBook.InMemory(100_000m, "test-mini", quotes, time);
        var instruments = new InstrumentCatalog([OrderPreparationTests.Spec()]);
        var channel = new PaperOrderChannel(book, PaperOrderChannelTests.Mini, quotes, instruments, time);
        using var gateway = new OrderGateway(channel, new GatewayEnvironment
        {
            Mode = TradingMode.Paper,
            Instruments = instruments,
            Quotes = quotes,
            Account = book,
            Calendar = OrderGatewayTests.Calendar(),
            Universe = new Universe([new UniverseEntry(RiskEngineTests.Eric, "ERIC B", "Ericsson B")]),
            AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { PaperConfig.AccountId },
            Fees = (p, s) => channel.EstimateFees(p.Value, s.Currency),
            CourtageVerified = false,
        }, new PreTradeRiskEngine(RiskLimits.AdrDefaults), oms, halts, audit, time);
        var reconciler = new Reconciler(oms, halts, audit, time, book.Account);

        quotes.Set(RiskEngineTests.Eric, time.GetUtcNow(), 100.4m, 500, 100.6m, 15, 100.5m, 10_000);
        await gateway.SubmitAsync(new OrderIntent(RiskEngineTests.Eric, "ERIC B", OrderSide.Buy, 20, 100.6m, "t", 100.6m, time.GetUtcNow(), "t"), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(30));
        quotes.Set(RiskEngineTests.Eric, time.GetUtcNow(), 100.4m, 500, 100.6m, 700, 100.5m, 10_000);
        await gateway.SubmitAsync(new OrderIntent(RiskEngineTests.Eric, "ERIC B", OrderSide.Buy, 10, 100.2m, "t", 100.2m, time.GetUtcNow(), "t"), CancellationToken.None);
        quotes.Set(RiskEngineTests.Eric, time.GetUtcNow(), 100.0m, 500, 100.2m, 700, 100.1m, 10_100);
        channel.OnQuote(quotes.Latest(RiskEngineTests.Eric)!);

        // The first order took 15 at entry and its last 5 from the print; the second shares the print's 10 % (10) and gets 5.
        Assert.Equal([20L, 5L], oms.All.Select(o => o.FilledVolume));
        ReconciliationReport r = await reconciler.RunAsync(channel, CancellationToken.None);
        Assert.True(r.Clean, string.Join("; ", r.Mismatches));
        Assert.Empty(r.Actions);
        Assert.Equal(1, r.Checked);

        channel.EndOfDay("close");
        r = await reconciler.RunAsync(channel, CancellationToken.None);
        Assert.True(r.Clean);
        Assert.Empty(oms.Open);
    }
}
