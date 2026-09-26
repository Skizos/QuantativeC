// Pre-trade check DTOs (Tier A), Phase 7 step 1, PROVISIONAL: modelled on the Go SDK trading/types.go @ 43f39025
// (ValidateOrderRequest/Response, PreliminaryFeeRequest/Response, CurrencyExchangeFee). Neither reference client has
// a recorded answer; `qa probe --preflight` or the owner's web-app capture confirms them. Until then a real answer
// that differs is schema drift, and in live modes drift halts trading (ADR 0002).
namespace QuantAnalyst.Avanza.Dto;

/// <summary>
/// The order to validate, field for field as the Go SDK sends it. Fields the SDK leaves unset are sent as explicit
/// nulls, as Go's encoder does.
/// </summary>
internal sealed class ValidateOrderRequestDto
{
    public const string Version = "preflight.validate/2026-09-26-provisional";

    public required bool IsDividendReinvestment { get; init; }

    public string? RequestId { get; init; }

    public string? OrderRequestParameters { get; init; }

    public required decimal Price { get; init; }

    public required long Volume { get; init; }

    public long? OpenVolume { get; init; }

    public required string AccountId { get; init; }

    /// <summary>BUY or SELL.</summary>
    public required string Side { get; init; }

    public required string OrderbookId { get; init; }

    /// <summary>yyyy-MM-dd: the day order's last day, as it would be placed.</summary>
    public required string ValidUntil { get; init; }

    public OrderMetadataDto? Metadata { get; init; }

    /// <summary>NORMAL (R3 allows nothing else).</summary>
    public required string Condition { get; init; }

    public required string Isin { get; init; }

    public required string Currency { get; init; }

    public required string MarketPlace { get; init; }
}

/// <summary>One <c>{valid}</c> result per named check (Go <c>ValidateOrderResponse</c>, all six non-optional there).</summary>
internal sealed class ValidateOrderResponseDto
{
    public const string Version = "preflight.validate.response/2026-09-26-provisional";

    public required ValidationResultDto CommissionWarning { get; init; }

    public required ValidationResultDto EmployeeValidation { get; init; }

    public required ValidationResultDto LargeInScaleWarning { get; init; }

    public required ValidationResultDto OrderValueLimitWarning { get; init; }

    public required ValidationResultDto PriceRampingWarning { get; init; }

    public required ValidationResultDto CanadaOddLotWarning { get; init; }
}

internal sealed class ValidationResultDto
{
    public required bool Valid { get; init; }
}

/// <summary>The fee request: every value a string (Go <c>PreliminaryFeeRequest</c>).</summary>
internal sealed class PreliminaryFeeRequestDto
{
    public const string Version = "preflight.fee/2026-09-26-provisional";

    public required string AccountId { get; init; }

    public required string OrderbookId { get; init; }

    /// <summary>Invariant decimal, e.g. "70.85".</summary>
    public required string Price { get; init; }

    public required string Volume { get; init; }

    public required string Side { get; init; }
}

/// <summary>The fee answer: money as strings in the orderbook currency (Go <c>PreliminaryFeeResponse</c>).</summary>
internal sealed class PreliminaryFeeResponseDto
{
    public const string Version = "preflight.fee.response/2026-09-26-provisional";

    public required string Commission { get; init; }

    public required string MarketFees { get; init; }

    public required string TotalFees { get; init; }

    public required string TotalSum { get; init; }

    public required string TotalSumWithoutFees { get; init; }

    public required string OrderbookCurrency { get; init; }

    /// <summary>A pointer in Go: may be null.</summary>
    public string? TransactionTax { get; init; }

    public required CurrencyExchangeFeeDto CurrencyExchangeFee { get; init; }

    /// <summary>A pointer in Go: may be null.</summary>
    public string? Campaign { get; init; }
}

internal sealed class CurrencyExchangeFeeDto
{
    public required string Rate { get; init; }

    public required string Sum { get; init; }
}
