using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Logging;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza.Auth;

/// <summary>
/// Shows the BankID QR code to the human who approves the login. <see cref="ShowQrCode"/> is called once at the start
/// and again for every refreshed (animated) QR token. The payload is only valid for seconds and must not be logged.
/// </summary>
public interface IBankIdPrompt
{
    void ShowQrCode(string qrPayload);

    void ShowStatus(string message);

    void Completed();
}

/// <summary>
/// BankID login, as implemented by the reference Go client (vmorsell/avanza-sdk-go@43f39025, auth/auth.go):
/// <list type="number">
/// <item>start page (cookies)</item>
/// <item>start a QR transaction</item>
/// <item>every <c>BankIdPollInterval</c>: collect the state, then restart for a fresh QR token</item>
/// <item>on COMPLETE, GET <c>logins[0].loginPath</c> (validated), then the trading page (cookies)</item>
/// <item>verify with session info; the security token is the AZACSRF cookie (or session info as fallback)</item>
/// </list>
/// One transaction per trigger; FAILED or expiry stops the trigger, never a second transaction. The collect
/// payload also carries the user's name and personal identity number: those fields are never read.
/// BankID failures do not count towards the TOTP login lock (there is no password lockout involved).
/// </summary>
public sealed partial class AvanzaAuthenticator
{
    private const int MaxRedirects = 5;

