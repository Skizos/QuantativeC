using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Avanza.Dto;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Core.Orders;

namespace QuantAnalyst.Avanza.Mapping;

/// <summary>
/// DTO → Core mapping, in one place (ADR 0002 §2). Prices and money stay <c>decimal</c>; volumes must be integral;
/// timestamps become UTC. Values the DTO allows but the domain cannot interpret (an unknown side, a non-integral
/// volume, an unparseable date) raise <see cref="SchemaDriftException"/> with the JSON path — never a guess.
/// </summary>
internal static partial class AvanzaMapper
{
    public static SessionHealth ToSessionHealth(SessionInfoDto dto, DateTimeOffset now) => new(dto.User.LoggedIn, now);

    public static IReadOnlyList<Account> ToAccounts(AccountsOverviewDto dto) =>
        [.. dto.Accounts.Select(a => new Account(
            new AccountId(a.Id),
            a.Type,
            string.IsNullOrWhiteSpace(a.Name.UserDefinedName) ? a.Name.DefaultName : a.Name.UserDefinedName,
            a.Status,
            a.Balance.Unit ?? a.TotalValue.Unit ?? "SEK",
            a.Balance.Value,
            a.TotalValue.Value,
            a.BuyingPower.Value))];

    public static IReadOnlyList<TradingAccount> ToTradingAccounts(List<TradingAccountDto> dto) =>
        [.. dto.Select(a => new TradingAccount(
            new AccountId(a.AccountId),
            a.Name,
            a.AccountType,
            a.AvailableForPurchase,
            a.IsTradable,
            a.HasCredit,
            a.IsDiscretionaryAccount,
            [.. (a.CurrencyBalances ?? []).Select(c => new CurrencyBalance(c.Currency, c.Balance))]))];

    /// <summary>Only an empty deal list can be mapped until a recording shows what a deal looks like.</summary>
    public static IReadOnlyList<BrokerDeal> ToDeals(DealsDto dto) =>
        dto.Deals.Count == 0
            ? []
            : throw new EndpointNotModelledException(
                AvanzaRoutes.Deals.Name,
                $"{dto.Deals.Count} deal(s) returned, but the deal fields have not been recorded yet; run 'qa probe' and share the sanitized recording");

    public static PortfolioSnapshot ToPortfolio(PositionsDto dto, AccountId? filter, DateTimeOffset now)
    {
        var positions = new List<Position>();
        AddPositions(dto.WithOrderbook, filter, positions);
        AddPositions(dto.WithoutOrderbook, filter, positions);

        var cash = dto.CashPositions
            .Select(c => new CashPosition(new AccountId(c.Account.Id), c.TotalBalance.Value, c.TotalBalance.Unit ?? "SEK"))
            .Where(c => filter is null || c.Account == filter.Value)
            .ToList();
        return new PortfolioSnapshot(positions, cash, now);
    }

    private static void AddPositions(List<PositionDto> source, AccountId? filter, List<Position> into)
    {
        foreach (PositionDto p in source)
        {
            var account = new AccountId(p.Account.Id);
            if (filter is not null && account != filter.Value)
            {
                continue;
            }

            // Fund holdings are fractional, so position volumes are not required to be whole numbers.
            into.Add(new Position(
                account,
                p.Instrument.Orderbook is { } ob ? new OrderbookId(ob.Id) : null,
                p.Instrument.Name,
                p.Instrument.Isin,
                p.Instrument.Type,
                p.Instrument.Currency,
                p.Volume.Value,
                p.Value.Value,
                p.AverageAcquiredPrice?.Value,
                p.AcquiredValue?.Value,
                p.Instrument.Orderbook?.Quote?.Latest?.Value));
        }
    }

    public static IReadOnlyList<BrokerOrder> ToOrders(OrdersDto dto)
    {
        var result = new List<BrokerOrder>(dto.Orders.Count);
        for (int i = 0; i < dto.Orders.Count; i++)
        {
            OrderDto o = dto.Orders[i];
            string path = $"$.orders[{i}]";
            AvanzaRoute route = AvanzaRoutes.Orders;
            result.Add(new BrokerOrder(
                new OrderId(o.OrderId),
                new AccountId(o.Account.AccountId),
                new OrderbookId(o.OrderbookId),
                o.Orderbook.Name,
                ParseSide(o.Side, route, OrdersDto.Version, path + ".side"),
                o.Price,
                RequireIntegral(o.Volume, route, OrdersDto.Version, path + ".volume"),
                RequireIntegral(o.OriginalVolume ?? o.Volume, route, OrdersDto.Version, path + ".originalVolume"),
                o.State,
                o.Condition ?? "NORMAL",
                ParseTimestamp(o.Created, route, OrdersDto.Version, path + ".created"),
                ParseDate(o.ValidUntil, route, OrdersDto.Version, path + ".validUntil"),
                o.Modifiable ?? false,
                o.Deletable ?? false));
        }

        return result;
    }

