using System.Text.Json;
using System.Text.Json.Serialization;

// Tier A (trading-critical) DTOs, ADR 0002 §2. Unknown or missing required fields => SchemaDriftException => halt.
// Field lists come from the reference clients at the commits in docs/research/avanza-endpoints.md:
//   Qluxzz  avanza/models/{overview,account_posititions,market_data,order_book}.py @ a6a18a94
//   Go SDK  {accounts,trading,market,auth}/types.go                                 @ 43f39025
// Only fields we map are `required`; known-but-unused fields are optional and typed JsonElement where their
// shape is irrelevant, so a renamed field is still reported as unknown. Provisional until live recordings
// (docs/plans/03-phase3-avanza-read.md "Stop point").
// ".2" versions (2026-09-25): field names corrected from the owner's first live probe.
// ".3" versions (2026-09-25): types taken from the owner's sanitized recording
// (recordings/fixtures/avanza/2026-09-25), which RecordedFixtureTests parse strictly on every build.
namespace QuantAnalyst.Avanza.Dto;

/// <summary>Avanza's value-with-unit object: {value, unit, unitType, decimalPrecision}.</summary>
internal sealed class AvanzaMoneyDto
{
    public required decimal Value { get; init; }

    public string? Unit { get; init; }

    public string? UnitType { get; init; }

    public int? DecimalPrecision { get; init; }
}

// ---- GET /_api/authentication/session/info/session (Go auth.SessionInfo) --------------------------------

internal sealed class SessionInfoDto
{
    public const string Version = "session-info/2026-09-25";

    public string? InvalidSessionId { get; init; }

    public required SessionUserDto User { get; init; }
}

internal sealed class SessionUserDto
{
    public required bool LoggedIn { get; init; }

    public string? GreetingName { get; init; }

    public string? PushSubscriptionId { get; init; }

    [JsonPropertyName("pushBaseUrl")]
    public string? PushBaseAddress { get; init; }

    public string? SecurityToken { get; init; }

    public bool? Company { get; init; }

    public bool? Minor { get; init; }

    public bool? Start { get; init; }

    public string? CustomerGroup { get; init; }

    public string? Id { get; init; }
}

// ---- GET /_api/account-overview/overview/categorizedAccounts ------------------------------------------

internal sealed class AccountsOverviewDto
{
    public const string Version = "accounts-overview/2026-09-25.3";

    public JsonElement? Categories { get; init; }

    public required List<OverviewAccountDto> Accounts { get; init; }

    public JsonElement? Loans { get; init; }
}

internal sealed class OverviewAccountDto
{
    public required string Id { get; init; }

    public string? CategoryId { get; init; }

    public required AvanzaMoneyDto Balance { get; init; }

    public JsonElement? Profit { get; init; }

    public required string Type { get; init; }

    public required AvanzaMoneyDto TotalValue { get; init; }

    public required AvanzaMoneyDto BuyingPower { get; init; }

    public AvanzaMoneyDto? BuyingPowerWithoutCredit { get; init; }

    public AvanzaMoneyDto? DepositInterestRate { get; init; }

    public AvanzaMoneyDto? LoanInterestRate { get; init; }

    public AvanzaMoneyDto? Credit { get; init; }

    public required AccountNameDto Name { get; init; }

    public required string Status { get; init; }

    public string? ErrorStatus { get; init; }

    public JsonElement? Overmortgaged { get; init; }

    public JsonElement? CurrencyBalances { get; init; }

    public JsonElement? Overdrawn { get; init; }

    public JsonElement? Performance { get; init; }

    public JsonElement? Settings { get; init; }

    public string? ClearingAccountNumber { get; init; }

    public bool? AccountType24 { get; init; }

    public bool? DiscretionaryPortfolio { get; init; }

    [JsonPropertyName("urlParameterId")]
    public string? UrlParameterKey { get; init; }

    public bool? Owner { get; init; }

    // Seen live 2026-09-25, not in any reference client. interestRates is {currency: {deposit, loan}} (unused);
    // creditAccountClearingAccountNumber was null in every account (unused; redacted by the sanitizer).
    public JsonElement? InterestRates { get; init; }

    public JsonElement? CreditAccountClearingAccountNumber { get; init; }

    public bool? AutoDistribution { get; init; }
}

internal sealed class AccountNameDto
{
    public required string DefaultName { get; init; }

    public string? UserDefinedName { get; init; }
}

// ---- GET /_api/trading-critical/rest/accounts (Go accounts.TradingAccount; root is an array) -----------

internal sealed class TradingAccountDto
{
    public const string Version = "trading-accounts/2026-09-25.3";

    public required string Name { get; init; }

    public required string AccountId { get; init; }

    public string? AccountTypeName { get; init; }

    public required string AccountType { get; init; }

    public required decimal AvailableForPurchase { get; init; }

    public decimal? AvailableForPurchaseWithoutCredit { get; init; }

    public decimal? AvailableCredit { get; init; }

    public required bool HasCredit { get; init; }

    public required bool IsTradable { get; init; }

    public bool? IsShortSellable { get; init; }

