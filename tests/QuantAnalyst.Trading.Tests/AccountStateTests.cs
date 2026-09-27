using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Core.Orders;
using QuantAnalyst.Trading.Accounts;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

/// <summary>A read gateway that serves the accounts and positions the test sets, and counts the reads.</summary>
internal sealed class FakeAccountGateway : IBrokerGateway
{
    public List<TradingAccount> Accounts { get; set; } = [AccountStateTests.Isk()];

    public List<Position> Positions { get; set; } = [AccountStateTests.EricPosition()];

    public List<CashPosition> Cash { get; set; } = [new(AccountStateTests.IskId, 12_345.67m, "SEK"), new(AccountStateTests.OtherId, 500m, "SEK")];

    public Exception? Failure { get; set; }

    public int AccountReads { get; private set; }

    public int PositionReads { get; private set; }

    public Task<IReadOnlyList<TradingAccount>> GetTradingAccountsAsync(CancellationToken ct)
    {
        AccountReads++;
        return Failure is null ? Task.FromResult<IReadOnlyList<TradingAccount>>([.. Accounts]) : Task.FromException<IReadOnlyList<TradingAccount>>(Failure);
    }

    public Task<PortfolioSnapshot> GetPositionsAsync(AccountId? account, CancellationToken ct)
    {
        PositionReads++;
        return Task.FromResult(new PortfolioSnapshot([.. Positions], [.. Cash], RiskEngineTests.Now));
    }

    public Task<SessionHealth> GetSessionHealthAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<BrokerDeal>> GetDealsAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<BrokerTransaction>> GetTransactionsAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<InstrumentSearchHit>> SearchStocksAsync(string query, int maxHits, CancellationToken ct) => throw new NotSupportedException();

    public Task<InstrumentTradingParams> GetTradingParamsAsync(OrderbookId id, CancellationToken ct) => throw new NotSupportedException();

    public Task<MarketSnapshot> GetMarketSnapshotAsync(OrderbookId id, CancellationToken ct) => throw new NotSupportedException();

    public Task<PriceHistory> GetPriceHistoryAsync(OrderbookId id, ChartPeriod period, ChartResolution? resolution, CancellationToken ct) => throw new NotSupportedException();

