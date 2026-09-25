using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Avanza.Credentials;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Logging;
using QuantAnalyst.Core;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>Fake credentials. Every value is distinctive so the log/recording scans can search for it.</summary>
internal static class FakeSecrets
{
    public const string Username = "user-FAKE-7d1e";
    public const string Password = "pw-FAKE-9f3c-Hunter2";
    public const string TotpSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    public const string SecurityToken = "tok-FAKE-5b0c2f7e9a";
    public const string CsrfCookie = "csrf-FAKE-44aa19";
    public const string SessionCookie = "sess-FAKE-c0ffee";
    public const string AuthenticationSession = "authsess-FAKE-8812";
    public const string PushSubscriptionId = "push-FAKE-3310";
    public const string CustomerId = "cust-FAKE-1177";

    public static readonly string[] All =
        [Username, Password, TotpSecret, SecurityToken, CsrfCookie, SessionCookie, AuthenticationSession, PushSubscriptionId, CustomerId];

    public static InMemorySecretStore Store(string totpSecret = TotpSecret) => new(new AvanzaCredentials(
        new Secret(Username), new Secret(Password), new Secret(totpSecret)));
}

internal sealed class InMemorySecretStore(AvanzaCredentials? credentials) : ISecretStore
{
    public string Name => "in-memory test store";

    public int Reads { get; private set; }

    public AvanzaCredentials GetAvanzaCredentials()
    {
        Reads++;
        return credentials ?? throw new SecretStoreException("Missing in test store: everything.");
    }
}

internal sealed record RecordedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>
/// In-process fake of the Avanza endpoints used in Phase 3, serving the provisional fixtures. Login succeeds by
/// default (header token + AZACSRF cookie); reads require the security token. Override any route with <see cref="On"/>.
/// </summary>
internal sealed class FakeAvanza : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>> _overrides = new(StringComparer.Ordinal);
    private readonly List<RecordedRequest> _requests = [];

    public bool SendTokenHeader { get; set; } = true;

    public bool SendCsrfCookie { get; set; } = true;

    public string TwoFactorMethod { get; set; } = "TOTP";

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public int CountFor(AvanzaRoute route) => Requests.Count(r => r.PathAndQuery.StartsWith(route.PathTemplate.Split('{')[0], StringComparison.Ordinal));

    /// <summary>Queues one-shot responses for a path prefix (consumed in order, then back to default).</summary>
    public FakeAvanza On(string pathPrefix, params Func<HttpRequestMessage, HttpResponseMessage>[] responses) =>
        OnAsync(pathPrefix, [.. responses.Select(r => (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)((req, _) => Task.FromResult(r(req))))]);

    public FakeAvanza OnAsync(string pathPrefix, params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] responses)
    {
        if (!_overrides.TryGetValue(pathPrefix, out Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>? q))
        {
            _overrides[pathPrefix] = q = new();
        }

        foreach (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> r in responses)
        {
            q.Enqueue(r);
        }

        return this;
    }

    public FakeAvanza OnAsync(AvanzaRoute route, params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] responses) =>
        OnAsync(route.PathTemplate.Split('{')[0], responses);

    public FakeAvanza On(AvanzaRoute route, params Func<HttpRequestMessage, HttpResponseMessage>[] responses) =>
        On(route.PathTemplate.Split('{')[0], responses);

    public static HttpResponseMessage Status(HttpStatusCode code, string body = "{}") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Json(string body) => Status(HttpStatusCode.OK, body);

    public static HttpResponseMessage Json(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } } };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.PathAndQuery;
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (_requests)
        {
            _requests.Add(new RecordedRequest(request.Method.Method, path, headers, body));
        }

        foreach ((string prefix, Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> queue) in _overrides)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal) && queue.Count > 0)
            {
                return await queue.Dequeue()(request, cancellationToken);
            }
        }

        return Default(request, path, headers);
    }

    private HttpResponseMessage Default(HttpRequestMessage request, string path, Dictionary<string, string> headers)
    {
        if (path == AvanzaRoutes.UserCredentials.Path())
        {
            HttpResponseMessage r = Json($$$"""{"twoFactorLogin":{"method":"{{{TwoFactorMethod}}}","transactionId":"tx-1"}}""");
            r.Headers.Add("Set-Cookie", $"AZAPERSISTENCE={FakeSecrets.SessionCookie}; Path=/; Secure; HttpOnly");
            return r;
        }

        if (path == AvanzaRoutes.Totp.Path())
        {
            HttpResponseMessage r = Json($$"""
                {"authenticationSession":"{{FakeSecrets.AuthenticationSession}}","pushSubscriptionId":"{{FakeSecrets.PushSubscriptionId}}","customerId":"{{FakeSecrets.CustomerId}}","registrationComplete":true}
                """);
            if (SendTokenHeader)
            {
                r.Headers.Add("X-SecurityToken", FakeSecrets.SecurityToken);
            }

            if (SendCsrfCookie)
            {
                r.Headers.Add("Set-Cookie", $"AZACSRF={FakeSecrets.CsrfCookie}; Path=/; Secure");
            }

            return r;
        }

        bool isPublic = path.StartsWith(AvanzaRoutes.Search.Path(), StringComparison.Ordinal)
                        || path.StartsWith(AvanzaRoutes.PriceChart.PathTemplate.Split('{')[0], StringComparison.Ordinal);
        string expectedToken = SendTokenHeader ? FakeSecrets.SecurityToken : FakeSecrets.CsrfCookie;
        if (!isPublic && (!headers.TryGetValue("X-SecurityToken", out string? token) || token != expectedToken))
        {
            return Status(HttpStatusCode.Unauthorized);
        }

        string p = request.RequestUri!.AbsolutePath;
        if (p == AvanzaRoutes.SessionInfo.Path())
        {
            return Json(Fixtures.Mutate("session-info.json", n =>
            {
                n["user"]!["securityToken"] = FakeSecrets.SecurityToken;
                n["user"]!["pushSubscriptionId"] = FakeSecrets.PushSubscriptionId;
            }));
        }

        return p switch
        {
            _ when p == AvanzaRoutes.AccountsOverview.Path() => Json(Fixtures.Bytes("accounts-overview.json")),
            _ when p == AvanzaRoutes.TradingAccounts.Path() => Json(Fixtures.Bytes("trading-accounts.json")),
            _ when p == AvanzaRoutes.Positions.Path() => Json(Fixtures.Bytes("positions.json")),
            _ when p == AvanzaRoutes.Orders.Path() => Json(Fixtures.Bytes("orders.json")),
            _ when p == AvanzaRoutes.Deals.Path() => Json("""{"deals":[{"dealId":"d-1","accountId":"9990001"}]}"""),
            _ when p == AvanzaRoutes.Transactions.Path() => Json(Fixtures.Bytes("transactions.json")),
            _ when p == AvanzaRoutes.Search.Path() => Json(Fixtures.Bytes("search-eric.json")),
            _ when p == AvanzaRoutes.Orderbook.Path("5240") => Json(Fixtures.Bytes("orderbook-5240.json")),
            _ when p == AvanzaRoutes.Orderbook.Path("5239") => Json(Fixtures.Mutate("orderbook-5240.json", n =>
            {
                n["id"] = "5239";
                n["name"] = "Ericsson A";
                n["tickerSymbol"] = "ERIC A";
            })),
            _ when p == AvanzaRoutes.MarketData.Path("5240") => Json(Fixtures.Bytes("marketdata-5240.json")),
            _ when p == AvanzaRoutes.PriceChart.Path("5240") => Json(Fixtures.Bytes("price-chart-5240.json")),
            _ => Status(HttpStatusCode.NotFound),
        };
    }
}

