using System.Text.Json;
using Microsoft.Extensions.Logging;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Logging;
using QuantAnalyst.Avanza.Streaming;

namespace QuantAnalyst.Avanza.Recording;

/// <summary>
/// Records one stream connection as <c>NNN-&lt;route&gt;.json</c> (format <c>qa-stream-recording/1</c>), written when the
/// connection ends. Like REST recordings it never contains header or cookie values. Event data is kept as JSON when it
/// parses; any other text (e.g. <c>info</c> heartbeats, whose live format is unknown) is kept only as its length.
/// At most <see cref="MaxEvents"/> events are kept per connection; the rest are counted.
/// </summary>
internal sealed class StreamRecording
{
    public const string Format = "qa-stream-recording/1";
    public const int MaxEvents = 10_000;

    private readonly Recorder _recorder;
    private readonly AvanzaRoute _route;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly DateTimeOffset _startedUtc;
    private readonly long _startTicks;
    private readonly string _method;
    private readonly string? _path;
    private readonly string[] _requestHeaderNames;
    private readonly bool _lastEventIdSent;
    private readonly int _status;
    private readonly string? _contentType;
    private readonly string[] _responseHeaderNames;
    private readonly string[] _setCookieNames;
    private readonly List<(long OffsetMs, SseEvent Event)> _events = [];
    private int _dropped;
    private int _written;

    public StreamRecording(Recorder recorder, AvanzaRoute route, HttpRequestMessage request, HttpResponseMessage response, TimeProvider time, ILogger logger)
    {
        _recorder = recorder;
        _route = route;
        _time = time;
        _logger = logger;
        _startedUtc = time.GetUtcNow();
        _startTicks = time.GetTimestamp();
        _method = request.Method.Method;
        _path = request.RequestUri?.OriginalString is { } p && Uri.TryCreate(p, UriKind.RelativeOrAbsolute, out Uri? u)
            ? (u.IsAbsoluteUri ? u.AbsolutePath : p.Split('?')[0])
            : null;
        _requestHeaderNames = [.. request.Headers.Select(h => h.Key)];
        _lastEventIdSent = request.Headers.Contains("Last-Event-ID");
        _status = (int)response.StatusCode;
        _contentType = response.Content.Headers.ContentType?.MediaType;
        _responseHeaderNames = [.. response.Headers.Select(h => h.Key)];
        _setCookieNames = [.. Recorder.CookieNames(response)];
    }

    public void Add(SseEvent e)
    {
        lock (_events)
        {
            if (_events.Count >= MaxEvents)
            {
                _dropped++;
                return;
            }

            _events.Add(((long)_time.GetElapsedTime(_startTicks).TotalMilliseconds, e));
        }
    }

    /// <summary>Writes the file once; later calls do nothing.</summary>
    public async Task CompleteAsync(string endReason)
    {
        if (Interlocked.Exchange(ref _written, 1) == 1)
        {
            return;
        }

        (long OffsetMs, SseEvent Event)[] events;
        int dropped;
        lock (_events)
        {
            events = [.. _events];
            dropped = _dropped;
        }

        string file = _recorder.NextFile(_route.Name, out string fileName);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteString("routesVersion", AvanzaRoutes.RoutesVersion);
            w.WriteString("route", _route.Name);
            w.WriteString("tier", _route.Tier?.ToString() ?? "none");
            w.WriteString("recordedAtUtc", _startedUtc);

            w.WriteStartObject("request");
            w.WriteString("method", _method);
            w.WriteString("path", _path);
            Recorder.WriteNames(w, "headerNames", _requestHeaderNames);
            w.WriteBoolean("lastEventIdSent", _lastEventIdSent);
            w.WriteEndObject();

            w.WriteStartObject("response");
            w.WriteNumber("status", _status);
            w.WriteString("contentType", _contentType);
            Recorder.WriteNames(w, "headerNames", _responseHeaderNames);
            Recorder.WriteNames(w, "setCookieNames", _setCookieNames);
            w.WriteEndObject();

            w.WriteStartObject("end");
            w.WriteString("reason", endReason);
            w.WriteNumber("durationMs", (long)_time.GetElapsedTime(_startTicks).TotalMilliseconds);
            w.WriteNumber("eventsDropped", dropped);
            w.WriteEndObject();

            w.WriteStartArray("events");
            foreach ((long offsetMs, SseEvent e) in events)
            {
                w.WriteStartObject();
                w.WriteNumber("offsetMs", offsetMs);
                w.WriteString("event", e.Event);
                w.WriteString("id", e.Id);
                if (e.Retry is { } retry)
                {
                    w.WriteNumber("retry", retry);
                }
                else
                {
                    w.WriteNull("retry");
                }

                w.WritePropertyName("data");
                WriteData(w, e.Data);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        await File.WriteAllBytesAsync(file, stream.ToArray()).ConfigureAwait(false);
        Log.Recorded(_logger, _route.Name, fileName);
    }

    /// <summary>Placeholder written for non-JSON event data; the replay treats it as opaque text.</summary>
    public static string TextPlaceholder(int length) => $"<text, {length} chars>";

    private static void WriteData(Utf8JsonWriter w, string data)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(data);
            doc.RootElement.WriteTo(w);
        }
        catch (JsonException)
        {
            w.WriteStringValue(TextPlaceholder(data.Length));
        }
    }
}
