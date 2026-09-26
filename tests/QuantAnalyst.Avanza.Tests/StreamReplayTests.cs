using System.Text.Json.Nodes;
using QuantAnalyst.Avanza.Recording;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Live;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// Gate item: recorded order-depth streams replay through the real parser → Tier A DTO → mapper → quote composer.
/// Every <c>qa-stream-recording/1</c> file under <c>recordings/fixtures/avanza/</c> is replayed, so the owner's
/// sanitized recording is covered as soon as it is committed.
/// </summary>
public sealed class StreamReplayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Recordings()
    {
        var data = new TheoryData<string>();
        foreach (string f in StreamReplay.AllRecordings())
        {
            data.Add(Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "fixtures", "avanza"), f));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Recordings))]
    public async Task EveryStreamRecording_ReplaysWithoutDrift(string recording)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "avanza", recording);
        var id = new OrderbookId(StreamReplay.OrderbookId(path));
        JsonArray events = JsonNode.Parse(File.ReadAllText(path))!["events"]!.AsArray();
        int depthEvents = events.Count(e => e!["event"]!.GetValue<string>() == "ORDER_DEPTH");
        int infoEvents = events.Count(e => e!["event"]!.GetValue<string>() == "info");

        var conn = new SseConnection();
        using var rig = new TestRig();
        await rig.Connection.Authenticator.LoginAsync(Ct);
        rig.Server.Serve(conn);
        await using var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(id, Ct));
        await conn.Send(StreamReplay.ToSse(path));
        await stream.WaitFor(e => e.OfType<DepthEvent>().Count() == depthEvents && e.OfType<StreamHeartbeat>().Count() == infoEvents, "all recorded events");

        Assert.False(stream.Completion.IsFaulted, stream.Completion.Exception?.InnerException?.Message);
        Assert.True(depthEvents > 0, "a recording without ORDER_DEPTH events proves nothing");
        Assert.All(stream.Of<DepthEvent>(), d =>
        {
            Assert.Equal(id, d.Depth.OrderbookId);
            Assert.All(d.Depth.Levels, l => Assert.True(l.BidPrice is null or > 0m && l.AskPrice is null or > 0m));
        });
    }

    [Fact]
    public async Task ProvisionalRecording_ComposesTheExpectedQuote()
    {
        string path = Path.Combine(Fixtures.Directory, "order-depth-stream-5240.json");
        var conn = new SseConnection();
        using var rig = new TestRig();
        await rig.Connection.Authenticator.LoginAsync(Ct);
        rig.Server.Serve(conn);
        var composer = new QuoteComposer(rig.Connection.Gateway, new OrderbookId("5240"), new QuoteComposerOptions(), rig.Time, rig.Logger);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task run = composer.RunAsync(cts.Token);

        await conn.Send(StreamReplay.ToSse(path));
        for (int i = 0; i < 500 && composer.Current is not { BidAskSource: QuoteSource.Stream, Bid: 70.86m }; i++)
        {
            await Task.Delay(10, Ct);
        }

        Quote q = composer.Current!;
        Assert.False(q.IsStale, q.StaleReason);
        Assert.Equal((70.86m, 300m, 70.88m, 1500m), (q.Bid, q.BidVolume, q.Ask, q.AskVolume)); // the last ORDER_DEPTH snapshot
        Assert.Equal(70.86m, q.Last); // from the marketdata poll
        Assert.Equal(2, q.Depth.Count);

        conn.Close(); // the server ends the stream ⇒ reconnecting ⇒ stale
        for (int i = 0; i < 500 && composer.Current is not { IsStale: true }; i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal("depth stream reconnecting (server closed the stream)", composer.Current!.StaleReason);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task LiveStreamIsRecorded_WithoutSecrets_AndSanitizesToAReplayableFixture()
    {
        var conn = new SseConnection();
        using var rig = new TestRig(record: true);
        await rig.Connection.Authenticator.LoginAsync(Ct);
        rig.Server.Serve(conn);
        await using (var stream = new StreamCollector(rig.Connection.Gateway.StreamOrderDepthAsync(new OrderbookId("5240"), Ct)))
        {
            await conn.Event("info", "connected", "srv-7f3a9c", 1000);
            await conn.Depth("5240", 70.84m, 70.86m, "srv-7f3a9d");
            await stream.WaitFor(e => e.OfType<DepthEvent>().Any(), "depth");
        }

        string raw = Directory.GetFiles(rig.Connection.RecordingDirectory!, "*-order-depth-stream.json").Single();
        string text = File.ReadAllText(raw);
        JsonNode doc = JsonNode.Parse(text)!;
        Assert.Equal(StreamRecording.Format, doc["format"]!.GetValue<string>());
        Assert.Equal("/_push/order-depth-web-push/5240", doc["request"]!["path"]!.GetValue<string>());
        Assert.Contains("X-SecurityToken", doc["request"]!["headerNames"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("cancelled", doc["end"]!["reason"]!.GetValue<string>());
        Assert.Equal("<text, 9 chars>", doc["events"]![0]!["data"]!.GetValue<string>()); // info text: length only
        Assert.Equal("5240", doc["events"]![1]!["data"]!["orderbookId"]!.GetValue<string>());
        foreach (string secret in FakeSecrets.All)
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        string output = Path.Combine(rig.Root, "sanitized");
        SanitizeReport report = RecordingSanitizer.Sanitize(rig.Connection.RecordingDirectory!, output, new SanitizeOptions { ForbiddenValues = FakeSecrets.All });
        Assert.True(report.Succeeded, string.Join("; ", report.Problems));
        string sanitized = Directory.GetFiles(output, "*-order-depth-stream.json").Single();
        JsonNode clean = JsonNode.Parse(File.ReadAllText(sanitized))!;
        Assert.Equal(["ev-1", "ev-2"], clean["events"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
        Assert.DoesNotContain("srv-7f3a9", File.ReadAllText(sanitized), StringComparison.Ordinal);
        Assert.Contains("ORDER_DEPTH", StreamReplay.ToSse(sanitized), StringComparison.Ordinal);

        // Idempotent: sanitizing the sanitized folder again changes nothing in the stream file.
        string again = Path.Combine(rig.Root, "again");
        Assert.True(RecordingSanitizer.Sanitize(output, again).Succeeded);
        Assert.Equal(
            clean["events"]!.ToJsonString(),
            JsonNode.Parse(File.ReadAllText(Path.Combine(again, Path.GetFileName(sanitized))))!["events"]!.ToJsonString());
    }

    [Fact]
    public void TheProvisionalFixtureIsListed() =>
        Assert.Contains(StreamReplay.AllRecordings(), f => f.EndsWith("order-depth-stream-5240.json", StringComparison.Ordinal));
}