    public bool? IsOvermortgaged { get; init; }

    public bool? IsOverdrawn { get; init; }

    public bool? IsHidden { get; init; }

    public JsonElement? Positions { get; init; }

    public List<CurrencyBalanceDto>? CurrencyBalances { get; init; }

    [JsonPropertyName("urlParameterId")]
    public string? UrlParameterKey { get; init; }

    // Seen live 2026-09-25 (boolean). A discretionary (managed) account must never be traded by this program.
    public bool? IsDiscretionaryAccount { get; init; }
}

internal sealed class CurrencyBalanceDto
{
    public required string Currency { get; init; }

    public string? CountryCode { get; init; }

    public required decimal Balance { get; init; }
}

// ---- GET /_api/position-data/positions ------------------------------------------------------------------

internal sealed class PositionsDto
{
    public const string Version = "positions/2026-09-25";

    public required List<PositionDto> WithOrderbook { get; init; }

    public required List<PositionDto> WithoutOrderbook { get; init; }

    public required List<CashPositionDto> CashPositions { get; init; }

    public bool? WithCreditAccount { get; init; }
}

internal sealed class PositionDto
{
    public required PositionAccountDto Account { get; init; }

    public required PositionInstrumentDto Instrument { get; init; }

    public required AvanzaMoneyDto Volume { get; init; }

    public required AvanzaMoneyDto Value { get; init; }

    public AvanzaMoneyDto? AverageAcquiredPrice { get; init; }

    public AvanzaMoneyDto? AverageAcquiredPriceInstrumentCurrency { get; init; }

    public AvanzaMoneyDto? AcquiredValue { get; init; }

    public JsonElement? LastTradingDayPerformance { get; init; }

    public required string Id { get; init; }

    public bool? SuperInterestApproved { get; init; }

    public AvanzaMoneyDto? CollateralFactor { get; init; }
}

internal sealed class PositionAccountDto
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public required string Name { get; init; }

    [JsonPropertyName("urlParameterId")]
    public string? UrlParameterKey { get; init; }

    public bool? HasCredit { get; init; }

    public bool? HasAutoDistribution { get; init; }
}

internal sealed class PositionInstrumentDto
{
    public string? Id { get; init; }

    public required string Type { get; init; }

    public required string Name { get; init; }

    public PositionOrderbookDto? Orderbook { get; init; }

    public required string Currency { get; init; }

    public string? Isin { get; init; }

    public decimal? VolumeFactor { get; init; }
}

internal sealed class PositionOrderbookDto
{
    public required string Id { get; init; }

    public string? FlagCode { get; init; }

    public required string Name { get; init; }

    public string? Type { get; init; }

    public string? TradeStatus { get; init; }

    public PositionQuoteDto? Quote { get; init; }

    public JsonElement? Turnover { get; init; }

    public JsonElement? LastDeal { get; init; }
}

internal sealed class PositionQuoteDto
{
    public AvanzaMoneyDto? Highest { get; init; }

    public AvanzaMoneyDto? Lowest { get; init; }

    public AvanzaMoneyDto? Buy { get; init; }

    public AvanzaMoneyDto? Sell { get; init; }

    public AvanzaMoneyDto? Latest { get; init; }

    public AvanzaMoneyDto? Change { get; init; }

    public AvanzaMoneyDto? ChangePercent { get; init; }

    public JsonElement? Updated { get; init; }
}

internal sealed class CashPositionDto
{
    public required PositionAccountDto Account { get; init; }

    public required AvanzaMoneyDto TotalBalance { get; init; }

    public required string Id { get; init; }
}

// ---- GET /_api/trading/rest/orders (Go trading.GetOrdersResponse) ---------------------------------------

internal sealed class OrdersDto
{
    public const string Version = "orders/2026-09-25";

    public required List<OrderDto> Orders { get; init; }

    public JsonElement? FundOrders { get; init; }

    public JsonElement? CancelledOrders { get; init; }
}

internal sealed class OrderDto
{
    public required OrderAccountDto Account { get; init; }

    public required string OrderId { get; init; }

    public required decimal Volume { get; init; }

    public decimal? OriginalVolume { get; init; }

    public required decimal Price { get; init; }

    public decimal? Amount { get; init; }

    public required string OrderbookId { get; init; }

    public required string Side { get; init; }

    public JsonElement? ValidUntil { get; init; }

    public JsonElement? Created { get; init; }

    public bool? Deletable { get; init; }

    public bool? Modifiable { get; init; }

    public string? Message { get; init; }

    public required string State { get; init; }

    public string? StateText { get; init; }

    public string? StateMessage { get; init; }

    public required OrderOrderbookDto Orderbook { get; init; }

    public JsonElement? AdditionalParameters { get; init; }

    public string? Condition { get; init; }
}

internal sealed class OrderAccountDto
{
    public required string AccountId { get; init; }

    public JsonElement? Name { get; init; }

    public JsonElement? Type { get; init; }

    [JsonPropertyName("urlParameterId")]
    public string? UrlParameterKey { get; init; }
}

