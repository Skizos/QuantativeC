using System.Globalization;
using QuantAnalyst.Avanza.Dto;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Mapping;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Avanza.Orders;

/// <summary>
/// Avanza's pre-trade checks (ADR 0003 §2 <c>BrokerPreflight</c>, Phase 7 step 1): <c>validate</c>, then
/// <c>preliminaryfee</c>, for the order exactly as it would be placed. Both are read-only POSTs through the read
/// pipeline (rate limit, security token, retries on 5xx). Nothing is placed. Failures become the outcome, never an
/// exception, so the caller always gets something to judge:
/// <list type="bullet">
/// <item>schema drift ⇒ <see cref="BrokerFault.SchemaDrift"/>, 401/403 ⇒ <see cref="BrokerFault.SessionExpired"/>, 404 ⇒
/// <see cref="BrokerFault.EndpointGone"/>: the gateway halts on each.</item>
/// <item>anything else (transport, 5xx after retries, 429) ⇒ no fault, the part is missing and <see cref="PreflightOutcome.Problem"/>
/// says why. A missing validation fails R21; a missing fee leaves R9 on the model's courtage.</item>
/// </list>
/// A validation fault skips the fee call. Created only through <see cref="AvanzaConnection"/> (internal), and the only
/// user of <see cref="AvanzaPreflightRoutes"/> (architecture test).
/// </summary>
public sealed class AvanzaPreflight : IBrokerPreflight
{
    private readonly AvanzaApiClient _api;
    private readonly TimeProvider _time;

    internal AvanzaPreflight(AvanzaApiClient api, TimeProvider time)
    {
        _api = api;
        _time = time;
    }

    public async Task<PreflightOutcome> CheckAsync(PreflightRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        string side = request.Side == OrderSide.Buy ? "BUY" : "SELL";
        string price = request.LimitPrice.ToString("0.##########", CultureInfo.InvariantCulture); // "70.85", never "70.850"
        var validateBody = new ValidateOrderRequestDto
        {
            IsDividendReinvestment = false,
            Price = request.LimitPrice,
            Volume = request.Volume,
            AccountId = request.Account.Value,
            Side = side,
            OrderbookId = request.OrderbookId.Value,
            ValidUntil = MarketTime.ToStockholm(_time.GetUtcNow()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Condition = "NORMAL",
            Isin = request.Isin,
            Currency = request.Currency,
            MarketPlace = request.MarketPlace,
        };

        PreflightValidation validation;
        try
        {
            ValidateOrderResponseDto dto = await _api.PostAsync(
                AvanzaPreflightRoutes.Validate, validateBody, AvanzaTierAContext.Default.ValidateOrderRequestDto,
                AvanzaTierAContext.Default.ValidateOrderResponseDto, ValidateOrderResponseDto.Version, ct).ConfigureAwait(false);
            validation = AvanzaMapper.ToPreflightValidation(dto);
        }
        catch (BrokerException ex)
        {
            return PreflightOutcome.Failed(FaultOf(ex), $"validate: {ex.Message}");
        }

        var feeBody = new PreliminaryFeeRequestDto
        {
            AccountId = request.Account.Value,
            OrderbookId = request.OrderbookId.Value,
            Price = price,
            Volume = request.Volume.ToString(CultureInfo.InvariantCulture),
            Side = side,
        };
        try
        {
            PreliminaryFeeResponseDto dto = await _api.PostAsync(
                AvanzaPreflightRoutes.PreliminaryFee, feeBody, AvanzaTierAContext.Default.PreliminaryFeeRequestDto,
                AvanzaTierAContext.Default.PreliminaryFeeResponseDto, PreliminaryFeeResponseDto.Version, ct).ConfigureAwait(false);
            return new PreflightOutcome(validation, AvanzaMapper.ToPreliminaryFee(dto), BrokerFault.None, null);
        }
        catch (BrokerException ex)
        {
            return new PreflightOutcome(validation, null, FaultOf(ex), $"preliminary fee: {ex.Message}");
        }
    }

    private static BrokerFault FaultOf(BrokerException ex) => ex switch
    {
        SchemaDriftException => BrokerFault.SchemaDrift,
        SessionExpiredException => BrokerFault.SessionExpired,
        EndpointGoneException => BrokerFault.EndpointGone,
        _ => BrokerFault.None,
    };

    private static void Validate(PreflightRequest r)
    {
        if (r.Volume <= 0 || r.LimitPrice <= 0m)
        {
            throw new ArgumentException("A preflight needs a positive volume and limit price.", nameof(r));
        }

        if (!Enum.IsDefined(r.Side) || string.IsNullOrWhiteSpace(r.Account.Value) || string.IsNullOrWhiteSpace(r.OrderbookId.Value)
            || string.IsNullOrWhiteSpace(r.Isin) || string.IsNullOrWhiteSpace(r.Currency) || string.IsNullOrWhiteSpace(r.MarketPlace))
        {
            throw new ArgumentException("A preflight needs the side, account, orderbook, ISIN, currency and market place.", nameof(r));
        }
    }
}
