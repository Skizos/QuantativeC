using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>One fake server-sent-event connection: the test writes the body while the client reads it.</summary>
internal sealed class SseConnection
{
    private readonly Pipe _pipe = new();

    public SseConnection(HttpStatusCode status = HttpStatusCode.OK, string contentType = "text/event-stream")
    {
        Response = new HttpResponseMessage(status) { Content = new StreamContent(_pipe.Reader.AsStream()) };
        Response.Content.Headers.ContentType = new(contentType);
    }

    public HttpResponseMessage Response { get; }

    /// <summary>Set when the client connected (the server handed out this response).</summary>
    public TaskCompletionSource<HttpRequestMessage> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task Send(string text)
    {
        await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
        await _pipe.Writer.FlushAsync();
    }

    public Task Event(string name, string data, string? id = null, int? retry = null) => Send(Format(name, data, id, retry));

    public Task Depth(string orderbookId, decimal bid, decimal ask, string? id = null) =>
        Event("ORDER_DEPTH", string.Create(CultureInfo.InvariantCulture,
            $$"""{"orderbookId":"{{orderbookId}}","levels":[{"buyPrice":{{bid}},"buyVolume":100,"sellPrice":{{ask}},"sellVolume":200}],"marketMakerLevelInAsk":0,"marketMakerLevelInBid":0}"""), id);

    public void Close() => _pipe.Writer.Complete();

    public static string Format(string name, string data, string? id, int? retry)
    {
        var sb = new StringBuilder();
        if (id is not null)
        {
            sb.Append("id: ").Append(id).Append('\n');
        }

        sb.Append("event: ").Append(name).Append('\n');
        if (retry is { } r)
        {
            sb.Append(CultureInfo.InvariantCulture, $"retry: {r}\n");
        }

        foreach (string line in data.Split('\n'))
        {
            sb.Append("data: ").Append(line).Append('\n');
        }

        return sb.Append('\n').ToString();
    }
}

internal static class SseServer
{
    /// <summary>Queues the connections the fake answers for the order-depth stream route, in order.</summary>
    public static void Serve(this FakeAvanza server, params SseConnection[] connections) =>
        server.OnAsync(AvanzaRoutes.OrderDepthStream, [.. connections.Select(c => (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)((req, _) =>
        {
            c.Connected.TrySetResult(req);
            return Task.FromResult(c.Response);
        }))]);

    /// <summary>A queued plain HTTP answer on the stream route (e.g. 401, 503).</summary>
    public static void ServeStatus(this FakeAvanza server, HttpStatusCode status, string contentType = "application/json") =>
        server.On(AvanzaRoutes.OrderDepthStream, _ => new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, contentType) });
}

/// <summary>Replays a <c>qa-stream-recording/1</c> file as SSE text (the same framing Avanza sent).</summary>
internal static class StreamReplay
{
    public static string OrderbookId(string recordingPath)
    {
        string path = JsonNode.Parse(File.ReadAllText(recordingPath))!["request"]!["path"]!.GetValue<string>();
        return path[(path.LastIndexOf('/') + 1)..];
    }

    public static string ToSse(string recordingPath)
    {
        JsonNode doc = JsonNode.Parse(File.ReadAllText(recordingPath))!;
        Assert.Equal("qa-stream-recording/1", doc["format"]!.GetValue<string>());
        var sb = new StringBuilder();
        foreach (JsonNode? e in doc["events"]!.AsArray())
        {
            JsonNode data = e!["data"]!;
            string text = data.GetValueKind() == JsonValueKind.String ? data.GetValue<string>() : data.ToJsonString();
            int? retry = e["retry"] is JsonValue r && r.GetValueKind() == JsonValueKind.Number ? r.GetValue<int>() : null;
            sb.Append(SseConnection.Format(e["event"]!.GetValue<string>(), text, e["id"]?.GetValue<string>(), retry));
        }

        return sb.ToString();
    }

    /// <summary>Every stream recording under the fixtures folder (provisional and dated owner recordings).</summary>
    public static IEnumerable<string> AllRecordings() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "avanza"), "*.json", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("\"qa-stream-recording/1\"", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
}

/// <summary>Collects a stream's events in the background so the test can advance fake time meanwhile.</summary>
internal sealed class StreamCollector : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<MarketStreamEvent> _events = [];

    public StreamCollector(IAsyncEnumerable<MarketStreamEvent> stream)
    {
        Completion = Task.Run(async () =>
        {
            await foreach (MarketStreamEvent e in stream.WithCancellation(_cts.Token))
            {
                lock (_events)
                {
                    _events.Add(e);
                }
            }
        });
    }

    public Task Completion { get; }

    public IReadOnlyList<MarketStreamEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<T> Of<T>() => [.. Events.OfType<T>()];

    public async Task WaitFor(Func<IReadOnlyList<MarketStreamEvent>, bool> condition, string because)
    {
        for (int i = 0; i < 500; i++)
        {
            if (condition(Events) || Completion.IsCompleted)
            {
                break;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(Events), $"Timed out waiting: {because}. Completion: {Completion.Status} {Completion.Exception?.InnerException?.Message}");
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await Completion;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Core.Broker.BrokerException)
        {
            // The test asserts terminal failures through Completion itself.
        }

        _cts.Dispose();
    }
}
