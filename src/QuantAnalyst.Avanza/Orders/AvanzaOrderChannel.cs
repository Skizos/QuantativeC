using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using QuantAnalyst.Avanza.Dto;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Json;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza.Orders;

/// <summary>
/// The real order channel (ADR 0002/0003). <b>Phase 6: fixture-tested only.</b> It cannot be created outside this
/// assembly (internal constructor, internal factory on <see cref="AvanzaConnection"/>), and <c>OrderGateway</c> refuses
/// any channel that is not simulated, so nothing in the program can send it an order yet.
/// <para>Each request is sent <b>once</b> through the order pipeline (rate limit, security token, cookies; no retry
/// handler, no recorder) and its answer is classified:</para>
/// <list type="bullet">
/// <item>2xx with <c>SUCCESS</c> and an order id ⇒ Accepted; with <c>ERROR</c> ⇒ Rejected with Avanza's message.</item>
/// <item>Timeout, transport error, 408, 5xx, a redirect, or a caller cancel while waiting ⇒ Unknown: the order may
/// exist.</item>
/// <item>404 ⇒ Unknown + <see cref="BrokerFault.EndpointGone"/>; 401/403 ⇒ Unknown + <see cref="BrokerFault.SessionExpired"/>;
/// an answer that fails the Tier A schema ⇒ Unknown + <see cref="BrokerFault.SchemaDrift"/>. The gateway halts on each.</item>
/// <item>Other 4xx (e.g. 400, 429) ⇒ Rejected: the server refused the request without acting on it.</item>
/// </list>
/// </summary>
public sealed class AvanzaOrderChannel : IBrokerOrderChannel
{
    public const string ChannelName = "avanza";
    private const int MaxMessageChars = 300;

    private readonly HttpClient _orders;
    private readonly AvanzaJson _json;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    internal AvanzaOrderChannel(HttpClient orderClient, AvanzaJson json, TimeProvider time, TimeSpan timeout)
    {
        _orders = orderClient;
        _json = json;
        _time = time;
        _timeout = timeout;
    }

    /// <summary>
    /// Why real orders wait (plan 07 step 7): the order bodies and answers are provisional until the owner captures a
    /// real web-app buy and sell (O4) and a recorded deal (O5) confirms the deals mapper.
    /// </summary>
    internal const string Provisional =
        "the Avanza order format is provisional until your web-app capture of one buy and one sell (O4) and a recorded deal (O5) finalise it (plan 07 step 7)";

    public string Name => ChannelName;

    /// <summary>Gets why no channel of this build may send real orders yet (for <c>qa status</c>), or null once the format is final.</summary>
    public static string? FormatNotFinal => Provisional;

    public string? NotReadyReason => FormatNotFinal;

    public Task<OrderSubmitResult> PlaceAsync(ApprovedOrder order, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        var body = new PlaceOrderRequestDto
        {
            AccountId = order.Account.Value,
            OrderbookId = order.OrderbookId.Value,
            Side = order.Side == OrderSide.Buy ? "BUY" : "SELL",
            Condition = "NORMAL",
            Price = order.LimitPrice,
            ValidUntil = Date(order.ValidUntil),
            Volume = order.Volume,
        };
        return SendAsync(AvanzaOrderRoutes.Place, body, AvanzaTierAContext.Default.PlaceOrderRequestDto, ct);
    }

    public Task<OrderSubmitResult> ModifyAsync(ApprovedModify modify, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(modify);
        var body = new ModifyOrderRequestDto
        {
            AccountId = modify.Account.Value,
            Metadata = new OrderMetadataDto { OrderEntryMode = "STANDARD" },
            OpenVolume = null,
            OrderId = modify.BrokerOrderId.Value,
            Price = modify.LimitPrice,
            ValidUntil = Date(modify.ValidUntil),
            Volume = modify.Volume,
        };
        return SendAsync(AvanzaOrderRoutes.Modify, body, AvanzaTierAContext.Default.ModifyOrderRequestDto, ct);
    }

