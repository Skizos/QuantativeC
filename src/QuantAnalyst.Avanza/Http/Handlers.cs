using System.Net;
using Microsoft.Extensions.Logging;
using QuantAnalyst.Avanza.Logging;

namespace QuantAnalyst.Avanza.Http;

/// <summary>Per-request metadata carried through the handler pipeline.</summary>
internal static class AvanzaRequest
{
    public static readonly HttpRequestOptionsKey<AvanzaRoute> Route = new("qa.avanza.route");

    public static string RouteName(HttpRequestMessage request) =>
        request.Options.TryGetValue(Route, out AvanzaRoute? route) ? route.Name : "unknown";
}

/// <summary>Waits for a token from the shared bucket before every attempt (ADR 0002: ~2 req/s, burst 5).</summary>
internal sealed class RateLimitHandler(TokenBucket bucket, TimeProvider time, ILogger logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        TimeSpan wait = bucket.Reserve();
        if (wait > TimeSpan.Zero)
        {
            string routeName = AvanzaRequest.RouteName(request);
            long waitMs = (long)wait.TotalMilliseconds;
            Log.RateLimited(logger, routeName, waitMs);

            await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Owns cookies explicitly (instead of the socket handler) so the security-token cookie can be read and so the
/// behaviour is identical with test handlers. Only cookie <em>names</em> ever reach logs or recordings.
/// </summary>
internal sealed class CookieHandler(CookieContainer cookies) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri ?? throw new InvalidOperationException("Request has no URI.");
        string header = cookies.GetCookieHeader(uri);
        request.Headers.Remove("Cookie");
        if (header.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", header);
        }

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies))
        {
            foreach (string c in setCookies)
            {
                try
                {
                    cookies.SetCookies(uri, c);
                }
                catch (CookieException)
                {
                    // A malformed cookie is ignored; the value is never logged.
                }
            }
        }

        return response;
    }
}

/// <summary>Adds <c>X-SecurityToken</c> and JSON accept headers to authenticated reads.</summary>
internal sealed class SecurityTokenHandler(AvanzaSession session) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(AvanzaSession.SecurityTokenHeader);
        if (session.SecurityToken is { } token)
        {
            request.Headers.TryAddWithoutValidation(AvanzaSession.SecurityTokenHeader, token.Reveal());
        }

        return base.SendAsync(request, cancellationToken);
    }
}
