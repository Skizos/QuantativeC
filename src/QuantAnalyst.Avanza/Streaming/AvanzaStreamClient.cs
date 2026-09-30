using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Logging;
using QuantAnalyst.Avanza.Recording;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Avanza.Streaming;

/// <summary>What the raw stream loop reports: a state change or one parsed event.</summary>
internal abstract record StreamItem(DateTimeOffset ReceivedUtc);

internal sealed record StreamStateItem(StreamState State, string? Reason, TimeSpan? Delay, DateTimeOffset ReceivedUtc) : StreamItem(ReceivedUtc);

internal sealed record StreamMessage(SseEvent Event, DateTimeOffset ReceivedUtc) : StreamItem(ReceivedUtc);

/// <summary>
/// Server-sent-event connection with reconnects (ADR 0002 §3; protocol from the Go SDK <c>internal/sse/subscription.go</c>):
/// <list type="bullet">
/// <item>GET with <c>Accept: text/event-stream</c>, <c>aza-do-not-touch-session: true</c>, the page <c>Referer</c>, cookies and
/// token (from the pipeline) and <c>Last-Event-ID</c> after the first event id</item>
/// <item>headers must arrive within <see cref="AvanzaOptions.AttemptTimeout"/>; afterwards an idle watchdog
/// (<see cref="AvanzaOptions.StreamIdleTimeout"/>) replaces the HTTP timeout</item>
/// <item>reconnect delay <c>max(server retry, 3 s) · 2^min(n,5)</c>, capped at 30 s, ±20 % jitter; <c>n</c> counts consecutive
/// connections that delivered no event</item>
/// <item>transport errors, a closed stream, 408, 429, 5xx and protocol errors reconnect; 401/403 (session expired), 404
/// (endpoint gone), any other status and a wrong content type (schema drift) stop the stream by throwing</item>
/// <item>a refusal (408, 429, 5xx) waits at least the server's <c>Retry-After</c> (at most <see cref="MaxRetryAfter"/>); the
/// log line names it and the answering <c>Server</c>, and the first <see cref="RecordedRefusals"/> refusals are recorded
/// when recording is on, so a block can be told from a rate limit</item>
/// </list>
/// Never used for anything but GET streams; the pipeline behind it has no retry handler (this loop is the retry).
/// </summary>
internal sealed class AvanzaStreamClient(HttpClient streamClient, AvanzaOptions options, TimeProvider time, ILogger logger, Random jitter, Recorder? recorder)
{
    public const string EventStreamMediaType = "text/event-stream";
    public const string StreamVersion = "event-stream/2026-09-25";

    /// <summary>A longer <c>Retry-After</c> is not waited in full; the stream retries after this.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(10);

    /// <summary>Refused connections recorded per stream (a refusal every few seconds all day would fill the folder).</summary>
    public const int RecordedRefusals = 3;

    private int _refusalsRecorded;

