using System.Text.Json;
using System.Text.Json.Serialization;

// Tier B (informational) DTOs, ADR 0002 §2. Missing required fields => SchemaDriftException (feature disabled);
// unknown fields are logged as drift.warning. Sources: Qluxzz models/search_result.py, Go market/types.go
// (SearchResponse, StockPriceChart) and accounts/types.go (TransactionsResponse) at the pinned commits.
namespace QuantAnalyst.Avanza.Dto;

// ---- POST /_api/search/filtered-search ------------------------------------------------------------------

internal sealed class SearchRequestDto
{
    public required string Query { get; init; }

    public required SearchFilterDto SearchFilter { get; init; }

    public required SearchPaginationDto Pagination { get; init; }
}

internal sealed class SearchFilterDto
{
    public required List<string> Types { get; init; }
}

internal sealed class SearchPaginationDto
{
    public required int From { get; init; }

    public required int Size { get; init; }
}

internal sealed class SearchResponseDto
{
    public const string Version = "search/2026-09-25.2";

    public int? TotalNumberOfHits { get; init; }

    public required List<SearchHitDto> Hits { get; init; }

    public string? SearchQuery { get; init; }

    public JsonElement? Facets { get; init; }

    public JsonElement? Pagination { get; init; }

    // Echo of the request filter; seen live 2026-09-25.
    public JsonElement? SearchFilter { get; init; }
}

internal sealed class SearchHitDto
{
    public string? Type { get; init; }

    public required string Title { get; init; }

    public string? HighlightedTitle { get; init; }

    public string? Description { get; init; }

    public string? HighlightedDescription { get; init; }

    public string? Path { get; init; }

    public string? FlagCode { get; init; }

    // Note the capital B, unlike every other route (Go SearchHit).
    [JsonPropertyName("orderBookId")]
    public required string OrderBookId { get; init; }

    public string? UrlSlugName { get; init; }

    public bool? Tradeable { get; init; }

    public bool? Sellable { get; init; }

    public bool? Buyable { get; init; }

    public SearchPriceDto? Price { get; init; }

    public JsonElement? StockSectors { get; init; }

    public JsonElement? FundTags { get; init; }

    public string? MarketPlaceName { get; init; }

    public JsonElement? SubType { get; init; }

    public string? HighlightedSubType { get; init; }
}

/// <summary>Search prices arrive as Swedish-formatted strings, e.g. "94,96" (live 2026-09-25); see ParseLooseDecimal.</summary>
internal sealed class SearchPriceDto
{
    public string? Last { get; init; }

    public string? Currency { get; init; }

    public string? TodayChangePercent { get; init; }

    public string? TodayChangeValue { get; init; }

    public int? TodayChangeDirection { get; init; }

    public string? ThreeMonthsAgoChangePercent { get; init; }

    public int? ThreeMonthsAgoChangeDirection { get; init; }

    public string? Spread { get; init; }
}

// ---- GET /_api/price-chart/stock/{id}?timePeriod=&resolution= ------------------------------------------

internal sealed class PriceChartDto
{
    // .2: metadata is typed and required (Phase 4 import checks the resolution Avanza used; present in both fixtures).
    public const string Version = "price-chart/2026-09-25.2";

    public required List<OhlcDto> Ohlc { get; init; }

    public required PriceChartMetadataDto Metadata { get; init; }

    public string? From { get; init; }

    public string? To { get; init; }

    public decimal? PreviousClosingPrice { get; init; }
}

internal sealed class PriceChartMetadataDto
{
    public required PriceChartResolutionDto Resolution { get; init; }
}

internal sealed class PriceChartResolutionDto
{
    /// <summary>"day" live (2026-09-25), "DAY" in the provisional fixture; parsed case-insensitively.</summary>
    public required string ChartResolution { get; init; }

    public List<string>? AvailableResolutions { get; init; }
}

internal sealed class OhlcDto
{
    /// <summary>Bar start, epoch milliseconds.</summary>
    public required long Timestamp { get; init; }

    public required decimal Open { get; init; }

    public required decimal Close { get; init; }

    public required decimal Low { get; init; }

    public required decimal High { get; init; }

    // Decimal because captures show "142385548.0"-style values; the mapper checks it is integral.
    public required decimal TotalVolumeTraded { get; init; }
}

