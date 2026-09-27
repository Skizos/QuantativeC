using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using QuantAnalyst.Avanza.Auth;
using QuantAnalyst.Avanza.Credentials;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Json;
using QuantAnalyst.Avanza.Logging;
using QuantAnalyst.Avanza.Recording;
using QuantAnalyst.Avanza.Streaming;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza;

/// <summary>
/// One trigger's worth of Avanza access: a session, the login pipeline and the read pipeline (ADR 0002 §2):
/// <list type="bullet">
/// <item>login: Recording → RateLimit → Cookies → primary (no retries, ever)</item>
/// <item>reads: Recording → ReadResilience → RateLimit → SecurityToken → Cookies → primary</item>
/// <item>streams: RateLimit → SecurityToken → Cookies → primary (the stream client records and reconnects itself)</item>
/// <item>orders: RateLimit → SecurityToken → Cookies → primary. No retry handler and no recorder (an order body holds
/// the full account id). Used only by the internal order channel, which nothing outside this assembly can create in
/// Phase 6.</item>
/// </list>
/// Create one per CLI invocation; <see cref="Authenticator"/> allows exactly one login per connection.
/// </summary>
public sealed class AvanzaConnection : IDisposable
{
    private readonly HttpMessageHandler _primary;
    private readonly bool _ownsPrimary;
    private readonly HttpClient _authClient;
    private readonly HttpClient _readClient;
    private readonly HttpClient _streamClient;
    private readonly HttpClient _orderClient;
    private readonly AvanzaApiClient _api;
    private readonly AvanzaGateway _gateway;
    private readonly AvanzaJson _json;
    private readonly TimeProvider _time;
    private readonly TimeSpan _orderTimeout;

