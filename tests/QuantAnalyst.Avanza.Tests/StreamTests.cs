using System.Net;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Streaming;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>The order-depth SSE stream through the real pipeline against the fake server (ADR 0002 §3).</summary>
public sealed class StreamTests
{
    private static readonly OrderbookId Eric = new("5240");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestRig> LoggedIn(FakeAvanza? server = null, AvanzaOptions? options = null)
    {
        var rig = new TestRig(server, options);
        await rig.Connection.Authenticator.LoginAsync(Ct);
        return rig;
    }

    private static int StreamRequests(TestRig rig) =>
        rig.Server.Requests.Count(r => r.PathAndQuery.StartsWith(AvanzaRoutes.OrderDepthStream.Path(Eric), StringComparison.Ordinal));

    [Fact]
    public async Task DepthAndHeartbeats_FlowWithTheRightRequestHeaders()
    {
        var conn = new SseConnection();
        using TestRig rig = await LoggedIn();
        rig.Server.Serve(conn);
        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(Eric, Ct));

        await conn.Event("info", "connected", "e1", 1000);
        await conn.Depth("5240", 70.84m, 70.86m, "e2");
        await stream.WaitFor(e => e.OfType<DepthEvent>().Any(), "a depth event");

        Assert.Equal([StreamState.Connecting, StreamState.Connected], stream.Of<StreamStateChanged>().Select(s => s.State));
        Assert.Single(stream.Of<StreamHeartbeat>());
        DepthLevel level = Assert.Single(stream.Of<DepthEvent>().Single().Depth.Levels);
        Assert.Equal(new DepthLevel(70.84m, 100m, 70.86m, 200m), level);

