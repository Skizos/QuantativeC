using System.Text.Json.Nodes;
using QuantAnalyst.Avanza.Dto;
using QuantAnalyst.Avanza.Json;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza.Tests;

public sealed class DtoDriftTests
{
    private readonly CapturingLogger _log = new();
    private readonly AvanzaJson _json;

    public DtoDriftTests() => _json = new AvanzaJson(_log);

    [Fact]
    public void EveryProvisionalFixtureParsesStrictly()
    {
        Assert.True(Parse("session-info.json", AvanzaTierAContext.Default.SessionInfoDto).User.LoggedIn);
        Assert.Equal(2, Parse("accounts-overview.json", AvanzaTierAContext.Default.AccountsOverviewDto).Accounts.Count);
        Assert.Single(Parse("trading-accounts.json", AvanzaTierAContext.Default.ListTradingAccountDto));
        Assert.Single(Parse("positions.json", AvanzaTierAContext.Default.PositionsDto).WithOrderbook);
        Assert.Single(Parse("orders.json", AvanzaTierAContext.Default.OrdersDto).Orders);
        Assert.Equal(10, Parse("orderbook-5240.json", AvanzaTierAContext.Default.OrderbookDto).TickSizeList.TickSizeEntries.Count);
        Assert.Equal(2, Parse("marketdata-5240.json", AvanzaTierAContext.Default.MarketDataDto).OrderDepth.Levels.Count);
        Assert.Equal(2, Parse("search-eric.json", AvanzaTierBContext.Default.SearchResponseDto, DtoTier.B).Hits.Count);
        Assert.Equal(2, Parse("price-chart-5240.json", AvanzaTierBContext.Default.PriceChartDto, DtoTier.B).Ohlc.Count);
        Assert.Single(Parse("transactions.json", AvanzaTierBContext.Default.TransactionsDto, DtoTier.B).Transactions);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void TierA_UnknownFields_AreAllReportedWithPaths()
    {
        byte[] json = Fixtures.Mutate("accounts-overview.json", n =>
        {
            n["newTopLevel"] = 1;
            n["accounts"]![0]!["balance"]!["newNested"] = "x";
            n["accounts"]![1]!["weird key"] = true;
        });

        var ex = Assert.Throws<SchemaDriftException>(() =>
            _json.Deserialize(json, AvanzaTierAContext.Default.AccountsOverviewDto, "accounts-overview", AccountsOverviewDto.Version, DtoTier.A));

        // Document order: "newTopLevel" was appended after "loans".
        Assert.Equal(["$.accounts[0].balance.newNested", "$.accounts[1]['weird key']", "$.newTopLevel"], ex.Paths);
        Assert.True(ex.HaltsTrading);
        Assert.Equal(AccountsOverviewDto.Version, ex.DtoVersion);
        Assert.Contains("$.accounts[0].balance.newNested", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TierA_UnknownAndMissingFields_AreReportedTogetherInOnePass()
    {
        byte[] json = Fixtures.Mutate("trading-accounts.json", n =>
        {
            n[0]!["somethingNew"] = true;
            n[0]!.AsObject().Remove("accountType");
            n[0]!.AsObject().Remove("hasCredit");
        });

        var ex = Assert.Throws<SchemaDriftException>(() =>
            _json.Deserialize(json, AvanzaTierAContext.Default.ListTradingAccountDto, "trading-accounts", TradingAccountDto.Version, DtoTier.A));

        Assert.Equal(["$[0].somethingNew", "$[0].accountType", "$[0].hasCredit"], ex.Paths);
        Assert.Contains("1 unknown field(s)", ex.Detail, StringComparison.Ordinal);
        Assert.Contains("2 missing required field(s)", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TierB_MissingRequired_IsReportedByPath_UnknownOnlyLogged()
    {
        byte[] json = Fixtures.Mutate("price-chart-5240.json", n =>
        {
            n["newThing"] = 1;
            n["ohlc"]![1]!.AsObject().Remove("close");
        });
        var ex = Assert.Throws<SchemaDriftException>(() =>
            _json.Deserialize(json, AvanzaTierBContext.Default.PriceChartDto, "price-chart", PriceChartDto.Version, DtoTier.B));
        Assert.Equal(["$.ohlc[1].close"], ex.Paths);
    }

    [Fact]
    public void TierA_UnknownFieldInsideJsonElementMember_IsAccepted()
    {
        // featureSupport is a known-but-unused JsonElement member: its inner shape is not policed.
        byte[] json = Fixtures.Mutate("orderbook-5240.json", n => n["featureSupport"]!["somethingNew"] = true);
        _json.Deserialize(json, AvanzaTierAContext.Default.OrderbookDto, "orderbook", OrderbookDto.Version, DtoTier.A);
    }

    [Fact]
    public void TierA_MissingRequiredField_IsDrift()
    {
        byte[] json = Fixtures.Mutate("trading-accounts.json", n => n[0]!.AsObject().Remove("availableForPurchase"));
        var ex = Assert.Throws<SchemaDriftException>(() =>
            _json.Deserialize(json, AvanzaTierAContext.Default.ListTradingAccountDto, "trading-accounts", TradingAccountDto.Version, DtoTier.A));
        Assert.Contains("availableForPurchase", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TierA_NullInNonNullableMember_IsDrift()
    {
        byte[] json = Fixtures.Mutate("orders.json", n => n["orders"]![0]!["state"] = null);
        var ex = Assert.Throws<SchemaDriftException>(() =>
            _json.Deserialize(json, AvanzaTierAContext.Default.OrdersDto, "orders", OrdersDto.Version, DtoTier.A));
        Assert.Contains("$.orders[0].state", ex.Paths);
    }

    [Fact]
    public void TierA_WrongType_IsDriftWithPath()
    {
        byte[] json = Fixtures.Mutate("orderbook-5240.json", n => n["tickSizeList"]!["tickSizeEntries"]![2]!["tick"] = "0.001");
        var ex = Assert.Throws<SchemaDriftException>(() =>
            _json.Deserialize(json, AvanzaTierAContext.Default.OrderbookDto, "orderbook", OrderbookDto.Version, DtoTier.A));
        Assert.Equal(["$.tickSizeList.tickSizeEntries[2].tick"], ex.Paths);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>Logga in</body></html>")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    public void TierA_NonJsonOrWrongRoot_IsDrift(string body)
    {
        Assert.Throws<SchemaDriftException>(() =>
            _json.Deserialize(System.Text.Encoding.UTF8.GetBytes(body), AvanzaTierAContext.Default.PositionsDto, "positions", PositionsDto.Version, DtoTier.A));
    }

    [Fact]
    public void TierB_UnknownFields_AreLoggedOncePerRouteAndParsed()
    {
        byte[] json = Fixtures.Mutate("search-eric.json", n =>
        {
            n["hits"]![0]!["esgScore"] = 42;
            n["brandNew"] = new JsonObject();
        });

        SearchResponseDto first = _json.Deserialize(json, AvanzaTierBContext.Default.SearchResponseDto, "search", SearchResponseDto.Version, DtoTier.B);
        _json.Deserialize(json, AvanzaTierBContext.Default.SearchResponseDto, "search", SearchResponseDto.Version, DtoTier.B);

        Assert.Equal(2, first.Hits.Count);
        string warning = Assert.Single(_log.Lines);
        Assert.Contains("drift.warning route=search", warning, StringComparison.Ordinal);
        Assert.Contains("$.hits[0].esgScore", warning, StringComparison.Ordinal);
        Assert.Contains("$.brandNew", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void TierB_MissingRequiredField_IsDriftThatDoesNotHalt()
    {
        byte[] json = Fixtures.Mutate("search-eric.json", n => n["hits"]![1]!.AsObject().Remove("orderBookId"));
        var ex = Assert.Throws<SchemaDriftException>(() =>
            _json.Deserialize(json, AvanzaTierBContext.Default.SearchResponseDto, "search", SearchResponseDto.Version, DtoTier.B));
        Assert.False(ex.HaltsTrading);
        Assert.Contains("orderBookId", ex.Detail, StringComparison.Ordinal);
    }

    private T Parse<T>(string fixture, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, DtoTier tier = DtoTier.A) =>
        _json.Deserialize(Fixtures.Bytes(fixture), info, fixture, "test", tier);
}