    private async Task<LoginResult> LoginWithBankIdAsync(CancellationToken ct)
    {
        IBankIdPrompt prompt = _bankIdPrompt
                               ?? throw new InvalidOperationException("BankID login needs a prompt that can show the QR code.");
        try
        {
            Log.BankIdStarting(_logger);
            using (HttpResponseMessage page = await GetFollowingRedirectsAsync(AvanzaRoutes.StartPage, AvanzaRoutes.StartPage.Path(), ct).ConfigureAwait(false))
            {
                EnsureBankIdStep(page, "start page");
            }

            string qrToken;
            DateTimeOffset deadline = _time.GetUtcNow() + _options.BankIdTimeout;
            byte[] startBody = Encoding.UTF8.GetBytes("""{"method":"QR_START","returnScheme":"NULL"}""");
            using (HttpResponseMessage started = await SendOnceAsync(AvanzaRoutes.BankIdStart, HttpMethod.Post, AvanzaRoutes.BankIdStart.Path(), startBody, null, ct).ConfigureAwait(false))
            {
                EnsureBankIdStep(started, "start");
                using JsonDocument doc = await ReadJsonAsync(started, AvanzaRoutes.BankIdStart, ct).ConfigureAwait(false);
                qrToken = RequireString(doc.RootElement, "qrToken", AvanzaRoutes.BankIdStart);
                if (OptionalString(doc.RootElement, "expires") is { } expiresText
                    && DateTimeOffset.TryParse(expiresText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset expires)
                    && expires < deadline)
                {
                    deadline = expires;
                }
            }

            prompt.ShowQrCode(qrToken);
            string? loginPath = null;
            string? lastHint = null;
            while (loginPath is null)
            {
                await Task.Delay(_options.BankIdPollInterval, _time, ct).ConfigureAwait(false);
                using (HttpResponseMessage collected = await SendOnceAsync(AvanzaRoutes.BankIdCollect, HttpMethod.Post, AvanzaRoutes.BankIdCollect.Path(), Encoding.UTF8.GetBytes("{}"), null, ct).ConfigureAwait(false))
                {
                    EnsureBankIdStep(collected, "collect");
                    using JsonDocument doc = await ReadJsonAsync(collected, AvanzaRoutes.BankIdCollect, ct).ConfigureAwait(false);
                    string state = RequireString(doc.RootElement, "state", AvanzaRoutes.BankIdCollect);
                    string? hint = OptionalString(doc.RootElement, "hintCode");
                    switch (state)
                    {
                        case "COMPLETE":
                            loginPath = ReadLoginPath(doc.RootElement);
                            break;
                        case "FAILED":
                            string reason = DescribeHint(hint);
                            string failedHint = SafeHint(hint);
                            prompt.ShowStatus(reason);
                            Log.LoginFailed(_logger, "bankid", failedHint);
                            throw new LoginFailedException($"BankID: {reason} Run the command again to start a new BankID login");
                        case "OUTSTANDING_TRANSACTION" or "PENDING":
                            if (hint != lastHint)
                            {
                                lastHint = hint;
                                prompt.ShowStatus(DescribeHint(hint));
                                string safeHint = SafeHint(hint);
                                Log.BankIdStatus(_logger, safeHint);
                            }

                            break;
                        default:
                            throw BankIdDrift(AvanzaRoutes.BankIdCollect, "$.state", "unknown BankID state");
                    }
                }

                if (loginPath is not null)
                {
                    break;
                }

                if (_time.GetUtcNow() >= deadline)
                {
                    prompt.ShowStatus("The BankID request expired.");
                    Log.LoginFailed(_logger, "bankid", "not completed in time");
                    throw new LoginFailedException(
                        $"BankID was not approved within {(int)_options.BankIdTimeout.TotalSeconds} s. Run the command again to start a new BankID login");
                }

                using HttpResponseMessage restarted = await SendOnceAsync(AvanzaRoutes.BankIdRestart, HttpMethod.Post, AvanzaRoutes.BankIdRestart.Path(), Encoding.UTF8.GetBytes("{}"), null, ct).ConfigureAwait(false);
                if (restarted.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    EnsureBankIdStep(restarted, "restart");
                }

                if (restarted.IsSuccessStatusCode)
                {
                    using JsonDocument doc = await ReadJsonAsync(restarted, AvanzaRoutes.BankIdRestart, ct).ConfigureAwait(false);
                    prompt.ShowQrCode(RequireString(doc.RootElement, "qrToken", AvanzaRoutes.BankIdRestart));
                }

                // Any other restart failure keeps the current QR, as the reference client does; collect decides.
            }

            prompt.Completed();
            using (HttpResponseMessage selected = await GetFollowingRedirectsAsync(AvanzaRoutes.BankIdLogin, loginPath, ct).ConfigureAwait(false))
            {
                EnsureBankIdStep(selected, "select customer");
            }

            using (HttpResponseMessage trading = await GetFollowingRedirectsAsync(AvanzaRoutes.TradingPage, AvanzaRoutes.TradingPage.Path(), ct).ConfigureAwait(false))
            {
                EnsureBankIdStep(trading, "trading page");
            }

            (Secret token, string source) = await ResolveBankIdTokenAsync(ct).ConfigureAwait(false);
            _redactor.AddSecret(token);
            _session.SetToken(token, source);
            _state.RecordBankIdSuccess();
            Log.LoginSucceeded(_logger, "BankID", source);
            return new LoginResult(AvanzaLoginMethod.BankId, source, _time.GetUtcNow());
        }
        catch (HttpRequestException ex)
        {
            Log.LoginFailed(_logger, "transport", "network error");
            throw new BrokerUnavailableException("login", "Network error during BankID login; not retrying.", null, ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.LoginFailed(_logger, "transport", "timeout");
            throw new BrokerUnavailableException("login", $"A BankID login step timed out after {_options.AttemptTimeout.TotalSeconds:0} s; not retrying.");
        }
    }

    /// <summary>AZACSRF cookie (reference client), verified by session info; session info's token as fallback.</summary>
    private async Task<(Secret Token, string Source)> ResolveBankIdTokenAsync(CancellationToken ct)
    {
        string? cookie = _session.Cookies.GetCookies(_options.BaseAddress)[AvanzaSession.SecurityTokenCookie]?.Value;
        using HttpResponseMessage info = await SendOnceAsync(
            AvanzaRoutes.SessionInfo, HttpMethod.Get, AvanzaRoutes.SessionInfo.Path(), null, string.IsNullOrEmpty(cookie) ? null : cookie, ct).ConfigureAwait(false);
        EnsureBankIdStep(info, "session check");
        using JsonDocument doc = await ReadJsonAsync(info, AvanzaRoutes.SessionInfo, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("user", out JsonElement user) || user.ValueKind != JsonValueKind.Object
            || !user.TryGetProperty("loggedIn", out JsonElement loggedIn) || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw BankIdDrift(AvanzaRoutes.SessionInfo, "$.user.loggedIn", "missing loggedIn");
        }

        string? sessionToken = OptionalString(user, "securityToken");
        _redactor.AddSecret(sessionToken);
        _redactor.AddSecret(OptionalString(user, "pushSubscriptionId"));
        if (!loggedIn.GetBoolean())
        {
            throw new LoginFailedException("BankID was approved but Avanza reports the session as not logged in");
        }

        if (!string.IsNullOrEmpty(cookie))
        {
            return (new Secret(cookie), "cookie");
        }

        return !string.IsNullOrEmpty(sessionToken)
            ? (new Secret(sessionToken), "session-info")
            : throw BankIdDrift(AvanzaRoutes.SessionInfo, "AZACSRF|$.user.securityToken", "no security token after BankID login");
    }

    /// <summary>GET that follows up to 5 same-origin redirects itself, so the cookie handler sees every hop.</summary>
    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(AvanzaRoute route, string pathAndQuery, CancellationToken ct)
    {
        string path = pathAndQuery;
        AvanzaRoute current = route;
        for (int hop = 0; ; hop++)
        {
            HttpResponseMessage response = await SendOnceAsync(current, HttpMethod.Get, path, null, null, ct).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if (code is < 300 or >= 400 || response.Headers.Location is not { } location)
            {
                return response;
            }

            var target = new Uri(_options.BaseAddress, location);
            if (hop >= MaxRedirects || target.Scheme != _options.BaseAddress.Scheme || target.Host != _options.BaseAddress.Host)
            {
                return response; // EnsureBankIdStep rejects the 3xx
            }

            response.Dispose();
            path = target.PathAndQuery;
            current = AvanzaRoutes.LoginRedirect;
        }
    }

    private void EnsureBankIdStep(HttpResponseMessage response, string step)
    {
        int code = (int)response.StatusCode;
        if (code is >= 200 and < 300)
        {
            return;
        }

        Log.LoginFailed(_logger, "bankid " + step, $"HTTP {code}");
        throw response.StatusCode switch
        {
            >= HttpStatusCode.InternalServerError => new BrokerUnavailableException("login", $"Avanza returned HTTP {code} at BankID {step}; not retrying.", code),
            _ => new LoginFailedException($"BankID {step}: HTTP {code}"),
        };
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, AvanzaRoute route, CancellationToken ct)
    {
        byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        try
        {
            JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                throw BankIdDrift(route, "$", "expected a JSON object");
            }

            return doc;
        }
        catch (JsonException ex)
        {
            throw new SchemaDriftException(route.Name, AuthDtoVersion, DtoTier.A, [ex.Path ?? "$"], "response is not valid JSON", ex);
        }
    }