        RecordedRequest request = rig.Server.Requests.Last(r => r.PathAndQuery == AvanzaRoutes.OrderDepthStream.Path(Eric));
        Assert.Equal("GET", request.Method);
        Assert.Equal("text/event-stream", request.Headers["Accept"]);
        Assert.Equal("true", request.Headers["aza-do-not-touch-session"]);
        Assert.Equal("no-cache", request.Headers["Cache-Control"]);
        Assert.Equal("https://www.avanza.se/handla/order.html/kop/5240", request.Headers["Referer"]);
        Assert.Equal(FakeSecrets.SecurityToken, request.Headers["X-SecurityToken"]);
        Assert.Contains("AZACSRF=", request.Headers["Cookie"], StringComparison.Ordinal);
        Assert.False(request.Headers.ContainsKey("Last-Event-ID"));
    }

    [Fact]
    public async Task Reconnect_SendsLastEventId_AfterTheBackoff()
    {
        var first = new SseConnection();
        var second = new SseConnection();
        using TestRig rig = await LoggedIn();
        rig.Server.Serve(first, second);
        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(Eric, Ct));

        await first.Depth("5240", 70.84m, 70.86m, "e41");
        await stream.WaitFor(e => e.OfType<DepthEvent>().Any(), "first depth");
        first.Close();
        await stream.WaitFor(e => e.OfType<StreamStateChanged>().Any(s => s.State == StreamState.Reconnecting), "reconnecting");

        StreamStateChanged reconnecting = stream.Of<StreamStateChanged>().Single(s => s.State == StreamState.Reconnecting);
        Assert.Equal("server closed the stream", reconnecting.Reason);
        Assert.Equal(1, StreamRequests(rig));

        // Base 3 s, n = 0 (the connection delivered), jitter ±20 %: reconnects between 2.4 and 3.6 s.
        TimeSpan waited = await AdvanceUntil(rig, () => second.Connected.Task.IsCompleted, TimeSpan.FromMilliseconds(100), 40);
        Assert.InRange(waited.TotalSeconds, 2.4, 3.7);
        HttpRequestMessage resumed = await second.Connected.Task;
        Assert.Equal("e41", resumed.Headers.GetValues("Last-Event-ID").Single());
        await second.Depth("5240", 70.86m, 70.88m, "e42");
        await stream.WaitFor(e => e.OfType<DepthEvent>().Count() == 2, "second depth");
        Assert.Equal(2, stream.Of<StreamStateChanged>().Count(s => s.State == StreamState.Connected));
    }

    [Fact]
    public async Task ConsecutiveFailures_BackOffExponentially_ThenRecover()
    {
        var ok = new SseConnection();
        using TestRig rig = await LoggedIn();
        rig.Server.ServeStatus(HttpStatusCode.ServiceUnavailable);
        rig.Server.ServeStatus(HttpStatusCode.TooManyRequests);
        rig.Server.ServeStatus(HttpStatusCode.RequestTimeout);
        rig.Server.Serve(ok);
        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(Eric, Ct));

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await stream.WaitFor(e => e.OfType<StreamStateChanged>().Count(s => s.State == StreamState.Reconnecting) == attempt, $"reconnect {attempt}");
            StreamStateChanged r = stream.Of<StreamStateChanged>().Where(s => s.State == StreamState.Reconnecting).Last();
            Assert.StartsWith("HTTP ", r.Reason, StringComparison.Ordinal);
            int requestsBefore = StreamRequests(rig);
            double nominal = 3 * Math.Pow(2, attempt); // 6, 12, 24 s
            TimeSpan waited = await AdvanceUntil(rig, () => StreamRequests(rig) > requestsBefore, TimeSpan.FromMilliseconds(500), 70);
            Assert.InRange(waited.TotalSeconds, (nominal * 0.8) - 0.5, (nominal * 1.2) + 0.5);
        }

        await ok.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await ok.Depth("5240", 1m, 2m);
        await stream.WaitFor(e => e.OfType<DepthEvent>().Any(), "recovered");
        Assert.Equal(4, StreamRequests(rig));
    }

    private static void ServeRefusal(FakeAvanza server, HttpStatusCode status, int? retryAfterSeconds, string body = "<html>Too many requests</html>") =>
        server.On(AvanzaRoutes.OrderDepthStream, _ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html") };
            response.Headers.Server.ParseAdd("TestEdge/1.0");
            if (retryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        });

    [Fact]
    public async Task ARefusal_WaitsAtLeastTheServersRetryAfter_AndSaysWhoRefused()
    {
        using TestRig rig = await LoggedIn();
        ServeRefusal(rig.Server, HttpStatusCode.TooManyRequests, retryAfterSeconds: 45);
        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(Eric, Ct));

        await stream.WaitFor(e => e.OfType<StreamStateChanged>().Any(s => s.State == StreamState.Reconnecting), "reconnecting");
        StreamStateChanged r = stream.Of<StreamStateChanged>().Single(s => s.State == StreamState.Reconnecting);
        Assert.Equal("HTTP 429 from TestEdge/1.0, Retry-After 45 s", r.Reason);
        (TimeSpan delay, _) = Delay(rig);
        Assert.Equal(45, delay.TotalSeconds, 3); // not the 6 s backoff: the server asked for 45
    }

    [Fact]
    public async Task Refusals_AreRecorded_TheFirstFewOnly()
    {
        using var rig = new TestRig(record: true);
        await rig.Connection.Authenticator.LoginAsync(Ct);
        for (int i = 0; i < 5; i++)
        {
            ServeRefusal(rig.Server, HttpStatusCode.TooManyRequests, retryAfterSeconds: 1, body: "<html>Access denied, reference 18.abc</html>");
        }

        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(Eric, Ct));
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            await stream.WaitFor(e => e.OfType<StreamStateChanged>().Count(s => s.State == StreamState.Reconnecting) == attempt, $"refusal {attempt}");
            if (attempt < 5)
            {
                int before = StreamRequests(rig);
                await AdvanceUntil(rig, () => StreamRequests(rig) > before, TimeSpan.FromSeconds(1), 60);
            }
        }

        string[] files = [.. Directory.EnumerateFiles(Path.Combine(rig.Root, "recordings"), "*order-depth-stream*.json", SearchOption.AllDirectories)];
        Assert.Equal(AvanzaStreamClient.RecordedRefusals, files.Length);
        string recorded = File.ReadAllText(files[0]);
        Assert.Contains("\"status\": 429", recorded, StringComparison.Ordinal);
        Assert.Contains("Access denied, reference 18.abc", recorded, StringComparison.Ordinal); // what refused it
        Assert.Contains("Retry-After", recorded, StringComparison.Ordinal); // header names only
        Assert.DoesNotContain(FakeSecrets.SecurityToken, recorded, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 3.0)]
    [InlineData(1, 6.0)]
    [InlineData(2, 12.0)]
    [InlineData(3, 24.0)]
    [InlineData(4, 30.0)]
    [InlineData(9, 30.0)]
    public void Backoff_DoublesPerFailure_WithJitter_CappedAt30s(int failures, double nominalSeconds)
    {
        TimeSpan cap = TimeSpan.FromSeconds(30);
        TimeSpan low = AvanzaStreamClient.Backoff(TimeSpan.FromSeconds(3), failures, cap, 0.0);
        TimeSpan high = AvanzaStreamClient.Backoff(TimeSpan.FromSeconds(3), failures, cap, 0.999999);
        Assert.Equal(nominalSeconds * 0.8, low.TotalSeconds, 3);
        Assert.Equal(Math.Min(nominalSeconds * 1.2, 30.0), high.TotalSeconds, 3);
        Assert.True(high <= cap);
        Assert.Equal(12.0, AvanzaStreamClient.Backoff(TimeSpan.FromSeconds(10), 0, cap, 1.0).TotalSeconds, 3); // server retry 10 s
    }

    [Fact]
    public async Task ServerRetry_RaisesTheBaseDelay()
    {
        var first = new SseConnection();
        using TestRig rig = await LoggedIn();
        rig.Server.Serve(first);
        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(Eric, Ct));
        await first.Event("info", "connected", "e1", retry: 10_000);
        await stream.WaitFor(e => e.OfType<StreamHeartbeat>().Any(), "heartbeat");
        first.Close();
        await stream.WaitFor(e => e.OfType<StreamStateChanged>().Any(s => s.State == StreamState.Reconnecting), "reconnecting");
        (TimeSpan delay, _) = Delay(rig);
        Assert.InRange(delay.TotalSeconds, 8.0, 12.0);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(SessionExpiredException))]
    [InlineData(HttpStatusCode.Forbidden, typeof(SessionExpiredException))]
    [InlineData(HttpStatusCode.NotFound, typeof(EndpointGoneException))]
    [InlineData(HttpStatusCode.BadRequest, typeof(SchemaDriftException))]
    public async Task TerminalStatuses_StopWithoutReconnecting(HttpStatusCode status, Type expected)
    {
        using TestRig rig = await LoggedIn();
        rig.Server.ServeStatus(status);
        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(Eric, Ct));
        Exception ex = await Assert.ThrowsAnyAsync<BrokerException>(() => stream.Completion);
        Assert.IsType(expected, ex);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50, Ct);
        Assert.Equal(1, StreamRequests(rig));
        Assert.Contains(((CapturingLogger)rig.Logger).Lines, l => l.Contains("stream order-depth-stream stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotAnEventStream_IsDrift()
    {
        using TestRig rig = await LoggedIn();
        rig.Server.Serve(new SseConnection(contentType: "application/json"));
        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(Eric, Ct));
        var ex = await Assert.ThrowsAsync<SchemaDriftException>(() => stream.Completion);
        Assert.Equal(["$http.contentType"], ex.Paths);
    }

    [Theory]
    [InlineData("""{"orderbookId":"5240","levels":[{"buyPrice":1,"buyVolume":1,"sellPrice":2,"sellVolume":1,"newField":3}]}""", "$.levels[0].newField")]
    [InlineData("""{"orderbookId":"5240"}""", "$.levels")]
    [InlineData("""{"orderbookId":"9999","levels":[]}""", "$.orderbookId")]
    [InlineData("""{"orderbookId":"5240","levels":[{"buyPrice":0,"buyVolume":5}]}""", "$.levels[0].buyPrice")]
    [InlineData("not json", "$")]
    public async Task TierADrift_OnADepthEvent_StopsTheStream(string payload, string path)
    {
        var conn = new SseConnection();
        using TestRig rig = await LoggedIn();
        rig.Server.Serve(conn);
        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(Eric, Ct));
        await conn.Event("ORDER_DEPTH", payload, "e1");
        var ex = await Assert.ThrowsAsync<SchemaDriftException>(() => stream.Completion);
        Assert.Contains(path, ex.Paths);
        Assert.True(ex.HaltsTrading);
    }

    [Fact]
    public async Task UnknownEventName_IsDrift()
    {
        var conn = new SseConnection();
        using TestRig rig = await LoggedIn();
        rig.Server.Serve(conn);
        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(Eric, Ct));
        await conn.Event("TRADES<script>", "{}");
        var ex = await Assert.ThrowsAsync<SchemaDriftException>(() => stream.Completion);
        Assert.Equal(["$event"], ex.Paths);
        Assert.Contains("'TRADESscript'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptySidesMapToNull_NeverToAZeroPrice()
    {
        var conn = new SseConnection();
        using TestRig rig = await LoggedIn();
        rig.Server.Serve(conn);
        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(Eric, Ct));
        await conn.Event("ORDER_DEPTH", """{"orderbookId":"5240","levels":[{"buyPrice":70.8,"buyVolume":400,"sellPrice":0,"sellVolume":0},{"buyPrice":null,"sellPrice":70.9,"sellVolume":10}]}""");
        await stream.WaitFor(e => e.OfType<DepthEvent>().Any(), "depth");
        Assert.Equal(
            [new DepthLevel(70.8m, 400m, null, 0m), new DepthLevel(null, 0m, 70.9m, 10m)],
            stream.Of<DepthEvent>().Single().Depth.Levels);
    }

    [Fact]
    public async Task IdleConnection_IsDroppedAndReconnected()
    {
        var quiet = new SseConnection();
        using TestRig rig = await LoggedIn();
        rig.Server.Serve(quiet, new SseConnection());
        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(Eric, Ct));
        await quiet.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await stream.WaitFor(e => e.OfType<StreamStateChanged>().Any(s => s.State == StreamState.Connected), "connected");
        await Task.Delay(50, Ct); // let the read (and its idle timer) start
        TimeSpan waited = await AdvanceUntil(
            rig, () => stream.Of<StreamStateChanged>().Any(s => s.State == StreamState.Reconnecting), TimeSpan.FromSeconds(5), 20);
        Assert.InRange(waited.TotalSeconds, 60, 65);
        Assert.Equal("no data for 60 s", stream.Of<StreamStateChanged>().Single(s => s.State == StreamState.Reconnecting).Reason);
    }

    [Fact]
    public async Task OversizedEvent_IsAProtocolError_AndReconnects()
    {
        var conn = new SseConnection();
        using TestRig rig = await LoggedIn(options: new AvanzaOptions { StreamMaxEventChars = 1024 });
        rig.Server.Serve(conn);
        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(Eric, Ct));
        await conn.Send("data: " + new string('x', 5000) + "\n\n");
        await stream.WaitFor(e => e.OfType<StreamStateChanged>().Any(s => s.State == StreamState.Reconnecting), "reconnecting");
        Assert.Contains("exceeds", stream.Of<StreamStateChanged>().Single(s => s.State == StreamState.Reconnecting).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingTheConsumer_ClosesTheStream_AndNoFurtherRequestsAreMade()
    {
        var conn = new SseConnection();
        using TestRig rig = await LoggedIn();
        rig.Server.Serve(conn);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        int seen = 0;
        await conn.Depth("5240", 1m, 2m);
        await foreach (MarketStreamEvent e in rig.Connection.Gateway.StreamOrderDepthAsync(Eric, cts.Token))
        {
            if (e is DepthEvent && ++seen == 1)
            {
                break;
            }
        }

        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50, Ct);
        Assert.Equal(1, StreamRequests(rig));
    }

    /// <summary>Advances fake time in steps until <paramref name="done"/>; returns how much time it took.</summary>
    private static async Task<TimeSpan> AdvanceUntil(TestRig rig, Func<bool> done, TimeSpan step, int maxSteps)
    {
        TimeSpan advanced = TimeSpan.Zero;
        for (int i = 0; i < maxSteps && !done(); i++)
        {
            rig.Time.Advance(step);
            advanced += step;
            for (int j = 0; j < 20 && !done(); j++)
            {
                await Task.Delay(5, Ct);
            }
        }

        Assert.True(done(), $"condition not met after advancing {advanced}");
        return advanced;
    }

    private static (TimeSpan Delay, string Reason) Delay(TestRig rig)
    {
        string line = ((CapturingLogger)rig.Logger).Lines.Last(l => l.Contains("reconnecting in", StringComparison.Ordinal));
        string ms = line[(line.IndexOf("reconnecting in ", StringComparison.Ordinal) + 16)..].Split(' ')[0];
        return (TimeSpan.FromMilliseconds(long.Parse(ms, System.Globalization.CultureInfo.InvariantCulture)), line);
    }
}
