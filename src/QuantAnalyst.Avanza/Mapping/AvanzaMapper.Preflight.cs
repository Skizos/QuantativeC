using QuantAnalyst.Avanza.Dto;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza.Mapping;

internal static partial class AvanzaMapper
{
    /// <summary>Every named check in its wire name, so the order card and the audit show Avanza's own words.</summary>
    public static PreflightValidation ToPreflightValidation(ValidateOrderResponseDto dto) =>
        new(
        [
            new("commissionWarning", dto.CommissionWarning.Valid),
            new("employeeValidation", dto.EmployeeValidation.Valid),
            new("largeInScaleWarning", dto.LargeInScaleWarning.Valid),
            new("orderValueLimitWarning", dto.OrderValueLimitWarning.Valid),
            new("priceRampingWarning", dto.PriceRampingWarning.Valid),
            new("canadaOddLotWarning", dto.CanadaOddLotWarning.Valid),
        ]);

    /// <summary>
    /// Money strings become decimals ("1,06" and "1.06" both read as 1.06). A required amount that is not a plain
    /// number, a negative amount, or a currency that is not a 3-letter code is Tier A drift, never a guessed value.
    /// </summary>
    public static PreliminaryFee ToPreliminaryFee(PreliminaryFeeResponseDto dto)
    {
        AvanzaRoute route = AvanzaPreflightRoutes.PreliminaryFee;
        const string version = PreliminaryFeeResponseDto.Version;

        decimal Amount(string? text, string path) =>
            ParseLooseDecimal(text) is { } v && v >= 0m ? v : throw Drift(route, version, DtoTier.A, path, $"'{path}' is not a non-negative amount");

        decimal? Optional(string? text, string path) => string.IsNullOrWhiteSpace(text) ? null : Amount(text, path);

        string currency = dto.OrderbookCurrency;
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            throw Drift(route, version, DtoTier.A, "orderbookCurrency", "'orderbookCurrency' is not a 3-letter currency code");
        }

        return new PreliminaryFee(
            currency,
            Amount(dto.Commission, "commission"),
            Amount(dto.MarketFees, "marketFees"),
            Amount(dto.TotalFees, "totalFees"),
            Amount(dto.TotalSum, "totalSum"),
            Amount(dto.TotalSumWithoutFees, "totalSumWithoutFees"),
            Optional(dto.TransactionTax, "transactionTax"),
            Optional(dto.CurrencyExchangeFee.Rate, "currencyExchangeFee.rate"),
            Optional(dto.CurrencyExchangeFee.Sum, "currencyExchangeFee.sum") ?? 0m);
    }
}