internal sealed class OrderOrderbookDto
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? CountryCode { get; init; }

    public string? Currency { get; init; }

    public string? InstrumentType { get; init; }

    // A string in the Go model, a number in the orderbook model: shape irrelevant here.
    public JsonElement? VolumeFactor { get; init; }

    public string? Isin { get; init; }

    public string? Mic { get; init; }
}

// ---- GET /_api/trading/rest/deals (envelope from the owner's recording 2026-09-25; no deal seen yet) ------

internal sealed class DealsDto
{
    public const string Version = "deals/2026-09-25";

    // Element fields are unknown until a recording contains a fill; the mapper refuses non-empty lists.
    public required List<JsonElement> Deals { get; init; }

    public JsonElement? FundDeals { get; init; }
}

// ---- GET /_api/trading-critical/rest/orderbook/{id} (tick table) ----------------------------------------

internal sealed class OrderbookDto
{
    public const string Version = "orderbook/2026-09-25.2";

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Isin { get; init; }

    public string? InstrumentId { get; init; }

    public required string MarketPlace { get; init; }

    public required string CountryCode { get; init; }

    public required TickSizeListDto TickSizeList { get; init; }

    public decimal? CollateralValue { get; init; }

    public required string Currency { get; init; }

    // Absent in the owner's live probe (2026-09-25); optional since.
    public string? OrderbookStatus { get; init; }

    public JsonElement? MinValidUntil { get; init; }

    public JsonElement? MaxValidUntil { get; init; }

    public required string InstrumentType { get; init; }

    public required decimal VolumeFactor { get; init; }

    public JsonElement? FeatureSupport { get; init; }

    public string? PriceType { get; init; }

    public required decimal TradingUnit { get; init; }

    public string? TickerSymbol { get; init; }

    public string? UnderlyingOrderbook { get; init; }

    public string? UnderlyingCountryCode { get; init; }
}

internal sealed class TickSizeListDto
{
    public required List<TickSizeEntryDto> TickSizeEntries { get; init; }
}

internal sealed class TickSizeEntryDto
{
    public required decimal Min { get; init; }

    public required decimal Max { get; init; }

    public required decimal Tick { get; init; }
}

// ---- GET /_api/trading-critical/rest/marketdata/{id} ----------------------------------------------------

internal sealed class MarketDataDto
{
    public const string Version = "marketdata/2026-09-25";

    public required MarketDataQuoteDto Quote { get; init; }

    public required OrderDepthDto OrderDepth { get; init; }

    public JsonElement? Trades { get; init; }
}

internal sealed class MarketDataQuoteDto
{
    public decimal? Buy { get; init; }

    public decimal? Sell { get; init; }

    public decimal? Last { get; init; }

    public decimal? Highest { get; init; }

    public decimal? Lowest { get; init; }

    public decimal? Change { get; init; }

    public decimal? ChangePercent { get; init; }

    public decimal? Spread { get; init; }

    // Epoch ms (Qluxzz Quote, Go testdata) or ISO-8601 (Qluxzz MarketData validator); mapper accepts both.
    public JsonElement? TimeOfLast { get; init; }

    public decimal? TotalValueTraded { get; init; }

    public decimal? TotalVolumeTraded { get; init; }

    public JsonElement? Updated { get; init; }

    public decimal? VolumeWeightedAveragePrice { get; init; }

    public bool? IsRealTime { get; init; }
}

internal sealed class OrderDepthDto
{
    public JsonElement? ReceivedTime { get; init; }

    public required List<OrderDepthLevelDto> Levels { get; init; }

    public bool? MarketMakerExpected { get; init; }
}

internal sealed class OrderDepthLevelDto
{
    public required OrderDepthSideDto BuySide { get; init; }

    public required OrderDepthSideDto SellSide { get; init; }
}

internal sealed class OrderDepthSideDto
{
    public required decimal Price { get; init; }

    // Empty sides have been reported as "0.00" strings (avanza-endpoints.md §3).
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public required decimal Volume { get; init; }

    public string? PriceString { get; init; }
}

// ---- SSE /_push/order-depth-web-push/{id}, event ORDER_DEPTH ---------------------------------------------
// PROVISIONAL: from the Go SDK market/types.go (OrderDepthData) @ 43f39025 and its tests; replaced by the owner's
// recording in Phase 4. Every event is a full snapshot.

internal sealed class OrderDepthPushDto
{
    public const string Version = "order-depth-push/2026-09-25";

    /// <summary>A string in the Go SDK model and test payloads.</summary>
    public required string OrderbookId { get; init; }

    public required List<OrderDepthPushLevelDto> Levels { get; init; }

    public int? MarketMakerLevelInAsk { get; init; }

    public int? MarketMakerLevelInBid { get; init; }
}

/// <summary>One flat level. A side is empty when its price is missing/null or price and volume are both zero.</summary>
internal sealed class OrderDepthPushLevelDto
{
    public decimal? BuyPrice { get; init; }

    public decimal? BuyVolume { get; init; }

    public decimal? SellPrice { get; init; }

    public decimal? SellVolume { get; init; }
}