/// <summary>Builds a connection against <see cref="FakeAvanza"/> with fake time and a temp state folder.</summary>
internal sealed class TestRig : IDisposable
{
    /// <param name="realisticRateLimit">
    /// False (default) raises the token bucket to its maximum (10/s, burst 20) so tests that do not advance fake
    /// time are not paced; rate limiting itself is tested with true.
    /// </param>
    public TestRig(FakeAvanza? server = null, AvanzaOptions? options = null, ISecretStore? secrets = null, bool record = false, ILogger? logger = null, bool realisticRateLimit = false)
    {
        Server = server ?? new FakeAvanza();
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 7, 0, 10, TimeSpan.Zero)); // 10 s into a TOTP window
        Root = Path.Combine(Path.GetTempPath(), "qa-avanza-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Secrets = secrets ?? FakeSecrets.Store();
        Logger = logger ?? new CapturingLogger();
        Options = options ?? new AvanzaOptions();
        Options = new AvanzaOptions
        {
            BaseAddress = Options.BaseAddress,
            MaxInactiveMinutes = Options.MaxInactiveMinutes,
            RequestsPerSecond = realisticRateLimit ? Options.RequestsPerSecond : 10,
            Burst = realisticRateLimit ? Options.Burst : 20,
            AttemptTimeout = Options.AttemptTimeout,
            MaxReadRetries = Options.MaxReadRetries,
            RetryBaseDelay = Options.RetryBaseDelay,
            MaxRetryAfter = Options.MaxRetryAfter,
            CircuitWindow = Options.CircuitWindow,
            CircuitMinimumCalls = Options.CircuitMinimumCalls,
            CircuitFailureRatio = Options.CircuitFailureRatio,
            CircuitBreakDuration = Options.CircuitBreakDuration,
            StateDirectory = Path.Combine(Root, "state"),
            RecordingDirectory = record ? Path.Combine(Root, "recordings") : null,
        };
        Connection = NewConnection();
    }

    public FakeAvanza Server { get; }

    public FakeTimeProvider Time { get; }

    public string Root { get; }

    public ISecretStore Secrets { get; }

    public ILogger Logger { get; }

    public Redactor Redactor { get; } = new();

    public AvanzaOptions Options { get; }

    public AvanzaConnection Connection { get; private set; }

    /// <summary>A fresh connection = a fresh trigger (same state folder, same server).</summary>
    public AvanzaConnection NewConnection()
    {
        Connection = AvanzaConnection.CreateForTest(Options, Secrets, Logger, Redactor, Time, Server);
        return Connection;
    }

    /// <summary>Runs an async operation while advancing fake time (for delays/timeouts), up to <paramref name="limit"/>.</summary>
    public async Task<T> Run<T>(Func<Task<T>> operation, TimeSpan? step = null, TimeSpan? limit = null)
    {
        Task<T> task = operation();
        TimeSpan advanced = TimeSpan.Zero;
        TimeSpan s = step ?? TimeSpan.FromMilliseconds(250);
        TimeSpan max = limit ?? TimeSpan.FromMinutes(5);
        while (!task.IsCompleted && advanced < max)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
            if (!task.IsCompleted)
            {
                Time.Advance(s);
                advanced += s;
            }
        }

        return await task;
    }

    public void Dispose()
    {
        Connection.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