    public Task<OrderSubmitResult> CancelAsync(ApprovedCancel cancel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        var body = new DeleteOrderRequestDto { AccountId = cancel.Account.Value, OrderId = cancel.BrokerOrderId.Value };
        return SendAsync(AvanzaOrderRoutes.Delete, body, AvanzaTierAContext.Default.DeleteOrderRequestDto, ct);
    }

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<OrderSubmitResult> SendAsync<TBody>(AvanzaRoute route, TBody body, JsonTypeInfo<TBody> bodyInfo, CancellationToken ct)
    {
        using var content = JsonContent.Create(body, bodyInfo);
        using var request = new HttpRequestMessage(HttpMethod.Post, route.Path()) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        request.Options.Set(AvanzaRequest.Route, route);

        using var timeout = new CancellationTokenSource(_timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        HttpResponseMessage response;
        try
        {
            response = await _orders.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return OrderSubmitResult.Unknown($"{route.Name}: no answer within {_timeout.TotalSeconds:0} s");
        }
        catch (OperationCanceledException)
        {
            // The request may already be on the wire: the caller's cancel does not make it "not sent".
            return OrderSubmitResult.Unknown($"{route.Name}: cancelled while waiting for the answer");
        }
        catch (HttpRequestException ex)
        {
            return OrderSubmitResult.Unknown($"{route.Name}: transport error ({ex.HttpRequestError})");
        }

        using (response)
        {
            int code = (int)response.StatusCode;
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return OrderSubmitResult.Unknown($"{route.Name}: HTTP {code}, session expired", BrokerFault.SessionExpired);
                case HttpStatusCode.NotFound:
                    return OrderSubmitResult.Unknown($"{route.Name}: HTTP 404, the order endpoint moved or is gone", BrokerFault.EndpointGone);
                case HttpStatusCode.RequestTimeout:
                    return OrderSubmitResult.Unknown($"{route.Name}: HTTP 408");
            }

            if (code is >= 400 and < 500)
            {
                return OrderSubmitResult.Rejected($"{route.Name}: HTTP {code}, refused by the server");
            }

            if (code is < 200 or >= 300)
            {
                return OrderSubmitResult.Unknown($"{route.Name}: HTTP {code}");
            }

            byte[] bytes;
            try
            {
                bytes = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                return OrderSubmitResult.Unknown($"{route.Name}: the answer could not be read ({ex.GetType().Name})");
            }

            return Classify(route, bytes);
        }
    }

    private OrderSubmitResult Classify(AvanzaRoute route, byte[] bytes)
    {
        OrderRequestResponseDto answer;
        try
        {
            answer = _json.Deserialize(bytes, AvanzaTierAContext.Default.OrderRequestResponseDto, route.Name, OrderRequestResponseDto.Version, DtoTier.A);
        }
        catch (SchemaDriftException ex)
        {
            return OrderSubmitResult.Unknown($"{route.Name}: {ex.Message}", BrokerFault.SchemaDrift);
        }

        string message = Clip(answer.Message);
        OrderId? id = ValidId(answer.OrderId);
        return answer.OrderRequestStatus switch
        {
            "SUCCESS" when id is { } ok => OrderSubmitResult.Accepted(ok, message),
            "SUCCESS" => OrderSubmitResult.Unknown($"{route.Name}: SUCCESS without a usable order id"),
            "ERROR" => OrderSubmitResult.Rejected(message.Length > 0 ? message : "ERROR without a message", id),
            _ => OrderSubmitResult.Unknown($"{route.Name}: unknown orderRequestStatus '{Clip(answer.OrderRequestStatus)}'", BrokerFault.SchemaDrift),
        };
    }

    private static OrderId? ValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? new OrderId(id) : null;

    private static string Clip(string? text)
    {
        string t = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return t.Length <= MaxMessageChars ? t : t[..MaxMessageChars] + "…";
    }
}
