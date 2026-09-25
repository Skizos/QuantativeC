using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QuantAnalyst.Avanza.Credentials;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Logging;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza.Auth;

/// <summary>Outcome of a successful login. Contains no secret.</summary>
public sealed record LoginResult(string TokenSource, DateTimeOffset AtUtc);

/// <summary>
/// Username + password + TOTP login (ADR 0002 §2, avanza-endpoints.md §1). <b>One attempt per trigger</b>:
/// every step is sent at most once, a failure is persisted, and a second failure within 24 h locks login until a
/// human clears it. There is no next-OTP retry (the reference Python client has one; we deliberately do not).
/// </summary>
public sealed class AvanzaAuthenticator
{
    internal const string AuthDtoVersion = "auth/2026-09-25";
    private static readonly TimeSpan MinTotpValidity = TimeSpan.FromSeconds(3);

    private readonly HttpClient _authClient;
    private readonly AvanzaSession _session;
    private readonly ISecretStore _secrets;
    private readonly AuthStateStore _state;
    private readonly AvanzaOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Redactor _redactor;
    private int _attempted;

    internal AvanzaAuthenticator(
        HttpClient authClient, AvanzaSession session, ISecretStore secrets, AuthStateStore state,
        AvanzaOptions options, TimeProvider time, ILogger logger, Redactor redactor)
    {
        _authClient = authClient;
        _session = session;
        _secrets = secrets;
        _state = state;
        _options = options;
        _time = time;
        _logger = logger;
        _redactor = redactor;
    }

    public bool IsLocked => _state.Load().Locked;

    public string StateFile => _state.FilePath;

    /// <summary>Clears a persisted lock. Only after the user has confirmed via BankID that login works.</summary>
    public void ClearLock() => _state.ClearLock();

