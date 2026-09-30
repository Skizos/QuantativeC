using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

/// <summary>Quotes the test sets by hand.</summary>
internal sealed class SettableQuotes : IQuoteSource
{
    private readonly Dictionary<OrderbookId, Quote> _quotes = [];

    public Quote? Latest(OrderbookId id) => _quotes.GetValueOrDefault(id);

    public Quote Set(
        OrderbookId id, DateTimeOffset now, decimal? bid, decimal bidVolume, decimal? ask, decimal askVolume, decimal? last, decimal? totalVolume,
        decimal? dayHigh = null, decimal? dayLow = null)
    {
        var q = new Quote(id, bid, bidVolume, ask, askVolume, last, now.AddSeconds(-1), totalVolume, [], QuoteSource.Stream, now, now, now, now, false, null, dayHigh, dayLow);
        _quotes[id] = q;
        return q;
    }

    public void Put(Quote quote) => _quotes[quote.OrderbookId] = quote;
}

public sealed class PaperOrderChannelTests : IDisposable
{
    private static readonly OrderbookId Eric = RiskEngineTests.Eric;

    // Mini-like numbers (0.25 %, min 1 SEK), written here so the test does not depend on the owner's cost files.
    internal static readonly CostModel Mini = new("test-mini", "SEK", 1m, 0.0025m, 0.0025m, 0m, 0m, 0.1m, "test", null);

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now);
    private readonly SettableQuotes _quotes = new();
    private readonly PaperBook _book;
    private readonly PaperOrderChannel _channel;
    private readonly List<SimulatedFill> _fills = [];
    private readonly List<(OrderId Id, string Reason)> _ended = [];

    public PaperOrderChannelTests()
    {
        _book = PaperBook.InMemory(50_000m, Mini.Name, _quotes, _time);
        _channel = NewChannel(lot: 1);
    }

    public void Dispose() => _dir.Dispose();

    private PaperOrderChannel NewChannel(long lot, PaperBook? book = null)
    {
        var channel = new PaperOrderChannel(book ?? _book, Mini, _quotes, new InstrumentCatalog([OrderPreparationTests.Spec(lot)]), _time);
        channel.Filled += _fills.Add;
        channel.Ended += (id, why) => _ended.Add((id, why));
        return channel;
    }

    private static ApprovedOrder Order(OrderSide side, long volume, decimal limit, string account = PaperConfig.AccountId) =>
        new(Guid.CreateVersion7(), new AccountId(account), Eric, side, volume, limit, new DateOnly(2026, 9, 28));

    private async Task<OrderSubmitResult> Place(ApprovedOrder order, PaperOrderChannel? channel = null) =>
        await (channel ?? _channel).PlaceAsync(order, CancellationToken.None);

    private void Market(decimal? last, decimal total, decimal bid = 100.4m, decimal ask = 100.6m, decimal bidVolume = 500, decimal askVolume = 700) =>
        _quotes.Set(Eric, _time.GetUtcNow(), bid, bidVolume, ask, askVolume, last, total);

    [Fact]
    public async Task AMarketableBuy_FillsAtTheAsk_UpToTheDisplayedVolume_AndTheRestRests()
    {
        Market(last: 100.5m, total: 10_000, askVolume: 30);
        OrderSubmitResult r = await Place(Order(OrderSide.Buy, 50, 101m));

        Assert.Equal(SubmitOutcome.Accepted, r.Outcome);
        SimulatedFill fill = Assert.Single(_fills);
        Assert.Equal((30L, 100.6m), (fill.Volume, fill.Price));
        Assert.Equal("marketable at entry", fill.How);
        Assert.Equal(1, _channel.RestingCount);
        Assert.Equal(30, _book.Position(Eric));
    }

    [Fact]
    public async Task AMarketableSell_FillsAtTheBid()
    {
        await BuySome(40);
        Market(last: 100.5m, total: 20_000, bidVolume: 25);
        await Place(Order(OrderSide.Sell, 40, 100m));
        Assert.Equal((25L, 100.4m), (_fills[^1].Volume, _fills[^1].Price));
        Assert.Equal(15, _book.Position(Eric));
    }

    [Fact]
    public async Task ARestingBuy_IsNotFilledByATouch_OnlyByATradeThrough_AtItsLimit()
    {
        Market(last: 100.5m, total: 10_000);
        await Place(Order(OrderSide.Buy, 50, 100.3m));
        Assert.Empty(_fills);

        Market(last: 100.3m, total: 12_000); // prints AT the limit: not a fill
        Assert.Equal(0, _channel.OnQuote(_quotes.Latest(Eric)!));

        Market(last: 100.2m, total: 12_300); // through the limit, 300 traded since: 10 % = 30
        Assert.Equal(1, _channel.OnQuote(_quotes.Latest(Eric)!));
        Assert.Equal((30L, 100.3m), (_fills[^1].Volume, _fills[^1].Price));
        Assert.Equal("traded through the limit", _fills[^1].How);

        Market(last: 100.1m, total: 13_300); // 1,000 more: capped by the 20 left
        _channel.OnQuote(_quotes.Latest(Eric)!);
        Assert.Equal(20, _fills[^1].Volume);
        Assert.Equal(0, _channel.RestingCount);
        Assert.Equal(50, _book.Position(Eric));
    }

    [Fact]
    public async Task ARestingSell_FillsWhenATradePrintsAboveIt()
    {
        await BuySome(20);
        Market(last: 100.5m, total: 20_000);
        await Place(Order(OrderSide.Sell, 20, 100.8m));
        Market(last: 100.8m, total: 21_000);
        Assert.Equal(0, _channel.OnQuote(_quotes.Latest(Eric)!));
        Market(last: 100.9m, total: 21_100);
        Assert.Equal(1, _channel.OnQuote(_quotes.Latest(Eric)!));
        Assert.Equal((10L, 100.8m), (_fills[^1].Volume, _fills[^1].Price));
    }

    [Theory]
    [InlineData(9, 0)] // 10 % of 9 is below one share
    [InlineData(10, 1)]
    [InlineData(99, 9)]
    public async Task TheFillIsCappedAtTenPercentOfTheVolumeIncrement(int increment, long expected)
    {
        Market(last: 100.5m, total: 10_000);
        await Place(Order(OrderSide.Buy, 50, 100.3m));
        Market(last: 100.0m, total: 10_000 + increment);
        _channel.OnQuote(_quotes.Latest(Eric)!);
        Assert.Equal(expected, _fills.Sum(f => f.Volume));
    }

    [Fact]
    public async Task SeveralRestingOrders_ShareTheTenPercent_OldestFirst()
    {
        Market(last: 100.5m, total: 10_000);
        OrderSubmitResult first = await Place(Order(OrderSide.Buy, 6, 100.3m));
        OrderSubmitResult second = await Place(Order(OrderSide.Buy, 50, 100.2m));
        Market(last: 100.0m, total: 10_100); // 100 traded through both: 10 in all
        _channel.OnQuote(_quotes.Latest(Eric)!);
        Assert.Equal([(first.BrokerOrderId, 6L), (second.BrokerOrderId, 4L)], _fills.Select(f => ((OrderId?)f.BrokerOrderId, f.Volume)));
    }

    [Fact]
    public async Task VolumeTradedBeforeTheOrder_OrAcrossADailyReset_IsNotCounted()
    {
        _quotes.Set(Eric, _time.GetUtcNow(), 100.4m, 500, 100.6m, 700, 100.5m, totalVolume: null); // no volume yet
        await Place(Order(OrderSide.Buy, 50, 100.3m));
        Market(last: 100.0m, total: 50_000); // the first volume seen is only the baseline
        Assert.Equal(0, _channel.OnQuote(_quotes.Latest(Eric)!));

        Market(last: 100.0m, total: 100); // the day's volume restarted: a new baseline, not a negative increment
        Assert.Equal(0, _channel.OnQuote(_quotes.Latest(Eric)!));
        Market(last: 100.0m, total: 200);
        Assert.Equal(1, _channel.OnQuote(_quotes.Latest(Eric)!));
        Assert.Equal(10, _fills.Single().Volume);
    }

    [Fact]
    public async Task FillsAreWholeLots()
    {
        PaperOrderChannel lots = NewChannel(lot: 10);
        Market(last: 100.5m, total: 10_000, askVolume: 25);
        await Place(Order(OrderSide.Buy, 50, 101m), lots);
        Assert.Equal(20, _fills.Single().Volume);
    }

    [Fact]
    public async Task Courtage_IsChargedPerOrder_SoTheMinimumIsPaidOnce()
    {
        Market(last: 100.5m, total: 10_000);
        await Place(Order(OrderSide.Buy, 50, 100.3m));
        Market(last: 100.0m, total: 10_100); // 10 @ 100.3 = 1,003 SEK: 0.25 % = 2.51 (above the 1 SEK minimum)
        _channel.OnQuote(_quotes.Latest(Eric)!);
        Market(last: 100.0m, total: 10_150); // 5 more: 1,504.5 in all, 3.76 total, so 1.25 now
        _channel.OnQuote(_quotes.Latest(Eric)!);

        Assert.Equal([2.51m, 1.25m], _fills.Select(f => f.Courtage));
        Assert.Equal(decimal.Round(Mini.Courtage(15 * 100.3m), 2, MidpointRounding.AwayFromZero), _fills.Sum(f => f.Courtage));
        Assert.All(_fills, f => Assert.Equal(0m, f.FxFee)); // SEK instrument
        Assert.Equal(_fills.Sum(f => f.Courtage), _book.FeesPaid);
    }

    [Fact]
    public async Task TheMinimumCourtage_AppliesToASmallOrder()
    {
        Market(last: 100.5m, total: 10_000, ask: 10m, askVolume: 1_000);
        await Place(Order(OrderSide.Buy, 2, 10m));
        Assert.Equal(1m, _fills.Single().Courtage); // 0.25 % of 20 SEK is 0.05; the minimum is 1
    }

    [Fact]
    public async Task TheBrokerChecks_AreMade_BuyingPowerIncludingWorkingBuys_AndSharesHeld()
    {
        Market(last: 100.5m, total: 10_000);
        Assert.Contains("another account", (await Place(Order(OrderSide.Buy, 1, 100m, account: "12345678"))).Message, StringComparison.Ordinal);

        await Place(Order(OrderSide.Buy, 400, 100m)); // resting: reserves 40,000 + courtage
        Assert.Equal(40_100m, _book.Reserved);
        Assert.Equal(9_900m, _book.Snapshot().AvailableCash);
        OrderSubmitResult tooMuch = await Place(Order(OrderSide.Buy, 100, 100m));
        Assert.Equal(SubmitOutcome.Rejected, tooMuch.Outcome);
        Assert.Contains("buying power", tooMuch.Message, StringComparison.Ordinal);

        OrderSubmitResult shortSell = await Place(Order(OrderSide.Sell, 1, 101m));
        Assert.Contains("does not cover", shortSell.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelAndEndOfDay_ReleaseTheReservation()
    {
        Market(last: 100.5m, total: 10_000);
        OrderSubmitResult a = await Place(Order(OrderSide.Buy, 10, 100m));
        OrderSubmitResult b = await Place(Order(OrderSide.Buy, 10, 99m));
        Assert.True(_book.Reserved > 0);

        OrderSubmitResult cancelled = await _channel.CancelAsync(Cancel(a.BrokerOrderId!.Value), CancellationToken.None);
        Assert.Equal(SubmitOutcome.Accepted, cancelled.Outcome);
        Assert.Equal(SubmitOutcome.Rejected, (await _channel.CancelAsync(Cancel(a.BrokerOrderId.Value), CancellationToken.None)).Outcome);
        Assert.Empty(_ended); // a cancel is answered, not "ended"

        Assert.Equal(1, _channel.EndOfDay("day order expired"));
        Assert.Equal([(b.BrokerOrderId!.Value, "day order expired")], _ended);
        Assert.Equal(0m, _book.Reserved);
        Assert.Equal(0, _channel.RestingCount);
    }

    private static ApprovedCancel Cancel(OrderId id) => new(Guid.NewGuid(), new AccountId(PaperConfig.AccountId), id);

    private async Task BuySome(long quantity)
    {
        Market(last: 100.5m, total: 10_000, askVolume: 10_000);
        await Place(Order(OrderSide.Buy, quantity, 100.6m));
    }
}

public sealed class PaperBookTests : IDisposable
{
    private static readonly OrderbookId Eric = RiskEngineTests.Eric;
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now);
    private readonly SettableQuotes _quotes = new();
    private readonly PaperConfig _config = new("test-mini", 10_000m, new TimeOnly(9, 10));

    public void Dispose() => _dir.Dispose();

    private PaperBook Open(out IReadOnlyList<string> notes) => PaperBook.OpenOrCreate(_dir.Path, _config, _quotes, _time, out notes);

    [Fact]
    public void Fills_MoveCash_Position_CostBasis_AndRealisedPnl()
    {
        PaperBook book = Open(out _);
        book.ApplyFill(Guid.NewGuid(), Eric, "ERIC B", OrderSide.Buy, 10, 100m, 1m, 0m, _time.GetUtcNow());
        book.ApplyFill(Guid.NewGuid(), Eric, "ERIC B", OrderSide.Buy, 10, 110m, 1m, 0m, _time.GetUtcNow());
        Assert.Equal(10_000m - 2_102m, book.Cash);
        PaperPosition p = book.Positions.Single();
        Assert.Equal((20L, 2_102m), (p.Quantity, p.CostBasis));

        book.ApplyFill(Guid.NewGuid(), Eric, "ERIC B", OrderSide.Sell, 5, 120m, 1m, 0m, _time.GetUtcNow());
        Assert.Equal(599m - 525.5m, book.RealizedPnl); // proceeds 600 - 1, cost out 2,102 / 4
        Assert.Equal(15, book.Position(Eric));
        Assert.Equal(3m, book.FeesPaid);

        Assert.Throws<PaperBookException>(() => book.ApplyFill(Guid.NewGuid(), Eric, "ERIC B", OrderSide.Sell, 16, 120m, 0m, 0m, _time.GetUtcNow()));
        Assert.Throws<PaperBookException>(() => book.ApplyFill(Guid.NewGuid(), Eric, "ERIC B", OrderSide.Buy, 1_000, 120m, 0m, 0m, _time.GetUtcNow()));
        Assert.Equal(15, book.Position(Eric)); // refused fills change nothing
    }

    [Fact]
    public void TheBookSurvivesARestart_AndLogsEachFill()
    {
        PaperBook book = Open(out IReadOnlyList<string> first);
        Assert.Contains("new paper book", Assert.Single(first), StringComparison.Ordinal);
        book.ApplyFill(Guid.NewGuid(), Eric, "ERIC B", OrderSide.Buy, 10, 100m, 1m, 0m, _time.GetUtcNow());
        book.ApplyFill(Guid.NewGuid(), Eric, "ERIC B", OrderSide.Sell, 4, 105m, 1m, 0m, _time.GetUtcNow());

        PaperBook again = Open(out IReadOnlyList<string> second);
        Assert.Empty(second);
        Assert.Equal(book.Cash, again.Cash);
        Assert.Equal(book.RealizedPnl, again.RealizedPnl);
        Assert.Equal(book.Positions, again.Positions);
        Assert.Equal(2, File.ReadAllLines(_dir.File(PaperBook.FillsFileName)).Length);
        Assert.False(File.Exists(_dir.File(PaperBook.FileName + ".tmp")));
    }

    [Fact]
    public void AnExistingBook_WinsOverAChangedConfig_WithANote()
    {
        _ = Open(out _);
        PaperBook book = PaperBook.OpenOrCreate(_dir.Path, _config with { Cash = 99_000m }, _quotes, _time, out IReadOnlyList<string> notes);
        Assert.Equal(10_000m, book.StartingCash);
        Assert.Contains("the book is kept", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void ADamagedBook_IsRefused_NotReplaced()
    {
        File.WriteAllText(_dir.File(PaperBook.FileName), "{ not json");
        PaperBookException ex = Assert.Throws<PaperBookException>(() => Open(out _));
        Assert.Contains("Move it away", ex.Message, StringComparison.Ordinal);
        Assert.Equal("{ not json", File.ReadAllText(_dir.File(PaperBook.FileName)));
    }

    [Fact]
    public void Snapshot_MarksAtLastThenMidThenLastFill_AndFixesTheStartOfDayOncePerDay()
    {
        PaperBook book = Open(out _);
        book.ApplyFill(Guid.NewGuid(), Eric, "ERIC B", OrderSide.Buy, 10, 100m, 0m, 0m, _time.GetUtcNow());
        Assert.Equal(10_000m, book.Snapshot().AccountValue); // marked at the fill price, no quote yet

        _quotes.Set(Eric, _time.GetUtcNow(), 90m, 1, 92m, 1, last: null, totalVolume: 1);
        Assert.Equal(9_000m + 910m, book.Snapshot().AccountValue); // mid
        _quotes.Set(Eric, _time.GetUtcNow(), 90m, 1, 92m, 1, last: 80m, totalVolume: 1);
        AccountSnapshot s = book.Snapshot();
        Assert.Equal(9_800m, s.AccountValue); // last
        Assert.Equal(10_000m, s.StartOfDayValue); // fixed by the first snapshot today

        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(9_800m, book.Snapshot().StartOfDayValue);
        Assert.Equal(new AccountId(PaperConfig.AccountId), s.Account);
    }
}

public sealed class PaperConfigTests
{
    [Fact]
    public void TheCommittedConfig_Loads_AndNamesACostFileItsCashMayUse()
    {
        string config = Path.Combine(TradingConfigTests.RepoRoot(), "config");
        PaperConfig paper = PaperConfig.Load(Path.Combine(config, PaperConfig.FileName));
        CostModel costs = CostModel.Load(Path.Combine(config, $"costs.{paper.Costs}.json"));
        Assert.True(costs.EligibleBelowCapital is not { } cap || paper.Cash < cap, $"{paper.Cash} is not below the {costs.Name} capital limit");
        Assert.InRange(paper.DecisionTime, RiskLimits.AdrDefaults.WindowOpen, RiskLimits.AdrDefaults.HalfDayWindowClose);
    }

    [Theory]
    [InlineData("""{ "format": "qa-paper/2", "costs": "x", "cash": 1, "decision_time": "09:10" }""", "format must be qa-paper/1")]
    [InlineData("""{ "format": "qa-paper/1", "costs": "", "cash": 1, "decision_time": "09:10" }""", "costs must name")]
    [InlineData("""{ "format": "qa-paper/1", "costs": "x", "cash": 0, "decision_time": "09:10" }""", "cash must be > 0")]
    [InlineData("""{ "format": "qa-paper/1", "costs": "x", "cash": 1, "decision_time": "9:10" }""", "HH:mm")]
    [InlineData("""{ "format": "qa-paper/1", "costs": "x", "cash": 1 }""", "not a valid paper config")]
    public void BadFiles_AreRefused(string json, string expected)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File(PaperConfig.FileName), json);
        TradingConfigException ex = Assert.Throws<TradingConfigException>(() => PaperConfig.Load(dir.File(PaperConfig.FileName)));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>The whole Paper path: gateway, OMS, paper channel and book together.</summary>
public sealed class PaperPipelineTests : IDisposable
{
    private static readonly OrderbookId Eric = RiskEngineTests.Eric;
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now);
    private readonly SettableQuotes _quotes = new();
    private readonly AuditLog _audit;
    private readonly HaltController _halts;
    private readonly OrderManager _oms;
    private readonly PaperBook _book;
    private readonly PaperOrderChannel _channel;
    private readonly OrderGateway _gateway;

    public PaperPipelineTests()
    {
        _audit = new AuditLog(Path.Combine(_dir.Path, "audit"), _time);
        _halts = new HaltController(_audit, _time);
        _oms = new OrderManager(_audit, _halts, _time);
        _book = PaperBook.OpenOrCreate(Path.Combine(_dir.Path, "paper"), new PaperConfig("test-mini", 100_000m, new TimeOnly(9, 10)), _quotes, _time, out _);
        var instruments = new InstrumentCatalog([OrderPreparationTests.Spec()]);
        _channel = new PaperOrderChannel(_book, PaperOrderChannelTests.Mini, _quotes, instruments, _time);
        var env = new GatewayEnvironment
        {
            Mode = TradingMode.Paper,
            Instruments = instruments,
            Quotes = _quotes,
            Account = _book,
            Calendar = OrderGatewayTests.Calendar(),
            Universe = new Universe([new UniverseEntry(Eric, "ERIC B", "Ericsson B")]),
            AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { PaperConfig.AccountId },
            Fees = (p, spec) => _channel.EstimateFees(p.Value, spec.Currency),
            CourtageVerified = false,
        };
        _gateway = new OrderGateway(_channel, env, new PreTradeRiskEngine(RiskLimits.AdrDefaults), _oms, _halts, _audit, _time);
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _dir.Dispose();
    }

    private void Market(decimal last, decimal total, decimal askVolume = 700) =>
        _quotes.Set(Eric, _time.GetUtcNow(), 100.4m, 500, 100.6m, askVolume, last, total);

    private Task<SubmitResult> Buy(long qty, decimal limit) =>
        _gateway.SubmitAsync(new OrderIntent(Eric, "ERIC B", OrderSide.Buy, qty, limit, "test", limit, _time.GetUtcNow(), "test"), CancellationToken.None);

    [Fact]
    public async Task AMarketableBuy_IsFilledInTheOmsAndTheBook_AndTheAuditVerifies()
    {
        Market(100.5m, 10_000);
        SubmitResult r = await Buy(20, 100.67m); // rounds down to 100.6 = the ask

        Assert.Equal(SubmitStatus.Accepted, r.Status);
        Assert.Equal(OmsState.Filled, r.Order!.State);
        Assert.Equal(100.6m, r.Order.AverageFillPrice);
        Assert.Equal(20, _book.Position(Eric));
        Assert.Equal(100_000m - 2_012m - 5.03m, _book.Cash);
        Assert.Equal(5.03m, r.Order.Fees);
        Assert.True(AuditLog.Verify(Path.Combine(_dir.Path, "audit")).Valid);
    }

    [Fact]
    public async Task ARestingBuy_PartFills_ThenEndsAtTheClose()
    {
        Market(100.5m, 10_000);
        SubmitResult r = await Buy(20, 100.3m);
        Assert.Equal(OmsState.Working, r.Order!.State);

        Market(100.2m, 10_100);
        _channel.OnQuote(_quotes.Latest(Eric)!);
        Assert.Equal(OmsState.PartiallyFilled, r.Order.State);
        Assert.Equal(10, r.Order.FilledVolume);

        _channel.EndOfDay("day order expired at the close");
        Assert.Equal(OmsState.Cancelled, r.Order.State);
        Assert.Equal(0m, _book.Reserved);
        Assert.Equal(10, _book.Position(Eric));
        Assert.False(_halts.IsHalted);
    }

    [Fact]
    public async Task TheRiskEngine_SeesTheBooksCashAndPositions()
    {
        Market(100.5m, 10_000);
        await Buy(90, 100.3m); // resting: 9,027 reserved of 100,000
        _time.Advance(TimeSpan.FromSeconds(30));
        Market(100.5m, 10_000); // a fresh quote (R15)
        SubmitResult r = await Buy(99, 100.2m); // 9,920: fits R6 (10 %), and R7 counts the working buy: 18,947 < 20,000
        Assert.True(r.Status == SubmitStatus.Accepted, r.Message);
        _time.Advance(TimeSpan.FromSeconds(30));
        Market(100.5m, 10_000);
        SubmitResult tooBig = await Buy(20, 100.1m); // 18,947 + 2,002 > 20 % of the account
        Assert.Equal(["R7"], tooBig.Risk!.Failures.Select(f => f.Id));
    }

    [Fact]
    public async Task TheBookFile_IsValidJson_WithMaskedNothingToMask()
    {
        Market(100.5m, 10_000);
        await Buy(10, 100.6m);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir.Path, "paper", PaperBook.FileName)));
        Assert.Equal("qa-paper-book/1", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(PaperConfig.AccountId, doc.RootElement.GetProperty("account").GetString());
    }
}
