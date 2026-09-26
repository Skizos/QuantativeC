using System.Net;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza.Tests;

public sealed class PipelineTests
{
    private static async Task<TestRig> LoggedIn(FakeAvanza? server = null, AvanzaOptions? options = null)
    {
        var rig = new TestRig(server, options);
        await rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken);
        return rig;
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Read_401Or403_IsSessionExpired_WithoutRetry(HttpStatusCode status)
    {
        using TestRig rig = await LoggedIn();
        rig.Server.On(AvanzaRoutes.Positions, _ => FakeAvanza.Status(status));
        var ex = await Assert.ThrowsAsync<SessionExpiredException>(() => rig.Connection.Gateway.GetPositionsAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal((int)status, ex.StatusCode);
        Assert.Equal(1, rig.Server.CountFor(AvanzaRoutes.Positions));
    }

    [Fact]
    public async Task Read_404_IsEndpointGoneOnTierA_AndUnavailableOnTierB()
    {
        using TestRig rig = await LoggedIn();
        rig.Server.On(AvanzaRoutes.Orders, _ => FakeAvanza.Status(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<EndpointGoneException>(() => rig.Connection.Gateway.GetOpenOrdersAsync(TestContext.Current.CancellationToken));

        var ex = await Assert.ThrowsAsync<BrokerUnavailableException>(() =>
            rig.Connection.Gateway.GetPriceHistoryAsync(new OrderbookId("1"), Core.Market.ChartPeriod.OneMonth, null, TestContext.Current.CancellationToken));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task Read_TransientFailure_IsRetriedThenSucceeds()
    {
        using TestRig rig = await LoggedIn();
        rig.Server.On(AvanzaRoutes.Positions, _ => FakeAvanza.Status(HttpStatusCode.ServiceUnavailable), _ => throw new HttpRequestException("reset"));
        var snapshot = await rig.Run(() => rig.Connection.Gateway.GetPositionsAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal(2, snapshot.Positions.Count);
        Assert.Equal(3, rig.Server.CountFor(AvanzaRoutes.Positions));
    }

    [Fact]
    public async Task Read_PersistentFailure_StopsAfterMaxRetries()
    {
        using TestRig rig = await LoggedIn();
        rig.Server.On(AvanzaRoutes.Positions, Enumerable.Repeat<Func<HttpRequestMessage, HttpResponseMessage>>(_ => FakeAvanza.Status(HttpStatusCode.BadGateway), 5).ToArray());
        var ex = await Assert.ThrowsAsync<BrokerUnavailableException>(() => rig.Run(() => rig.Connection.Gateway.GetPositionsAsync(null, TestContext.Current.CancellationToken)));
        Assert.Equal(502, ex.StatusCode);
        Assert.Equal(3, rig.Server.CountFor(AvanzaRoutes.Positions)); // 1 + MaxReadRetries (2)
    }

    [Fact]
    public async Task Read_ClientError_IsNotRetried()
    {
        using TestRig rig = await LoggedIn();
        rig.Server.On(AvanzaRoutes.Positions, _ => FakeAvanza.Status(HttpStatusCode.BadRequest));
        var ex = await Assert.ThrowsAsync<BrokerUnavailableException>(() => rig.Connection.Gateway.GetPositionsAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(1, rig.Server.CountFor(AvanzaRoutes.Positions));
    }

    [Fact]
    public async Task Read_RetryAfter_IsHonoured_AndRejectedAboveTheCap()
    {
        using TestRig rig = await LoggedIn();
        rig.Server.On(AvanzaRoutes.Positions, _ =>
        {
            HttpResponseMessage r = FakeAvanza.Status(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(4));
            return r;
        });
        DateTimeOffset start = rig.Time.GetUtcNow();
        await rig.Run(() => rig.Connection.Gateway.GetPositionsAsync(null, TestContext.Current.CancellationToken));
        Assert.True(rig.Time.GetUtcNow() - start >= TimeSpan.FromSeconds(4));

        rig.Server.On(AvanzaRoutes.Orders, _ =>
        {
            HttpResponseMessage r = FakeAvanza.Status(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return r;
        });
        var ex = await Assert.ThrowsAsync<BrokerUnavailableException>(() => rig.Connection.Gateway.GetOpenOrdersAsync(TestContext.Current.CancellationToken));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(1, rig.Server.CountFor(AvanzaRoutes.Orders));
    }

    [Fact]
    public async Task Read_AttemptTimeout_IsReportedAsUnavailable()
    {
        using TestRig rig = await LoggedIn(options: new AvanzaOptions { MaxReadRetries = 0 });
        rig.Server.OnAsync(AvanzaRoutes.Orders, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // hangs until the 15 s attempt timeout (fake time) cancels it
            return FakeAvanza.Status(HttpStatusCode.OK);
        });
        DateTimeOffset start = rig.Time.GetUtcNow();
        var ex = await Assert.ThrowsAsync<BrokerUnavailableException>(() => rig.Run(() => rig.Connection.Gateway.GetOpenOrdersAsync(TestContext.Current.CancellationToken)));
        Assert.Contains("timeout", ex.Message, StringComparison.Ordinal);
        Assert.True(rig.Time.GetUtcNow() - start >= TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterRepeatedFailures_ThenHalfOpensOnce()
    {
        using TestRig rig = await LoggedIn(options: new AvanzaOptions { MaxReadRetries = 0 });
        rig.Server.On(AvanzaRoutes.Positions, Enumerable.Repeat<Func<HttpRequestMessage, HttpResponseMessage>>(_ => FakeAvanza.Status(HttpStatusCode.ServiceUnavailable), 5).ToArray());
        for (int i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<BrokerUnavailableException>(() => rig.Connection.Gateway.GetPositionsAsync(null, TestContext.Current.CancellationToken));
        }

        int before = rig.Server.Requests.Count;
        var open = await Assert.ThrowsAsync<BrokerUnavailableException>(() => rig.Connection.Gateway.GetOpenOrdersAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Circuit breaker is open", open.Message, StringComparison.Ordinal);
        Assert.Equal(before, rig.Server.Requests.Count); // no HTTP while open

        rig.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Single(await rig.Connection.Gateway.GetOpenOrdersAsync(TestContext.Current.CancellationToken)); // half-open probe succeeds
        Assert.Single(await rig.Connection.Gateway.GetOpenOrdersAsync(TestContext.Current.CancellationToken)); // closed again
    }

    [Fact]
    public void TokenBucket_AllowsBurstThenPacesAtTheRate()
    {
        var time = new FakeTimeProvider();
        var bucket = new TokenBucket(2.0, 5, time);
        TimeSpan[] waits = [.. Enumerable.Range(0, 7).Select(_ => bucket.Reserve())];
        Assert.All(waits.Take(5), w => Assert.Equal(TimeSpan.Zero, w));
        Assert.Equal(TimeSpan.FromSeconds(0.5), waits[5]);
        Assert.Equal(TimeSpan.FromSeconds(1.0), waits[6]);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.Zero, bucket.Reserve());
    }

    [Fact]
    public async Task RateLimit_AppliesToLoginAndReadsTogether()
    {
        using var rig = new TestRig(options: new AvanzaOptions { RequestsPerSecond = 1, Burst = 2 }, realisticRateLimit: true);
        DateTimeOffset start = rig.Time.GetUtcNow();
        await rig.Run(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
        await rig.Run(() => rig.Connection.Gateway.GetOpenOrdersAsync(TestContext.Current.CancellationToken));
        await rig.Run(() => rig.Connection.Gateway.GetOpenOrdersAsync(TestContext.Current.CancellationToken));
        Assert.True(rig.Time.GetUtcNow() - start >= TimeSpan.FromSeconds(2), "4 calls at 1 req/s with burst 2 need >= 2 s");
    }
}
