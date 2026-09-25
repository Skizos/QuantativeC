using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using QuantAnalyst.Avanza.Dto;
using QuantAnalyst.Avanza.Json;
using QuantAnalyst.Avanza.Mapping;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// The owner's sanitized live recordings (<c>recordings/fixtures/avanza/yyyy-MM-dd/</c>) must parse strictly and
/// map, on every build. When Avanza changes a payload, a fresh recording fails here first: the drift is
/// visible in CI, not in trading.
/// </summary>
public sealed class RecordedFixtureTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "fixtures", "avanza");

    public static TheoryData<string> RecordedFolders()
    {
        var data = new TheoryData<string>();
        foreach (string dir in Directory.GetDirectories(Root).Select(Path.GetFileName).Where(d => DateOnly.TryParse(d, out _)).Order(StringComparer.Ordinal)!)
        {
            data.Add(dir!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RecordedFolders))]
    public void EveryRecordedRouteParsesStrictlyAndMaps(string folder)
    {
        var log = new CapturingLogger();
        var json = new AvanzaJson(log);
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        int checkedFiles = 0;
        foreach (string file in Directory.GetFiles(Path.Combine(Root, folder), "*.json").Order(StringComparer.Ordinal).Where(f => !IsStreamRecording(f)))
        {
            (string route, int status, JsonElement body) = Load(file);
            if (status is < 200 or >= 300)
            {
                continue;
            }

            checkedFiles++;
            _ = route switch
            {
                "session-info" => (object)AvanzaMapper.ToSessionHealth(Parse(json, body, AvanzaTierAContext.Default.SessionInfoDto, route, SessionInfoDto.Version, DtoTier.A), now),
                "accounts-overview" => AvanzaMapper.ToAccounts(Parse(json, body, AvanzaTierAContext.Default.AccountsOverviewDto, route, AccountsOverviewDto.Version, DtoTier.A)),
                "trading-accounts" => AvanzaMapper.ToTradingAccounts(Parse(json, body, AvanzaTierAContext.Default.ListTradingAccountDto, route, TradingAccountDto.Version, DtoTier.A)),
                "positions" => AvanzaMapper.ToPortfolio(Parse(json, body, AvanzaTierAContext.Default.PositionsDto, route, PositionsDto.Version, DtoTier.A), null, now),
                "orders" => AvanzaMapper.ToOrders(Parse(json, body, AvanzaTierAContext.Default.OrdersDto, route, OrdersDto.Version, DtoTier.A)),
                "deals" => AvanzaMapper.ToDeals(Parse(json, body, AvanzaTierAContext.Default.DealsDto, route, DealsDto.Version, DtoTier.A)),
                "orderbook" => AvanzaMapper.ToTradingParams(Parse(json, body, AvanzaTierAContext.Default.OrderbookDto, route, OrderbookDto.Version, DtoTier.A), now),
                "marketdata" => AvanzaMapper.ToMarketSnapshot(new OrderbookId("1"), Parse(json, body, AvanzaTierAContext.Default.MarketDataDto, route, MarketDataDto.Version, DtoTier.A), now),
                "search" => AvanzaMapper.ToSearchHits(Parse(json, body, AvanzaTierBContext.Default.SearchResponseDto, route, SearchResponseDto.Version, DtoTier.B)),
                "price-chart" => AvanzaMapper.ToBars(Parse(json, body, AvanzaTierBContext.Default.PriceChartDto, route, PriceChartDto.Version, DtoTier.B)),
                "transactions" => AvanzaMapper.ToTransactions(Parse(json, body, AvanzaTierBContext.Default.TransactionsDto, route, TransactionsDto.Version, DtoTier.B)),
                _ => null!, // authentication routes: structure-only recordings, checked below
            };
        }

        Assert.True(checkedFiles >= 10, $"only {checkedFiles} data files in {folder}");
        Assert.Empty(log.Lines); // no Tier B drift warnings either
    }

    [Fact]
    public void Recording20260925_ValuesMapAsExpected()
    {
        string dir = Path.Combine(Root, "2026-09-25");
        var json = new AvanzaJson(new CapturingLogger());
        JsonElement Body(string route) => Directory.GetFiles(dir, $"*-{route}.json").Order(StringComparer.Ordinal).Where(f => !IsStreamRecording(f)).Select(Load).First(x => x.Route == route).Body;

        IReadOnlyList<Account> accounts = AvanzaMapper.ToAccounts(Parse(json, Body("accounts-overview"), AvanzaTierAContext.Default.AccountsOverviewDto, "a", "v", DtoTier.A));
        Assert.Equal(3, accounts.Count);
        Assert.Contains(accounts, a => a.Type == "INVESTERINGSSPARKONTO");

        IReadOnlyList<TradingAccount> trading = AvanzaMapper.ToTradingAccounts(Parse(json, Body("trading-accounts"), AvanzaTierAContext.Default.ListTradingAccountDto, "t", "v", DtoTier.A));
        Assert.All(trading, t => Assert.False(t.IsDiscretionary));

        InstrumentTradingParams eric = AvanzaMapper.ToTradingParams(Parse(json, Body("orderbook"), AvanzaTierAContext.Default.OrderbookDto, "o", "v", DtoTier.A), DateTimeOffset.UnixEpoch);
        Assert.Equal("ERIC B", eric.TickerSymbol);
        Assert.Equal(17, eric.TickSizes.Bands.Count);
        Assert.Null(eric.OrderbookStatus);
        Assert.Equal(0.02m, eric.TickSizes.TickAt(94.96m));
        Assert.Equal(94.94m, eric.TickSizes.RoundForOrder(94.955m, OrderSide.Buy));
        Assert.Equal(94.96m, eric.TickSizes.RoundForOrder(94.955m, OrderSide.Sell));

        // Naive timestamps are Stockholm local time: timeOfLast "17:29:40" == receivedTime (epoch ms) 15:29:40Z.
        MarketSnapshot m = AvanzaMapper.ToMarketSnapshot(eric.OrderbookId, Parse(json, Body("marketdata"), AvanzaTierAContext.Default.MarketDataDto, "m", "v", DtoTier.A), DateTimeOffset.UnixEpoch);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 15, 29, 40, TimeSpan.Zero), m.TimeOfLastUtc);
        Assert.Equal(m.TimeOfLastUtc!.Value.ToUnixTimeSeconds(), m.DepthReceivedUtc!.Value.ToUnixTimeSeconds());
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 16, 0, 0, 213, TimeSpan.Zero), m.UpdatedUtc);
        Assert.Equal(94.96m, m.Bid);
        Assert.Equal(94.98m, m.Ask);

        // Search prices are Swedish-formatted ("94,96") and must not become 9496.
        IReadOnlyList<InstrumentSearchHit> hits = AvanzaMapper.ToSearchHits(Parse(json, Body("search"), AvanzaTierBContext.Default.SearchResponseDto, "s", "v", DtoTier.B));
        Assert.Equal(new OrderbookId("5240"), hits[0].OrderbookId);
        Assert.Equal(94.96m, hits[0].LastPrice);

        Assert.Empty(AvanzaMapper.ToDeals(Parse(json, Body("deals"), AvanzaTierAContext.Default.DealsDto, "d", "v", DtoTier.A)));
        Assert.Equal(24, AvanzaMapper.ToBars(Parse(json, Body("price-chart"), AvanzaTierBContext.Default.PriceChartDto, "c", "v", DtoTier.B)).Count);
        Assert.Equal(2, AvanzaMapper.ToTransactions(Parse(json, Body("transactions"), AvanzaTierBContext.Default.TransactionsDto, "x", "v", DtoTier.B)).Count);
    }

    [Fact]
    public void Recording20260925_ConfirmsTheBankIdFieldNamesTheLoginRelieson()
    {
        string dir = Path.Combine(Root, "2026-09-25");
        var bodies = Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal).Where(f => !IsStreamRecording(f)).Select(Load).ToList();

        JsonElement start = bodies.First(b => b.Route == "auth.bankid.start").Body;
        Assert.True(start.TryGetProperty("qrToken", out _));
        Assert.True(bodies.Where(b => b.Route == "auth.bankid.restart").All(b => b.Body.TryGetProperty("qrToken", out _)));

        JsonElement complete = bodies.Last(b => b.Route == "auth.bankid.collect").Body;
        Assert.True(complete.TryGetProperty("state", out _));
        Assert.True(complete.GetProperty("logins")[0].TryGetProperty("loginPath", out _));

        // The security-token cookie is set by the login path (not the trading page as the reference client assumed).
        using JsonDocument login = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(dir, "*-auth.bankid.login.json").Single()));
        Assert.Contains("AZACSRF", login.RootElement.GetProperty("response").GetProperty("setCookieNames").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData("94,96", "94.96")]
    [InlineData("94.96", "94.96")]
    [InlineData("-0,5", "-0.5")]
    [InlineData("1 234,5", "1234.5")]
    [InlineData("12", "12")]
    public void ParseLooseDecimal_AcceptsSwedishAndInvariantFormats(string text, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), AvanzaMapper.ParseLooseDecimal(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("1.234,5")]
    [InlineData("1,234.5")]
    [InlineData("12e3")]
    [InlineData("٣٤")]
    public void ParseLooseDecimal_RejectsAmbiguousInput(string? text) =>
        Assert.Null(AvanzaMapper.ParseLooseDecimal(text));

    [Fact]
    public void StockholmConversion_RefusesDstGaps()
    {
        Assert.True(MarketTime.TryStockholmToUtc(new DateTime(2026, 9, 25, 17, 29, 40), out DateTimeOffset utc));
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 15, 29, 40, TimeSpan.Zero), utc);
        Assert.True(MarketTime.TryStockholmToUtc(new DateTime(2026, 1, 15, 9, 0, 0), out utc));
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero), utc);
        Assert.False(MarketTime.TryStockholmToUtc(new DateTime(2026, 3, 29, 2, 30, 0), out _)); // spring forward: doesn't exist
        Assert.False(MarketTime.TryStockholmToUtc(new DateTime(2026, 10, 25, 2, 30, 0), out _)); // fall back: ambiguous
    }

    /// <summary>Stream recordings (qa-stream-recording/1) are replayed by <see cref="StreamReplayTests"/> instead.</summary>
    private static bool IsStreamRecording(string file) => File.ReadAllText(file).Contains("\"qa-stream-recording/1\"", StringComparison.Ordinal);

    private static (string Route, int Status, JsonElement Body) Load(string file)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
        JsonElement root = doc.RootElement;
        return (root.GetProperty("route").GetString()!, root.GetProperty("response").GetProperty("status").GetInt32(),
            root.GetProperty("response").GetProperty("body").Clone());
    }

    private static T Parse<T>(AvanzaJson json, JsonElement body, JsonTypeInfo<T> info, string route, string version, DtoTier tier) =>
        json.Deserialize(body, info, route, version, tier);
}
