using System.Text.Json.Nodes;
using QuantAnalyst.Avanza.Dto;
using QuantAnalyst.Avanza.Json;
using QuantAnalyst.Avanza.Mapping;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Core.Orders;

namespace QuantAnalyst.Avanza.Tests;

public sealed class MapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly AvanzaJson _json = new(new CapturingLogger());

    [Fact]
    public void Accounts_PreferUserDefinedNameAndKeepDecimalsExact()
    {
        IReadOnlyList<Account> accounts = AvanzaMapper.ToAccounts(A("accounts-overview.json", AvanzaTierAContext.Default.AccountsOverviewDto));
        Assert.Equal(2, accounts.Count);
        Assert.Equal("***001", accounts[0].Id.ToString());
        Assert.Equal("Algo ISK", accounts[0].Name);
        Assert.Equal("AF", accounts[1].Name);
        Assert.Equal(12345.67m, accounts[0].Balance);
        Assert.Equal(142340.55m, accounts[0].TotalValue);
        Assert.Equal("SEK", accounts[0].Currency);
    }

    [Fact]
    public void TradingAccounts_MapAvailableForPurchase()
    {
        TradingAccount a = Assert.Single(AvanzaMapper.ToTradingAccounts(A("trading-accounts.json", AvanzaTierAContext.Default.ListTradingAccountDto)));
        Assert.Equal(new AccountId("9990001"), a.Id);
        Assert.Equal(12345.67m, a.AvailableForPurchase);
        Assert.True(a.IsTradable);
        Assert.Equal(new CurrencyBalance("SEK", 12345.67m), Assert.Single(a.CurrencyBalances));
    }

    [Fact]
    public void Positions_IncludeFundsWithFractionalVolumeAndFilterByAccount()
    {
        PositionsDto dto = A("positions.json", AvanzaTierAContext.Default.PositionsDto);
        PortfolioSnapshot all = AvanzaMapper.ToPortfolio(dto, null, Now);
        Assert.Equal(2, all.Positions.Count);
        Assert.Equal(2, all.Cash.Count);

        Position eric = all.Positions[0];
        Assert.Equal(new OrderbookId("5240"), eric.OrderbookId);
        Assert.Equal(150m, eric.Volume);
        Assert.Equal(65.1234m, eric.AverageAcquiredPrice);
        Assert.Equal(70.86m, eric.LastPrice);
        Assert.Equal(12.3456m, all.Positions[1].Volume);
        Assert.Null(all.Positions[1].OrderbookId);

        PortfolioSnapshot isk = AvanzaMapper.ToPortfolio(dto, new AccountId("9990001"), Now);
        Assert.Single(isk.Positions);
        Assert.Equal(12345.67m, Assert.Single(isk.Cash).Balance);
    }

    [Fact]
    public void Orders_MapSideDatesAndVolumes()
    {
        BrokerOrder o = Assert.Single(AvanzaMapper.ToOrders(A("orders.json", AvanzaTierAContext.Default.OrdersDto)));
        Assert.Equal(OrderSide.Buy, o.Side);
        Assert.Equal(69.5m, o.Price);
        Assert.Equal(10m, o.Volume);
        Assert.Equal(new DateOnly(2026, 9, 25), o.ValidUntil);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 7, 15, 2, 345, TimeSpan.Zero), o.CreatedUtc);
        Assert.Equal("***001", o.Account.ToString());
    }

    [Fact]
    public void Orders_UnknownSideIsDriftWithPath()
    {
        OrdersDto dto = A("orders.json", AvanzaTierAContext.Default.OrdersDto, n => n["orders"]![0]!["side"] = "SHORT");
        var ex = Assert.Throws<SchemaDriftException>(() => AvanzaMapper.ToOrders(dto));
        Assert.Equal(["$.orders[0].side"], ex.Paths);
        Assert.True(ex.HaltsTrading);
    }

    [Fact]
    public void Orders_FractionalVolumeIsDrift()
    {
        OrdersDto dto = A("orders.json", AvanzaTierAContext.Default.OrdersDto, n => n["orders"]![0]!["volume"] = 1.5);
        Assert.Equal(["$.orders[0].volume"], Assert.Throws<SchemaDriftException>(() => AvanzaMapper.ToOrders(dto)).Paths);
    }

    [Theory]
    [InlineData("25/09/2026")]
    [InlineData("2026-09-25T10:00:00")] // no offset: we do not guess a time zone
    public void Orders_UnparseableCreatedIsDrift(string created)
    {
        OrdersDto dto = A("orders.json", AvanzaTierAContext.Default.OrdersDto, n => n["orders"]![0]!["created"] = created);
        Assert.Equal(["$.orders[0].created"], Assert.Throws<SchemaDriftException>(() => AvanzaMapper.ToOrders(dto)).Paths);
    }

    [Fact]
    public void Orderbook_BuildsTickTableAndLot()
    {
        InstrumentTradingParams p = AvanzaMapper.ToTradingParams(A("orderbook-5240.json", AvanzaTierAContext.Default.OrderbookDto), Now);
        Assert.Equal("ERIC B", p.TickerSymbol);
        Assert.Equal(0.02m, p.TickSizes.TickAt(70.86m));
        Assert.Equal(70.84m, p.TickSizes.RoundForOrder(70.853m, OrderSide.Buy));
        Assert.Equal(70.86m, p.TickSizes.RoundForOrder(70.853m, OrderSide.Sell));
        Assert.Equal(1, p.VolumeFactor);
        Assert.Equal(1, p.TradingUnit);
        Assert.Equal(new DateOnly(2026, 10, 23), p.MaxValidUntil);
        Assert.Equal(Now, p.KnownAtUtc);
        Assert.Null(p.OrderbookStatus); // absent in the live payload (owner's probe 2026-09-25)
    }

    [Fact]
    public void Orderbook_OverlappingTickTableIsDrift()
    {
        OrderbookDto dto = A("orderbook-5240.json", AvanzaTierAContext.Default.OrderbookDto,
            n => n["tickSizeList"]!["tickSizeEntries"]![1]!["min"] = 0.1);
        var ex = Assert.Throws<SchemaDriftException>(() => AvanzaMapper.ToTradingParams(dto, Now));
        Assert.Equal(["$.tickSizeList.tickSizeEntries"], ex.Paths);
    }

    [Fact]
    public void Orderbook_FractionalLotIsDrift()
    {
        OrderbookDto dto = A("orderbook-5240.json", AvanzaTierAContext.Default.OrderbookDto, n => n["tradingUnit"] = 0.5);
        Assert.Equal(["$.tradingUnit"], Assert.Throws<SchemaDriftException>(() => AvanzaMapper.ToTradingParams(dto, Now)).Paths);
    }

    [Fact]
    public void MarketData_MapsQuoteDepthAndEpochTimestamps()
    {
        MarketSnapshot s = AvanzaMapper.ToMarketSnapshot(new OrderbookId("5240"), A("marketdata-5240.json", AvanzaTierAContext.Default.MarketDataDto), Now);
        Assert.Equal(70.84m, s.Bid);
        Assert.Equal(70.86m, s.Ask);
        Assert.Equal(70.86m, s.Last);
        Assert.Equal(3456789m, s.TotalVolumeTraded);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1790337598123), s.TimeOfLastUtc);
        Assert.Equal(2, s.Depth.Count);
        Assert.Equal(new DepthLevel(70.82m, 0m, null, 0m), s.Depth[1]); // "0.00" string volume, empty sell side
        Assert.Equal(Now, s.RetrievedAtUtc);
    }

    [Fact]
    public void MarketData_AcceptsIsoTimestampWithOffset()
    {
        MarketDataDto dto = A("marketdata-5240.json", AvanzaTierAContext.Default.MarketDataDto,
            n => n["quote"]!["timeOfLast"] = "2026-09-25T15:59:58.123+02:00");
        MarketSnapshot s = AvanzaMapper.ToMarketSnapshot(new OrderbookId("5240"), dto, Now);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 13, 59, 58, 123, TimeSpan.Zero), s.TimeOfLastUtc);
    }

    [Fact]
    public void MarketData_EmptyQuoteValuesBecomeNull()
    {
        MarketDataDto dto = A("marketdata-5240.json", AvanzaTierAContext.Default.MarketDataDto, n =>
        {
            n["quote"]!["buy"] = 0;
            n["quote"]!.AsObject().Remove("sell");
            n["quote"]!["updated"] = null;
        });
        MarketSnapshot s = AvanzaMapper.ToMarketSnapshot(new OrderbookId("5240"), dto, Now);
        Assert.Null(s.Bid);
        Assert.Null(s.Ask);
        Assert.Null(s.UpdatedUtc);
    }

    [Fact]
    public void Search_ParsesStringPrices()
    {
        IReadOnlyList<InstrumentSearchHit> hits = AvanzaMapper.ToSearchHits(B("search-eric.json", AvanzaTierBContext.Default.SearchResponseDto));
        Assert.Equal(new OrderbookId("5240"), hits[0].OrderbookId);
        Assert.Equal(70.86m, hits[0].LastPrice);
        Assert.Null(hits[1].LastPrice); // "null" string
        Assert.Equal("STOCK", hits[0].Type);
    }

    [Fact]
    public void Chart_MapsBarsAndIntegralFloatVolumes()
    {
        IReadOnlyList<Bar> bars = AvanzaMapper.ToBars(B("price-chart-5240.json", AvanzaTierBContext.Default.PriceChartDto));
        Assert.Equal(2, bars.Count);
        Assert.Equal(3456789L, bars[1].Volume);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1790287200000), bars[1].TimestampUtc);
        Assert.Equal(70.86m, bars[1].Close);
    }

    [Fact]
    public void Transactions_MapDatesAndAmounts()
    {
        BrokerTransaction t = Assert.Single(AvanzaMapper.ToTransactions(B("transactions.json", AvanzaTierBContext.Default.TransactionsDto)));
        Assert.Equal(new DateOnly(2026, 9, 24), t.Date);
        Assert.Equal(-9768.51m, t.Amount);
        Assert.Equal(39m, t.Commission);
        Assert.Equal("SE0000108656", t.Isin);
    }

    private T A<T>(string fixture, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, Action<JsonNode>? mutate = null) =>
        _json.Deserialize(mutate is null ? Fixtures.Bytes(fixture) : Fixtures.Mutate(fixture, mutate), info, fixture, "test", DtoTier.A);

    private T B<T>(string fixture, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        _json.Deserialize(Fixtures.Bytes(fixture), info, fixture, "test", DtoTier.B);
}