    private AvanzaConnection(
        AvanzaOptions options, ISecretStore secrets, ILogger logger, Redactor redactor, TimeProvider time, HttpMessageHandler? primary,
        IBankIdPrompt? bankIdPrompt)
    {
        options.Validate();
        _ownsPrimary = primary is null;
        _primary = primary ?? new SocketsHttpHandler
        {
            UseCookies = false, // CookieHandler owns the jar
            AllowAutoRedirect = false, // a redirect to a login page must surface, not be followed
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        var session = new AvanzaSession();
        var bucket = new TokenBucket(options.RequestsPerSecond, options.Burst, time);
        Recorder? recorder = null;
        if (options.RecordingDirectory is { } dir)
        {
            string stamp = time.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            recorder = new Recorder(Path.Combine(dir, stamp), time, logger);
            RecordingDirectory = recorder.Directory;
        }

        _authClient = Client(options, Chain(recorder,
            new RateLimitHandler(bucket, time, logger),
            new CookieHandler(session.Cookies)));

        var breaker = new CircuitBreaker(time, options.CircuitWindow, options.CircuitMinimumCalls, options.CircuitFailureRatio, options.CircuitBreakDuration, logger);
        _readClient = Client(options, Chain(recorder,
            new ReadResilienceHandler(options, breaker, time, logger, new Random()),
            new RateLimitHandler(bucket, time, logger),
            new SecurityTokenHandler(session),
            new CookieHandler(session.Cookies)));

        // Streams: no RecordingHandler (it buffers whole bodies; the stream client records events itself) and no retry
        // handler (the stream loop reconnects with its own backoff).
        _streamClient = Client(options, Chain(null,
            new RateLimitHandler(bucket, time, logger),
            new SecurityTokenHandler(session),
            new CookieHandler(session.Cookies)));

        // Orders: one attempt each (ADR 0003 §6: an order is never retried), no recording.
        _orderClient = Client(options, Chain(null,
            new RateLimitHandler(bucket, time, logger),
            new SecurityTokenHandler(session),
            new CookieHandler(session.Cookies)));

        var json = new AvanzaJson(logger);
        _json = json;
        _time = time;
        _orderTimeout = options.OrderTimeout;
        var api = new AvanzaApiClient(_readClient, json);
        _api = api;
        var streams = new AvanzaStreamClient(_streamClient, options, time, logger, Random.Shared, recorder);
        _gateway = new AvanzaGateway(api, streams, json, time, redactor);
        Authenticator = new AvanzaAuthenticator(
            _authClient, session, secrets, new AuthStateStore(options.StateDirectory, time), options, time, logger, redactor, bankIdPrompt);
        Session = session;
        Probe = new AvanzaProbe(Authenticator, _gateway, new Orders.AvanzaPreflight(api, time), time);

        HttpMessageHandler Chain(Recorder? rec, params DelegatingHandler[] handlers)
        {
            DelegatingHandler[] all = rec is null ? handlers : [new RecordingHandler(rec), .. handlers];
            for (int i = 0; i < all.Length - 1; i++)
            {
                all[i].InnerHandler = all[i + 1];
            }

            all[^1].InnerHandler = _primary;
            return all[0];
        }
    }

    /// <summary>Stored with imported chart rows: the DTO and routes versions they were parsed with.</summary>
    public static string PriceChartSourceVersion => $"{Dto.PriceChartDto.Version}; routes {AvanzaRoutes.RoutesVersion}";

    /// <summary>Stored with instrument-master rows built from the orderbook response.</summary>
    public static string OrderbookSourceVersion => $"{Dto.OrderbookDto.Version}; routes {AvanzaRoutes.RoutesVersion}";

    public AvanzaAuthenticator Authenticator { get; }

    public IBrokerGateway Gateway => _gateway;

    public AvanzaProbe Probe { get; }

    /// <summary>The folder this connection records into, or null when recording is off.</summary>
    public string? RecordingDirectory { get; }

    internal AvanzaSession Session { get; }

    /// <param name="bankIdPrompt">Shows the QR code; required when <see cref="AvanzaOptions.LoginMethod"/> is BankID.</param>
    public static AvanzaConnection Create(
        AvanzaOptions options, ISecretStore secrets, ILogger logger, Redactor redactor, IBankIdPrompt? bankIdPrompt = null, TimeProvider? time = null) =>
        new(options, secrets, logger, redactor, time ?? TimeProvider.System, primary: null, bankIdPrompt);

    /// <summary>Test seam: a fake primary handler instead of the network.</summary>
    internal static AvanzaConnection CreateForTest(
        AvanzaOptions options, ISecretStore secrets, ILogger logger, Redactor redactor, TimeProvider time, HttpMessageHandler primary,
        IBankIdPrompt? bankIdPrompt = null) =>
        new(options, secrets, logger, redactor, time, primary, bankIdPrompt);

    /// <summary>
    /// The real order channel (Phase 7). Only the CLI's Confirm composition asks for it (an IL-scanning architecture
    /// test checks every caller), and the trading gateway uses it only with the live authorization that the Confirm
    /// startup checks issue for it. Until the owner's capture finalises the order format it says it is not ready, so no
    /// authorization can be issued for it.
    /// </summary>
    public Orders.AvanzaOrderChannel CreateOrderChannel() => new(_orderClient, _json, _time, _orderTimeout);

    /// <summary>
    /// Avanza's read-only pre-trade checks (validate + preliminary fee) through the read pipeline. Asked for by the probe
    /// and the CLI's Confirm composition only (architecture test).
    /// </summary>
    public Orders.AvanzaPreflight CreatePreflight() => new(_api, _time);

    public void Dispose()
    {
        // Chains are not disposed through the clients (disposeHandler: false) so the shared primary handler is released once.
        _authClient.Dispose();
        _readClient.Dispose();
        _streamClient.Dispose();
        _orderClient.Dispose();
        if (_ownsPrimary)
        {
            _primary.Dispose();
        }
    }

    private static HttpClient Client(AvanzaOptions options, HttpMessageHandler chain)
    {
        var client = new HttpClient(chain, disposeHandler: false)
        {
            BaseAddress = options.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan, // per-attempt timeouts live in the handlers
            MaxResponseContentBufferSize = AvanzaApiClient.MaxResponseBytes,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        return client;
    }
}