    public static InstrumentTradingParams ToTradingParams(OrderbookDto dto, DateTimeOffset now)
    {
        AvanzaRoute route = AvanzaRoutes.Orderbook;
        TickSizeTable ticks;
        try
        {
            ticks = new TickSizeTable(dto.TickSizeList.TickSizeEntries.Select(e => new TickSizeBand(e.Min, e.Max, e.Tick)));
        }
        catch (ArgumentException ex)
        {
            throw Drift(route, OrderbookDto.Version, DtoTier.A, "$.tickSizeList.tickSizeEntries", $"invalid tick table: {ex.Message}");
        }

        return new InstrumentTradingParams(
            new OrderbookId(dto.Id),
            dto.Name,
            dto.TickerSymbol,
            dto.Isin,
            dto.Currency,
            dto.MarketPlace,
            dto.CountryCode,
            dto.InstrumentType,
            dto.OrderbookStatus,
            ticks,
            RequirePositiveInt(dto.VolumeFactor, route, OrderbookDto.Version, "$.volumeFactor"),
            RequirePositiveInt(dto.TradingUnit, route, OrderbookDto.Version, "$.tradingUnit"),
            ParseDate(dto.MinValidUntil, route, OrderbookDto.Version, "$.minValidUntil"),
            ParseDate(dto.MaxValidUntil, route, OrderbookDto.Version, "$.maxValidUntil"),
            now);
    }

    public static MarketSnapshot ToMarketSnapshot(OrderbookId id, MarketDataDto dto, DateTimeOffset now)
    {
        AvanzaRoute route = AvanzaRoutes.MarketData;
        MarketDataQuoteDto q = dto.Quote;
        var depth = new List<DepthLevel>(dto.OrderDepth.Levels.Count);
        foreach (OrderDepthLevelDto level in dto.OrderDepth.Levels)
        {
            depth.Add(new DepthLevel(
                PositiveOrNull(level.BuySide.Price), level.BuySide.Volume,
                PositiveOrNull(level.SellSide.Price), level.SellSide.Volume));
        }

        return new MarketSnapshot(
            id,
            PositiveOrNull(q.Buy),
            PositiveOrNull(q.Sell),
            PositiveOrNull(q.Last),
            PositiveOrNull(q.Highest),
            PositiveOrNull(q.Lowest),
            q.Change,
            q.ChangePercent,
            PositiveOrNull(q.VolumeWeightedAveragePrice),
            q.TotalVolumeTraded ?? 0m,
            q.TotalValueTraded ?? 0m,
            ParseTimestamp(q.TimeOfLast, route, MarketDataDto.Version, "$.quote.timeOfLast", naiveIsStockholm: true),
            ParseTimestamp(q.Updated, route, MarketDataDto.Version, "$.quote.updated", naiveIsStockholm: true),
            depth,
            ParseTimestamp(dto.OrderDepth.ReceivedTime, route, MarketDataDto.Version, "$.orderDepth.receivedTime"),
            now);
    }

    public static IReadOnlyList<InstrumentSearchHit> ToSearchHits(SearchResponseDto dto) =>
        [.. dto.Hits.Select(h => new InstrumentSearchHit(
            new OrderbookId(h.OrderBookId),
            h.Title,
            h.Type ?? string.Empty,
            h.MarketPlaceName ?? string.Empty,
            h.Tradeable ?? false,
            ParseLooseDecimal(h.Price?.Last),
            h.Price?.Currency))];

    public static IReadOnlyList<Bar> ToBars(PriceChartDto dto)
    {
        var bars = new List<Bar>(dto.Ohlc.Count);
        for (int i = 0; i < dto.Ohlc.Count; i++)
        {
            OhlcDto b = dto.Ohlc[i];
            decimal volume = RequireIntegral(b.TotalVolumeTraded, AvanzaRoutes.PriceChart, PriceChartDto.Version, $"$.ohlc[{i}].totalVolumeTraded", DtoTier.B);
            bars.Add(new Bar(DateTimeOffset.FromUnixTimeMilliseconds(b.Timestamp), b.Open, b.High, b.Low, b.Close, (long)volume));
        }

        return bars;
    }

    public static IReadOnlyList<BrokerTransaction> ToTransactions(TransactionsDto dto)
    {
        var result = new List<BrokerTransaction>(dto.Transactions.Count);
        for (int i = 0; i < dto.Transactions.Count; i++)
        {
            TransactionDto t = dto.Transactions[i];
            DateOnly date = ParseDateString(t.Date)
                ?? throw Drift(AvanzaRoutes.Transactions, TransactionsDto.Version, DtoTier.B, $"$.transactions[{i}].date", "unrecognised date format");
            result.Add(new BrokerTransaction(
                t.Id,
                date,
                new AccountId(t.Account.Id),
                t.Type,
                t.Description,
                t.Orderbook is { } ob ? new OrderbookId(ob.Id) : null,
                t.Isin ?? t.Orderbook?.Isin,
                t.Volume?.Value,
                t.PriceInTradedCurrency?.Value,
                t.Amount?.Value,
                t.Commission?.Value,
                t.Amount?.Unit ?? t.Orderbook?.Currency,
                t.Cancelled ?? false));
        }

        return result;
    }