    /// <summary>
    /// Performs the single login attempt for this trigger. A second call on the same instance throws: one
    /// connection = one trigger = at most one login.
    /// </summary>
    public async Task<LoginResult> LoginAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _attempted, 1) != 0)
        {
            throw new InvalidOperationException("Login was already attempted for this trigger; start a new trigger (process) to try again.");
        }

        AuthState state = _state.Load();
        if (state.Locked)
        {
            throw new LoginLockedException(state.LockReason ?? "locked");
        }

        AvanzaCredentials credentials = _secrets.GetAvanzaCredentials();
        _redactor.AddSecret(credentials.Username);
        _redactor.AddSecret(credentials.Password);
        _redactor.AddSecret(credentials.TotpSecret);
        byte[] totpKey;
        try
        {
            totpKey = Base32.Decode(credentials.TotpSecret.Reveal());
        }
        catch (FormatException)
        {
            // Configuration error, detected before any HTTP call: not counted as a failed login.
            throw new LoginFailedException($"the TOTP secret in {_secrets.Name} is not valid Base32");
        }

        try
        {
            Log.LoginStarting(_logger, _options.MaxInactiveMinutes);

            // Step 1: username + password.
            byte[] credentialsBody = WriteJson(w =>
            {
                w.WriteNumber("maxInactiveMinutes", _options.MaxInactiveMinutes);
                w.WriteString("username", credentials.Username.Reveal());
                w.WriteString("password", credentials.Password.Reveal());
            });
            using HttpResponseMessage step1 = await PostOnceAsync(AvanzaRoutes.UserCredentials, credentialsBody, ct).ConfigureAwait(false);
            ThrowIfFailed(step1, "usercredentials");
            string? method = await ReadTwoFactorMethodAsync(step1, ct).ConfigureAwait(false);

            HttpResponseMessage final = step1;
            HttpResponseMessage? step2 = null;
            try
            {
                if (method is not null)
                {
                    if (!string.Equals(method, "TOTP", StringComparison.Ordinal))
                    {
                        throw Fail("usercredentials", $"unsupported two-factor method '{method}' (only TOTP is supported)", countTowardsLock: false);
                    }

                    // Step 2: TOTP. Wait for a fresh window if the current code is about to expire (a wait, not a retry).
                    await WaitForFreshTotpWindowAsync(ct).ConfigureAwait(false);
                    string code = Totp.Compute(totpKey, _time.GetUtcNow());
                    _redactor.AddSecret(code);
                    byte[] totpBody = WriteJson(w =>
                    {
                        w.WriteString("method", "TOTP");
                        w.WriteString("totpCode", code);
                    });
                    step2 = await PostOnceAsync(AvanzaRoutes.Totp, totpBody, ct).ConfigureAwait(false);
                    ThrowIfFailed(step2, "totp");
                    final = step2;
                }

                (Secret token, string source) = ExtractToken(final);
                _redactor.AddSecret(token);
                _session.SetToken(token, source);
                _state.RecordSuccess();
                Log.LoginSucceeded(_logger, source);
                return new LoginResult(source, _time.GetUtcNow());
            }
            finally
            {
                step2?.Dispose();
            }
        }
        catch (HttpRequestException ex)
        {
            Log.LoginFailed(_logger, "transport", "network error");
            throw new BrokerUnavailableException("login", "Network error during login; not retrying.", null, ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.LoginFailed(_logger, "transport", "timeout");
            throw new BrokerUnavailableException("login", $"Login step timed out after {_options.AttemptTimeout.TotalSeconds:0} s; not retrying.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(totpKey);
        }
    }

    private async Task<HttpResponseMessage> PostOnceAsync(AvanzaRoute route, byte[] body, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_options.AttemptTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, route.Path());
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Accept.ParseAdd("application/json");
        request.Options.Set(AvanzaRequest.Route, route);
        try
        {
            return await _authClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    private void ThrowIfFailed(HttpResponseMessage response, string step)
    {
        int code = (int)response.StatusCode;
        if (code is >= 200 and < 300)
        {
            return;
        }

        throw response.StatusCode switch
        {
            // Lockout signal is undocumented (ADR 0002 open item 4): 423/429 on login are treated as a lock, conservatively.
            HttpStatusCode.Locked or HttpStatusCode.TooManyRequests => Fail(step, $"HTTP {code} (possible lockout)", lockNow: true),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Fail(step, $"HTTP {code} (credentials or code rejected)"),
            >= HttpStatusCode.InternalServerError => new BrokerUnavailableException("login", $"Avanza returned HTTP {code} at {step}; not retrying.", code),
            _ => Fail(step, $"HTTP {code}"),
        };
    }

    private Exception Fail(string step, string reason, bool countTowardsLock = true, bool lockNow = false)
    {
        Log.LoginFailed(_logger, step, reason);
        if (!countTowardsLock)
        {
            return new LoginFailedException($"{step}: {reason}");
        }

        AuthState next = _state.RecordFailure($"{step}: {reason}", lockNow);
        if (next.Locked)
        {
            Log.LoginLocked(_logger, next.LockReason ?? reason);
            return new LoginLockedException(next.LockReason ?? reason);
        }

        return new LoginFailedException($"{step}: {reason}");
    }

    private static async Task<string?> ReadTwoFactorMethodAsync(HttpResponseMessage response, CancellationToken ct)
    {
        byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Drift("$", "login response is not a JSON object");
            }

            if (!doc.RootElement.TryGetProperty("twoFactorLogin", out JsonElement twoFactor) || twoFactor.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return twoFactor.ValueKind == JsonValueKind.Object
                   && twoFactor.TryGetProperty("method", out JsonElement m)
                   && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : throw Drift("$.twoFactorLogin.method", "missing two-factor method");
        }
        catch (JsonException ex)
        {
            throw new SchemaDriftException(AvanzaRoutes.UserCredentials.Name, AuthDtoVersion, DtoTier.A, [ex.Path ?? "$"], "login response is not valid JSON", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    /// <summary>X-SecurityToken response header (Qluxzz), else the AZACSRF cookie (Go SDK), else drift.</summary>
    private (Secret Token, string Source) ExtractToken(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues(AvanzaSession.SecurityTokenHeader, out IEnumerable<string>? values)
            && values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) is { } header)
        {
            return (new Secret(header.Trim()), "header");
        }

        Cookie? cookie = _session.Cookies.GetCookies(_options.BaseAddress)[AvanzaSession.SecurityTokenCookie];
        if (cookie is { Value.Length: > 0 })
        {
            return (new Secret(cookie.Value), "cookie");
        }

        throw Drift("X-SecurityToken|AZACSRF", "no security token in the login response (neither header nor cookie)");
    }

    private async Task WaitForFreshTotpWindowAsync(CancellationToken ct)
    {
        long msIntoStep = _time.GetUtcNow().ToUnixTimeMilliseconds() % (Totp.DefaultStepSeconds * 1000L);
        var remaining = TimeSpan.FromMilliseconds((Totp.DefaultStepSeconds * 1000L) - msIntoStep);
        if (remaining < MinTotpValidity)
        {
            TimeSpan wait = remaining + TimeSpan.FromMilliseconds(250);
            Log.TotpWindowWait(_logger, (long)wait.TotalMilliseconds);
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    private static SchemaDriftException Drift(string path, string detail) =>
        new(AvanzaRoutes.UserCredentials.Name, AuthDtoVersion, DtoTier.A, [path], detail);

    private static byte[] WriteJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }

        byte[] result = buffer.WrittenSpan.ToArray();
        buffer.Clear();
        return result;
    }
}