    private static string ReadLoginPath(JsonElement root)
    {
        if (!root.TryGetProperty("logins", out JsonElement logins) || logins.ValueKind != JsonValueKind.Array || logins.GetArrayLength() == 0)
        {
            throw BankIdDrift(AvanzaRoutes.BankIdCollect, "$.logins", "no logins in a completed BankID transaction");
        }

        string? path = OptionalString(logins[0], "loginPath");
        return AvanzaRoutes.IsValidBankIdLoginPath(path)
            ? path!
            : throw BankIdDrift(AvanzaRoutes.BankIdCollect, "$.logins[0].loginPath", "loginPath is missing or not a BankID login route");
    }

    private static string RequireString(JsonElement obj, string property, AvanzaRoute route) =>
        OptionalString(obj, property) is { Length: > 0 } value
            ? value
            : throw BankIdDrift(route, "$." + property, $"missing '{property}'");

    private static string? OptionalString(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    private static SchemaDriftException BankIdDrift(AvanzaRoute route, string path, string detail) =>
        new(route.Name, AuthDtoVersion, DtoTier.A, [path], detail);

    /// <summary>Hint codes are BankID's public RP API values; anything else is shown only as sanitized letters.</summary>
    internal static string DescribeHint(string? hintCode) => hintCode switch
    {
        null or "" or "outstandingTransaction" or "noClient" or "started" => "Open the BankID app and scan the QR code.",
        "userSign" or "userMrtd" or "userCallConfirm" => "Confirm the login in the BankID app.",
        "userCancel" => "The login was cancelled in the BankID app.",
        "cancelled" => "The BankID request was cancelled (another login may have started).",
        "expiredTransaction" => "The BankID request expired.",
        "startFailed" => "The QR code was not scanned in time.",
        "certificateErr" => "BankID reported a certificate problem; check your BankID.",
        _ => $"BankID status: {SafeHint(hintCode)}",
    };

    private static string SafeHint(string? hintCode) =>
        string.IsNullOrEmpty(hintCode) ? "none" : new string([.. hintCode.Where(char.IsAsciiLetter).Take(40)]);
}