    public IAsyncEnumerable<MarketStreamEvent> StreamOrderDepthAsync(OrderbookId id, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>
/// Phase 7 step 2: R1 from <c>AVANZA__ALLOWEDACCOUNTIDS</c> and the live account state. The values are the Phase 3
/// provisional fixtures' (one ISK <c>9990001</c> with 12,345.67 SEK and 150 ERIC B worth 10,629 SEK); the recorded
/// 2026-09-25 payloads run through the real gateway in the Avanza tests.
/// </summary>
public sealed class AccountStateTests : IDisposable
{
    internal static readonly AccountId IskId = new("9990001");
    internal static readonly AccountId OtherId = new("9990002");

    private static readonly OrderbookId Eric = RiskEngineTests.Eric;
    private static readonly PreTradeRiskEngine Engine = new(RiskLimits.AdrDefaults);

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now);
    private readonly SettableQuotes _quotes = new();
    private readonly FakeAccountGateway _gateway = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static TradingAccount Isk(
        AccountId? id = null, string type = AccountAllowlist.IskType, bool tradable = true, bool credit = false, bool? discretionary = false,
        decimal available = 12_345.67m, decimal? withoutCredit = null) =>
        new(id ?? IskId, "ISK", type, available, tradable, credit, discretionary, [new CurrencyBalance("SEK", 12_345.67m)], withoutCredit);

    internal static Position EricPosition(decimal volume = 150m, decimal value = 10_629.0m, AccountId? account = null) =>
        new(account ?? IskId, RiskEngineTests.Eric, "Ericsson B", "SE0000108656", "STOCK", "SEK", volume, value, 68.5m, 10_275m, 70.86m);

    public void Dispose() => _dir.Dispose();

    private GatewayAccountState State(TimeSpan? maxAge = null) => new(_gateway, IskId, _quotes, _time, _dir.Path, maxAge);

    // ---- R1: the allowlist -------------------------------------------------------------------------

    [Theory]
    [InlineData("9990001")]
    [InlineData(" 9990001 ")]
    [InlineData("9990001,")]
    public void TheOneIsk_IsAllowed_AndBecomesR1sInput(string raw)
    {
        AllowlistResult r = AccountAllowlist.Resolve(raw, [Isk(), Isk(OtherId, "AKTIEFONDKONTO")]);

        Assert.True(r.Allowed);
        Assert.Empty(r.Problems);
        Assert.Equal(["9990001"], r.AllowedAccountIds);
        Assert.Equal("Live trading account (R1, AVANZA__ALLOWEDACCOUNTIDS): ***001, ISK, tradable, not managed, no credit: OK.", r.Describe());

        RiskCheckResult r1 = Engine.Evaluate(RiskEngineTests.Buy(), RiskEngineTests.Baseline(TradingMode.Confirm) with
        {
            Account = IskId,
            AllowedAccountIds = r.AllowedAccountIds,
        })["R1"];
        Assert.True(r1.Passed);
        Assert.Equal("***001", r1.Observed);
    }

    public static TheoryData<string, string?, TradingAccount[], string> Refusals() => new()
    {
        { "unset", null, [Isk()], "AVANZA__ALLOWEDACCOUNTIDS is not set" },
        { "empty", "", [Isk()], "is not set" },
        { "blank", " ,; ", [Isk()], "is not set" },
        { "two ids, comma", "9990001,9990002", [Isk(), Isk(OtherId)], "names 2 accounts (***001, ***002); exactly one may trade" },
        { "two ids, semicolon", "9990001;9990002", [Isk(), Isk(OtherId)], "names 2 accounts" },
        { "two ids, space", "9990001 9990002", [Isk(), Isk(OtherId)], "names 2 accounts" },
        { "the same id twice", "9990001,9990001", [Isk()], "names 2 accounts" },
        { "unknown", "9990009", [Isk(), Isk(OtherId)], "account ***009 from AVANZA__ALLOWEDACCOUNTIDS is not among your trading accounts (***001, ***002)" },
        { "no accounts", "9990001", [], "is not among your trading accounts (none)" },
        { "not tradable", "9990001", [Isk(tradable: false)], "account ***001 is not tradable at Avanza" },
        { "not an ISK", "9990001", [Isk(type: "AKTIEFONDKONTO")], "account ***001 is a AKTIEFONDKONTO, not an ISK" },
        { "discretionary", "9990001", [Isk(discretionary: true)], "account ***001 is a discretionary (managed) account" },
        { "discretionary unknown", "9990001", [Isk(discretionary: null)], "unknown counts as managed" },
        { "credit", "9990001", [Isk(credit: true)], "account ***001 has credit; live trading needs an account without credit (R8: no leverage)" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void EveryRefusal_AllowsNothing_AndIsMasked(string label, string? raw, TradingAccount[] accounts, string expected)
    {
        AllowlistResult r = AccountAllowlist.Resolve(raw, accounts);

        Assert.False(r.Allowed, label);
        Assert.Empty(r.AllowedAccountIds);
        string text = r.Describe();
        Assert.StartsWith("Live trading account (R1, AVANZA__ALLOWEDACCOUNTIDS): refused. ", text, StringComparison.Ordinal);
        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.DoesNotContain("999000", text, StringComparison.Ordinal); // never a full id

        RiskCheckResult r1 = Engine.Evaluate(RiskEngineTests.Buy(), RiskEngineTests.Baseline(TradingMode.Confirm) with
        {
            Account = IskId,
            AllowedAccountIds = r.AllowedAccountIds,
        })["R1"];
        Assert.False(r1.Passed);
    }

    [Fact]
    public void EveryProblemOfTheAccount_IsListed()
    {
        AllowlistResult r = AccountAllowlist.Resolve("9990001", [Isk(type: "KAPITALFORSAKRING", tradable: false, credit: true, discretionary: null)]);

        Assert.False(r.Allowed);
        Assert.NotNull(r.Account);
        Assert.Equal(4, r.Problems.Count);
    }

    [Fact]
    public void FromEnvironment_ReadsTheVariable()
    {
        var asked = new List<string>();
        AllowlistResult r = AccountAllowlist.FromEnvironment([Isk()], name =>
        {
            asked.Add(name);
            return "9990001";
        });

        Assert.Equal(["AVANZA__ALLOWEDACCOUNTIDS"], asked);
        Assert.True(r.Allowed);
    }

    // ---- The live account state -------------------------------------------------------------------

    [Fact]
    public async Task TheFixtureAccount_GivesCashPositionsAndValue()
    {
        AccountSnapshot s = await State().GetAsync(Ct);

        Assert.Equal(IskId, s.Account);
        Assert.Equal(12_345.67m, s.AvailableCash);
        Assert.Equal(12_345.67m + 10_629.0m, s.AccountValue); // cash + ERIC at the broker's value (no quote yet)
        Assert.Equal(150, s.Positions[Eric]);
        Assert.Equal(10_629.0m, s.PositionValues[Eric]);
        Assert.Equal(s.AccountValue, s.StartOfDayValue);
        Assert.Equal(1, _gateway.AccountReads);
        Assert.Equal(1, _gateway.PositionReads);
    }

    [Fact]
    public async Task Positions_AreMarkedAtTheComposedQuote_LastElseMid()
    {
        GatewayAccountState state = State();
        _quotes.Set(Eric, RiskEngineTests.Now, bid: 71.00m, 100, ask: 71.10m, 100, last: 72.00m, totalVolume: 1_000);
        Assert.Equal(150 * 72.00m, (await state.GetAsync(Ct)).PositionValues[Eric]);

        _quotes.Set(Eric, RiskEngineTests.Now, bid: 71.00m, 100, ask: 71.10m, 100, last: null, totalVolume: 1_000);
        AccountSnapshot s = await state.GetAsync(Ct);
        Assert.Equal(150 * 71.05m, s.PositionValues[Eric]);
        Assert.Equal(12_345.67m + (150 * 71.05m), s.AccountValue);
    }

    [Fact]
    public async Task FundsAndUnlistedHoldings_CountTowardsTheValue_ButAreNotSellablePositions()
    {
        var fund = new OrderbookId("878733");
        var usd = new OrderbookId("238449");
        _gateway.Positions =
        [
            EricPosition(),
            new(IskId, fund, "Global Index", "SE0000000001", "FUND", "SEK", 12.3456m, 2_000m, null, null, 162m),
            new(IskId, usd, "Apple", "US0378331005", "STOCK", "USD", 2m, 4_300m, null, null, 215m),
            new(IskId, null, "Unlisted AB", null, "STOCK", "SEK", 10m, 100m, null, null, null),
            new(IskId, new OrderbookId("1"), "Sold out", null, "STOCK", "SEK", 0m, 0m, null, null, null),
            EricPosition(volume: 1_000m, value: 70_000m, account: OtherId),
        ];
        _quotes.Set(fund, RiskEngineTests.Now, null, 0, null, 0, last: 170m, totalVolume: null);
        _quotes.Set(usd, RiskEngineTests.Now, null, 0, null, 0, last: 230m, totalVolume: null);

        AccountSnapshot s = await State().GetAsync(Ct);

        Assert.Equal([(Eric, 150L), (usd, 2L)], s.Positions.Select(p => (p.Key, p.Value)).OrderBy(p => p.Key.Value, StringComparer.Ordinal).Reverse());
        Assert.Equal(12.3456m * 170m, s.PositionValues[fund]); // the fund at its quote, value only
        Assert.Equal(4_300m, s.PositionValues[usd]); // not SEK: the broker's SEK value, not 2 x 230
        Assert.Equal(12_345.67m + 10_629.0m + (12.3456m * 170m) + 4_300m + 100m, s.AccountValue);
    }

    [Fact]
    public async Task AvailableCash_NeverIncludesCredit()
    {
        _gateway.Accounts = [Isk(available: 15_000m, withoutCredit: 12_000m)];
        Assert.Equal(12_000m, (await State().GetAsync(Ct)).AvailableCash);

        _gateway.Accounts = [Isk(available: 11_000m, withoutCredit: 12_000m)];
        Assert.Equal(11_000m, (await State().GetAsync(Ct)).AvailableCash);
    }

    [Fact]
    public async Task Cash_FallsBackToTheTradingAccount_OnlyWhenThePositionsReadHasNone()
    {
        _gateway.Cash = [new(OtherId, 500m, "SEK")];
        Assert.Equal(12_345.67m + 10_629.0m, (await State().GetAsync(Ct)).AccountValue);

        _gateway.Cash = [new(IskId, 0m, "SEK")];
        Assert.Equal(10_629.0m, (await State(TimeSpan.Zero).GetAsync(Ct)).AccountValue);
    }

    [Fact]
    public async Task TheBroker_IsReadAgain_AfterTheMaximumAge_OrAnInvalidate()
    {
        GatewayAccountState state = State();
        await state.GetAsync(Ct);
        _time.Advance(TimeSpan.FromSeconds(29));
        await state.GetAsync(Ct);
        Assert.Equal((1, 1), (_gateway.AccountReads, _gateway.PositionReads));

        _time.Advance(TimeSpan.FromSeconds(1));
        await state.GetAsync(Ct);
        Assert.Equal((2, 2), (_gateway.AccountReads, _gateway.PositionReads));

        _gateway.Cash = [new(IskId, 2_345.67m, "SEK")];
        state.Invalidate();
        Assert.Equal(2_345.67m + 10_629.0m, (await state.GetAsync(Ct)).AccountValue);
        Assert.Equal((3, 3), (_gateway.AccountReads, _gateway.PositionReads));
        Assert.Equal(RiskEngineTests.Now.AddSeconds(30), state.ReadAtUtc);
    }

    public static TheoryData<TradingAccount[], string> Changes() => new()
    {
        { [Isk(OtherId)], "account ***001 is no longer among the trading accounts; live orders stop (R1)." },
        { [Isk(discretionary: true)], "account ***001 may no longer trade (R1): account ***001 is a discretionary (managed) account." },
        { [Isk(credit: true)], "has credit" },
        { [Isk(tradable: false)], "is not tradable" },
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task EveryRead_ChecksR1Again(TradingAccount[] accounts, string expected)
    {
        GatewayAccountState state = State();
        await state.GetAsync(Ct);
        _gateway.Accounts = [.. accounts];
        state.Invalidate();

        AccountStateException ex = await Assert.ThrowsAsync<AccountStateException>(() => state.GetAsync(Ct));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("9990001", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, _gateway.PositionReads); // no positions read for a refused account
    }

    [Fact]
    public async Task ImpossibleHoldings_Refuse()
    {
        _gateway.Positions = [EricPosition(volume: -5m)];
        Assert.Contains("an ISK can't be short", (await Assert.ThrowsAsync<AccountStateException>(() => State().GetAsync(Ct))).Message, StringComparison.Ordinal);

        _gateway.Positions = [EricPosition()];
        _gateway.Cash = [new(IskId, 12_345.67m, "SEK"), new(IskId, 10m, "USD")];
        Assert.Contains("holds cash in USD", (await Assert.ThrowsAsync<AccountStateException>(() => State().GetAsync(Ct))).Message, StringComparison.Ordinal);

        _gateway.Cash = [];
        _gateway.Accounts = [Isk() with { CurrencyBalances = [] }];
        Assert.Contains("SEK cash balance", (await Assert.ThrowsAsync<AccountStateException>(() => State().GetAsync(Ct))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrokerFailures_Propagate()
    {
        _gateway.Failure = new SessionExpiredException("trading-accounts", 401);
        await Assert.ThrowsAsync<SessionExpiredException>(() => State().GetAsync(Ct));
    }

    // ---- R19's start of the day ---------------------------------------------------------------------

    [Fact]
    public async Task TheStartOfTheDay_IsTheFirstSnapshot_AndSurvivesARestart()
    {
        GatewayAccountState state = State(TimeSpan.Zero);
        decimal start = (await state.GetAsync(Ct)).StartOfDayValue;
        Assert.Equal(22_974.67m, start);

        _gateway.Cash = [new(IskId, 12_000m, "SEK")]; // a loss during the day
        AccountSnapshot later = await state.GetAsync(Ct);
        Assert.Equal((22_629.0m, start), (later.AccountValue, later.StartOfDayValue));

        // A restart the same day must not reset the daily loss stop.
        AccountSnapshot restarted = await State(TimeSpan.Zero).GetAsync(Ct);
        Assert.Equal((22_629.0m, start), (restarted.AccountValue, restarted.StartOfDayValue));

        // The next Stockholm day starts again.
        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(22_629.0m, (await State(TimeSpan.Zero).GetAsync(Ct)).StartOfDayValue);

        string record = await File.ReadAllTextAsync(state.StartOfDayPath, Ct);
        Assert.Contains("\"account\": \"***001\"", record, StringComparison.Ordinal);
        Assert.Contains("\"date\": \"2026-09-29\"", record, StringComparison.Ordinal);
        Assert.DoesNotContain("9990001", record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherAccountsRecord_IsNotThisAccountsStartOfTheDay()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir.Path, GatewayAccountState.FileName),
            """{ "format": "qa-live-start-of-day/1", "account": "***777", "date": "2026-09-28", "value": 99999, "saved_utc": "2026-09-28T07:00:00+00:00" }""", Ct);

        Assert.Equal(22_974.67m, (await State().GetAsync(Ct)).StartOfDayValue);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "format": "something-else/9", "account": "***001", "date": "2026-09-28", "value": 1, "saved_utc": "2026-09-28T07:00:00+00:00" }""")]
    public async Task AnUnreadableRecord_Refuses_RatherThanForgetTheDaysLoss(string content)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir.Path, GatewayAccountState.FileName), content, Ct);

        AccountStateException ex = await Assert.ThrowsAsync<AccountStateException>(() => State().GetAsync(Ct));
        Assert.Contains(GatewayAccountState.FileName, ex.Message, StringComparison.Ordinal);
    }
}
