using System.Net;
using System.Text.Json.Serialization.Metadata;
using QuantAnalyst.Avanza.Json;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza.Http;

/// <summary>
/// Sends one logical read through the read pipeline and maps the status (ADR 0002 §2):
/// <list type="bullet">
/// <item>401/403 ⇒ <see cref="SessionExpiredException"/></item>
/// <item>404 on a Tier A route ⇒ <see cref="EndpointGoneException"/></item>
/// <item>any other non-2xx ⇒ <see cref="BrokerUnavailableException"/></item>
/// </list>
/// </summary>
internal sealed class AvanzaApiClient(HttpClient readClient, AvanzaJson json)
{
    public const int MaxResponseBytes = 16 * 1024 * 1024;

    public async Task<byte[]> SendAsync(AvanzaRoute route, string pathAndQuery, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(route.Method), pathAndQuery) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        request.Options.Set(AvanzaRequest.Route, route);

        HttpResponseMessage response;
        try
        {
            response = await readClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerUnavailableException(route.Name, "Transport error calling Avanza.", null, ex);
        }

        using (response)
        {
            ThrowForStatus(route, response.StatusCode);
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<T> GetAsync<T>(AvanzaRoute route, string pathAndQuery, JsonTypeInfo<T> typeInfo, string dtoVersion, CancellationToken ct)
    {
        byte[] body = await SendAsync(route, pathAndQuery, null, ct).ConfigureAwait(false);
        return json.Deserialize(body, typeInfo, route.Name, dtoVersion, route.Tier ?? DtoTier.A);
    }

    public async Task<TResponse> PostAsync<TRequest, TResponse>(
        AvanzaRoute route, TRequest body, JsonTypeInfo<TRequest> requestInfo, JsonTypeInfo<TResponse> responseInfo, string dtoVersion, CancellationToken ct)
    {
        using var content = System.Net.Http.Json.JsonContent.Create(body, requestInfo);
        byte[] response = await SendAsync(route, route.Path(), content, ct).ConfigureAwait(false);
        return json.Deserialize(response, responseInfo, route.Name, dtoVersion, route.Tier ?? DtoTier.A);
    }

    internal static void ThrowForStatus(AvanzaRoute route, HttpStatusCode status)
    {
        int code = (int)status;
        if (code is >= 200 and < 300)
        {
            return;
        }

        throw status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new SessionExpiredException(route.Name, code),
            HttpStatusCode.NotFound when route.Tier == DtoTier.A => new EndpointGoneException(route.Name),
            _ => new BrokerUnavailableException(route.Name, $"Avanza returned HTTP {code} for '{route.Name}'.", code),
        };
    }
}