    // ---- helpers -------------------------------------------------------------------------------------

    internal static OrderSide ParseSide(string side, AvanzaRoute route, string version, string path) => side switch
    {
        "BUY" => OrderSide.Buy,
        "SELL" => OrderSide.Sell,
        _ => throw Drift(route, version, DtoTier.A, path, "unknown order side"),
    };

    private static decimal? PositiveOrNull(decimal? value) => value is > 0m ? value : null;

    private static decimal RequireIntegral(decimal value, AvanzaRoute route, string version, string path, DtoTier tier = DtoTier.A) =>
        value == decimal.Truncate(value) && value >= 0m
            ? decimal.Truncate(value)
            : throw Drift(route, version, tier, path, "expected a non-negative whole number");

    private static int RequirePositiveInt(decimal value, AvanzaRoute route, string version, string path) =>
        value == decimal.Truncate(value) && value is >= 1m and <= int.MaxValue
            ? (int)value
            : throw Drift(route, version, DtoTier.A, path, "expected a positive whole number");

    /// <summary>
    /// Epoch milliseconds (number) or ISO-8601 with an explicit offset. With <paramref name="naiveZone"/> set, an ISO
    /// timestamp without an offset is read in that zone. This is only done for routes where a recording proved the
    /// zone (marketdata: Europe/Stockholm, see <see cref="MarketTime"/>). null/absent ⇒ null; anything else ⇒ drift.
    /// </summary>
    internal static DateTimeOffset? ParseTimestamp(
        JsonElement? element, AvanzaRoute route, string version, string path, bool naiveIsStockholm = false)
    {
        if (element is not { } e || e.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long ms))
        {
            return ms == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        if (e.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
            && HasExplicitOffset(e.GetString()!))
        {
            return parsed.ToUniversalTime();
        }

        if (naiveIsStockholm
            && e.ValueKind == JsonValueKind.String
            && DateTime.TryParseExact(e.GetString(), NaiveIsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            return MarketTime.TryStockholmToUtc(local, out DateTimeOffset utc)
                ? utc
                : throw Drift(route, version, DtoTier.A, path, "Stockholm local time is ambiguous or invalid (DST change)");
        }

        throw Drift(route, version, DtoTier.A, path,
            naiveIsStockholm
                ? "expected epoch milliseconds or an ISO-8601 timestamp (with offset, or Stockholm local time)"
                : "expected epoch milliseconds or an ISO-8601 timestamp with offset");
    }

    /// <summary>yyyy-MM-dd, or an ISO timestamp whose date part is used; null/absent ⇒ null; anything else ⇒ drift.</summary>
    internal static DateOnly? ParseDate(JsonElement? element, AvanzaRoute route, string version, string path)
    {
        if (element is not { } e || e.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (e.ValueKind == JsonValueKind.String && ParseDateString(e.GetString()) is { } date)
        {
            return date;
        }

        throw Drift(route, version, DtoTier.A, path, "expected a yyyy-MM-dd date");
    }

    private static DateOnly? ParseDateString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
        {
            return d;
        }

        return text.Length > 10 && text[10] == 'T'
               && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
            ? d
            : null;
    }

    private static bool HasExplicitOffset(string text)
    {
        int t = text.IndexOf('T', StringComparison.Ordinal);
        if (t < 0)
        {
            return false;
        }

        string time = text[(t + 1)..];
        return time.EndsWith('Z') || time.Contains('+', StringComparison.Ordinal) || time.Contains('-', StringComparison.Ordinal);
    }

    private static readonly string[] NaiveIsoFormats =
        ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.f", "yyyy-MM-dd'T'HH:mm:ss.ff", "yyyy-MM-dd'T'HH:mm:ss.fff", "yyyy-MM-dd'T'HH:mm:ss.ffffff"];

    /// <summary>
    /// Tier B string numbers. Search prices come Swedish-formatted ("94,96", seen live 2026-09-25) or with a dot.
    /// Only <c>-?digits[,|.]digits</c> is accepted (spaces removed); "null", "" or anything ambiguous ⇒ null, never a
    /// silently wrong value such as 9496.
    /// </summary>
    internal static decimal? ParseLooseDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string s = text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\u00a0", string.Empty, StringComparison.Ordinal);
        if (!LooseDecimal().IsMatch(s))
        {
            return null;
        }

        return decimal.TryParse(s.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal v)
            ? v
            : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^-?[0-9]+([.,][0-9]+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex LooseDecimal();

    internal static SchemaDriftException Drift(AvanzaRoute route, string version, DtoTier tier, string path, string detail) =>
        new(route.Name, version, tier, [path], detail);
}