    public async IAsyncEnumerable<StreamItem> RunAsync(AvanzaRoute route, string path, string refererPath, [EnumeratorCancellation] CancellationToken ct)
    {
        Channel<StreamItem> channel = Channel.CreateBounded<StreamItem>(new BoundedChannelOptions(options.StreamBufferCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait, // back-pressure to the socket; fan-out downstream drops, not this
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task pump = PumpAsync(route, path, new Uri(options.BaseAddress, refererPath), channel.Writer, stop.Token);
        try
        {
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out StreamItem? item))
                {
                    yield return item;
                }
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false); // never throws: failures complete the channel instead
        }
    }

    /// <summary>Reconnect delay; <paramref name="jitterUnit"/> in [0, 1) spreads it ±20 %.</summary>
    internal static TimeSpan Backoff(TimeSpan baseDelay, int failures, TimeSpan cap, double jitterUnit)
    {
        double ms = Math.Min(baseDelay.TotalMilliseconds * Math.Pow(2, Math.Clamp(failures, 0, 5)), cap.TotalMilliseconds);
        ms *= 0.8 + (0.4 * jitterUnit);
        return TimeSpan.FromMilliseconds(Math.Min(ms, cap.TotalMilliseconds));
    }

    private async Task PumpAsync(AvanzaRoute route, string path, Uri referer, ChannelWriter<StreamItem> writer, CancellationToken ct)
    {
        var parser = new SseParser(options.StreamMaxEventChars);
        Exception? terminal = null;
        try
        {
            await writer.WriteAsync(new StreamStateItem(StreamState.Connecting, null, null, time.GetUtcNow()), ct).ConfigureAwait(false);
            int failures = 0;
            while (true)
            {
                (bool delivered, string reason, TimeSpan? retryAfter) = await ConnectOnceAsync(route, path, referer, parser, writer, ct).ConfigureAwait(false);
                failures = delivered ? 0 : failures + 1;
                TimeSpan serverRetry = parser.RetryMilliseconds is { } ms ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
                TimeSpan baseDelay = serverRetry > options.StreamMinRetry ? serverRetry : options.StreamMinRetry;
                TimeSpan delay = Backoff(baseDelay, failures, options.StreamMaxBackoff, jitter.NextDouble());
                if (retryAfter is { } wait && wait > delay)
                {
                    delay = wait < MaxRetryAfter ? wait : MaxRetryAfter; // the server asked for longer: never sooner
                }

                Log.StreamReconnecting(logger, route.Name, reason, (long)delay.TotalMilliseconds, failures);
                await writer.WriteAsync(new StreamStateItem(StreamState.Reconnecting, reason, delay, time.GetUtcNow()), ct).ConfigureAwait(false);
                await Task.Delay(delay, time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The consumer stopped reading.
        }
        catch (Exception ex)
        {
            terminal = ex;
            Log.StreamStopped(logger, route.Name, ex.Message);
        }
        finally
        {
            writer.TryComplete(terminal);
        }
    }

    private async Task<(bool Delivered, string Reason, TimeSpan? RetryAfter)> ConnectOnceAsync(
        AvanzaRoute route, string path, Uri referer, SseParser parser, ChannelWriter<StreamItem> writer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Options.Set(AvanzaRequest.Route, route);
        request.Headers.Accept.ParseAdd(EventStreamMediaType);
        request.Headers.TryAddWithoutValidation("aza-do-not-touch-session", "true");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.Referrer = referer;
        bool resumed = parser.LastEventId.Length > 0;
        if (resumed)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", parser.LastEventId);
        }

        HttpResponseMessage response;
        using (var headerTimeout = new CancellationTokenSource(options.AttemptTimeout, time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, headerTimeout.Token))
        {
            try
            {
                response = await streamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (false, $"no response within {options.AttemptTimeout.TotalSeconds:0} s", null);
            }
            catch (HttpRequestException)
            {
                return (false, "transport error", null);
            }
        }

        using (response)
        {
            int code = (int)response.StatusCode;
            if (code is 408 or 429 or >= 500)
            {
                TimeSpan? retryAfter = RetryAfter(response);
                if (recorder is not null && _refusalsRecorded < RecordedRefusals)
                {
                    _refusalsRecorded++;
                    try
                    {
                        await recorder.RecordAsync(request, response, ct).ConfigureAwait(false); // status, headers and the short body
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
                    {
                        // A diagnostic only: the stream goes on without it.
                    }
                }

                string server = response.Headers.Server.Count > 0 ? $" from {response.Headers.Server}" : string.Empty;
                string after = retryAfter is { } ra ? string.Create(CultureInfo.InvariantCulture, $", Retry-After {ra.TotalSeconds:0} s") : string.Empty;
                return (false, $"HTTP {code}{server}{after}", retryAfter);
            }

            if (code is 401 or 403)
            {
                throw new SessionExpiredException(route.Name, code);
            }

            if (code == 404)
            {
                throw new EndpointGoneException(route.Name);
            }

            if (code != 200)
            {
                throw new SchemaDriftException(route.Name, StreamVersion, DtoTier.A, ["$http.status"], $"HTTP {code} on the event stream");
            }

            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, EventStreamMediaType, StringComparison.OrdinalIgnoreCase))
            {
                throw new SchemaDriftException(route.Name, StreamVersion, DtoTier.A, ["$http.contentType"], "the response is not an event stream");
            }

            StreamRecording? recording = recorder?.BeginStream(route, request, response);
            string endReason = "cancelled";
            try
            {
                Log.StreamConnected(logger, route.Name, resumed);
                await writer.WriteAsync(new StreamStateItem(StreamState.Connected, null, null, time.GetUtcNow()), ct).ConfigureAwait(false);
                (bool delivered, string reason) = await ReadEventsAsync(response, parser, writer, recording, ct).ConfigureAwait(false);
                endReason = reason;
                return (delivered, reason, null);
            }
            finally
            {
                parser.ResetConnection();
                if (recording is not null)
                {
                    await recording.CompleteAsync(endReason).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>The server's <c>Retry-After</c> as a wait from now (seconds or a date), or null.</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } when date > time.GetUtcNow() => date - time.GetUtcNow(),
            _ => null,
        };

    private async Task<(bool Delivered, string Reason)> ReadEventsAsync(
        HttpResponseMessage response, SseParser parser, ChannelWriter<StreamItem> writer, StreamRecording? recording, CancellationToken ct)
    {
        Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            Decoder decoder = Encoding.UTF8.GetDecoder();
            byte[] bytes = ArrayPool<byte>.Shared.Rent(16 * 1024);
            char[] chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(bytes.Length));
            var events = new List<SseEvent>();
            bool delivered = false;
            try
            {
                while (true)
                {
                    int read;
                    using (var idle = new CancellationTokenSource(options.StreamIdleTimeout, time))
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token))
                    {
                        try
                        {
                            read = await body.ReadAsync(bytes, linked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            return (delivered, $"no data for {options.StreamIdleTimeout.TotalSeconds:0} s");
                        }
                        catch (Exception ex) when (ex is IOException or HttpRequestException)
                        {
                            return (delivered, "connection lost");
                        }
                    }

                    if (read == 0)
                    {
                        return (delivered, "server closed the stream");
                    }

                    int count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                    try
                    {
                        parser.Feed(chars.AsSpan(0, count), events);
                    }
                    catch (SseProtocolException ex)
                    {
                        return (delivered, ex.Message);
                    }

                    DateTimeOffset now = time.GetUtcNow();
                    foreach (SseEvent e in events)
                    {
                        recording?.Add(e);
                        await writer.WriteAsync(new StreamMessage(e, now), ct).ConfigureAwait(false);
                        delivered = true;
                    }

                    events.Clear();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
                ArrayPool<char>.Shared.Return(chars);
            }
        }
    }
}