// ---- GET /_api/transactions/list?from=&to= ---------------------------------------------------------------

internal sealed class TransactionsDto
{
    public const string Version = "transactions/2026-09-25";

    public required List<TransactionDto> Transactions { get; init; }

    public int? TransactionsAfterFiltering { get; init; }

    public JsonElement? TransactionsFilter { get; init; }

    public string? FirstTransactionDate { get; init; }
}

internal sealed class TransactionDto
{
    public required string Id { get; init; }

    public required string Date { get; init; }

    public string? SettlementDate { get; init; }

    public string? AvailabilityDate { get; init; }

    public string? TradeDate { get; init; }

    public required TransactionAccountDto Account { get; init; }

    public TransactionOrderbookDto? Orderbook { get; init; }

    public string? InstrumentName { get; init; }

    public required string Description { get; init; }

    public required string Type { get; init; }

    public string? BackofficeType { get; init; }

    public string? BackofficeTypeText { get; init; }

    public AvanzaMoneyDto? Volume { get; init; }

    public AvanzaMoneyDto? PriceInTradedCurrency { get; init; }

    public AvanzaMoneyDto? Amount { get; init; }

    public bool? OnCreditAccount { get; init; }

    public AvanzaMoneyDto? Commission { get; init; }

    public decimal? CurrencyRate { get; init; }

    public string? NoteId { get; init; }

    public AvanzaMoneyDto? PriceInTransactionCurrency { get; init; }

    public bool? Intraday { get; init; }

    public decimal? ForeignTaxRate { get; init; }

    public string? Isin { get; init; }

    public AvanzaMoneyDto? Result { get; init; }

    public decimal? VolumeFactor { get; init; }

    public bool? Cancelled { get; init; }

    public string? CancelDate { get; init; }

    public string? VerificationNumber { get; init; }
}

internal sealed class TransactionAccountDto
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public string? Type { get; init; }

    [JsonPropertyName("urlParameterId")]
    public string? UrlParameterKey { get; init; }
}

internal sealed class TransactionOrderbookDto
{
    public required string Id { get; init; }

    public string? FlagCode { get; init; }

    public string? Name { get; init; }

    public string? Marketplace { get; init; }

    public string? Type { get; init; }

    public string? Currency { get; init; }

    public string? Isin { get; init; }

    public decimal? VolumeFactor { get; init; }
}

// ---- GET /_api/market-guide/stock/{id}/details (plan 21) --------------------------------------------------
// Go SDK market/types.go StockDetails, StockDividends, DividendEvent @ 43f39025. Only the dividends and the share
// count are read; every other section is accepted as it comes (JsonElement), so their changes are not drift.

internal sealed class StockDetailsDto
{
    public const string Version = "stock-details/2026-09-30";

    public required StockDividendsDto Dividends { get; init; }

    public StockShareInfoDto? Stock { get; init; }

    public JsonElement? Company { get; init; }

    public JsonElement? CompanyEvents { get; init; }

    public JsonElement? CompanyOwners { get; init; }

    public JsonElement? CompanyHoldings { get; init; }

    public JsonElement? BrokerTradeSummaries { get; init; }

    public JsonElement? TradingTerms { get; init; }

    public JsonElement? FundExposures { get; init; }

    public JsonElement? EtfExposures { get; init; }

    public JsonElement? EsgView { get; init; }

    public JsonElement? Trades { get; init; }

    public JsonElement? OrderDepth { get; init; }

    public JsonElement? InsiderTransactionsView { get; init; }

    public JsonElement? CompanyReports { get; init; }
}

internal sealed class StockDividendsDto
{
    /// <summary>Announced dividends (ex-date ahead).</summary>
    public required List<DividendEventDto> Events { get; init; }

    public required List<DividendEventDto> PastEvents { get; init; }
}

internal sealed class DividendEventDto
{
    public required string ExDate { get; init; }

    /// <summary>Absent until announced (Go SDK: <c>omitempty</c>).</summary>
    public string? PaymentDate { get; init; }

    public required decimal Amount { get; init; }

    public required string CurrencyCode { get; init; }

    public string? DividendType { get; init; }
}

internal sealed class StockShareInfoDto
{
    public decimal? NumberOfShares { get; init; }

    public bool? Preferred { get; init; }

    public bool? DepositoryReceipt { get; init; }
}
