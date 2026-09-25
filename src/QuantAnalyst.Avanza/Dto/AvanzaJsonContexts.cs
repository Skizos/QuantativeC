using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantAnalyst.Avanza.Dto;

/// <summary>Tier A: unmapped members are an error (in addition to the unknown-field scan in <c>AvanzaJson</c>).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    NumberHandling = JsonNumberHandling.Strict,
    PropertyNameCaseInsensitive = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    AllowTrailingCommas = false)]
[JsonSerializable(typeof(SessionInfoDto))]
[JsonSerializable(typeof(AccountsOverviewDto))]
[JsonSerializable(typeof(List<TradingAccountDto>))]
[JsonSerializable(typeof(PositionsDto))]
[JsonSerializable(typeof(OrdersDto))]
[JsonSerializable(typeof(OrderbookDto))]
[JsonSerializable(typeof(MarketDataDto))]
internal sealed partial class AvanzaTierAContext : JsonSerializerContext;

/// <summary>Tier B: unmapped members are skipped here and reported by the unknown-field scan as warnings.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    NumberHandling = JsonNumberHandling.Strict,
    PropertyNameCaseInsensitive = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(SearchRequestDto))]
[JsonSerializable(typeof(SearchResponseDto))]
[JsonSerializable(typeof(PriceChartDto))]
[JsonSerializable(typeof(TransactionsDto))]
internal sealed partial class AvanzaTierBContext : JsonSerializerContext;
