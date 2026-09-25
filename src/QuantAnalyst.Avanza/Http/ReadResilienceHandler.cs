using System.Net;
using Microsoft.Extensions.Logging;
using QuantAnalyst.Avanza.Logging;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza.Http;

/// <summary>
/// Sliding-window circuit breaker: with at least <c>minCalls</c> outcomes in the window and a failure ratio at or
/// above the threshold, the circuit opens for <c>breakDuration</c>; then exactly one half-open probe is allowed.
/// </summary>
internal sealed class CircuitBreaker(TimeProvider time, TimeSpan window, int minCalls, double failureRatio, TimeSpan breakDuration, ILogger logger)
{
    private readonly Lock _gate = new();
    private readonly Queue<(DateTimeOffset At, bool Ok)> _outcomes = new();
    private DateTimeOffset? _openUntil;
    private bool _probeInFlight;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _openUntil is { } until && time.GetUtcNow() < until;
            }
        }
    }

    /// <summary>False while open (or while the single half-open probe is running).</summary>
    public bool TryAcquire(out bool isProbe)
    {
        lock (_gate)
        {
            isProbe = false;
            if (_openUntil is not { } until)
            {
                return true;
            }

            if (time.GetUtcNow() < until || _probeInFlight)
            {
                return false;
            }

            _probeInFlight = true;
            isProbe = true;
            return true;
        }
    }

    /// <summary>Releases a half-open probe that ended without an outcome (e.g. cancelled by the caller).</summary>
    public void Abandon(bool wasProbe)
    {
        if (wasProbe)
        {
            lock (_gate)
            {
                _probeInFlight = false;
            }
        }
    }

    public void Record(bool ok, bool wasProbe)
    {
        lock (_gate)
        {
            DateTimeOffset now = time.GetUtcNow();
            if (wasProbe)
            {
                _probeInFlight = false;
                _outcomes.Clear();
                _openUntil = ok ? null : now + breakDuration;
                return;
            }

            _outcomes.Enqueue((now, ok));
            while (_outcomes.Count > 0 && now - _outcomes.Peek().At > window)
            {
                _outcomes.Dequeue();
            }

            int failures = _outcomes.Count(o => !o.Ok);
            if (_outcomes.Count >= minCalls && failures >= failureRatio * _outcomes.Count)
            {
                _openUntil = now + breakDuration;
                Log.CircuitOpened(logger, (int)breakDuration.TotalSeconds, failures, _outcomes.Count);
                _outcomes.Clear();
            }
        }
    }
}

/// <summary>
/// Retries <b>reads only</b> (this handler is never in the login or order pipelines): transport errors, attempt
/// timeouts and 408/429/5xx, at most <c>MaxReadRetries</c> times, with exponential backoff and full jitter or the
/// server's <c>Retry-After</c> (capped). Feeds the circuit breaker with the final outcome of each call.
/// </summary>
internal sealed class ReadResilienceHandler(AvanzaOptions options, CircuitBreaker breaker, TimeProvider time, ILogger logger, Random jitter)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string route = AvanzaRequest.RouteName(request);
        if (!breaker.TryAcquire(out bool isProbe))
        {
            throw new BrokerUnavailableException(route, "Circuit breaker is open after repeated failures; not calling Avanza.");
        }

        bool recorded = false;
        try
        {
            int attempts = options.MaxReadRetries + 1;
            for (int attempt = 1; ; attempt++)
            {
                long started = time.GetTimestamp();
                using var timeoutCts = new CancellationTokenSource(options.AttemptTimeout, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                HttpResponseMessage? response = null;
                string? failure;
                Exception? error = null;
                try
                {
                    response = await base.SendAsync(request, linked.Token).ConfigureAwait(false);
                    failure = IsTransient(response.StatusCode) ? $"HTTP {(int)response.StatusCode}" : null;
                    long elapsedMs = (long)time.GetElapsedTime(started).TotalMilliseconds;
                    Log.HttpExchange(logger, request.Method.Method, route, (int)response.StatusCode, elapsedMs, attempt);

                }
                catch (HttpRequestException ex)
                {
                    failure = "transport error";
                    error = ex;
                }
                catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    failure = $"timeout after {options.AttemptTimeout.TotalSeconds:0} s";
                    error = ex;
                }

                if (failure is null)
                {
                    breaker.Record(true, isProbe);
                    recorded = true;
                    return response!;
                }

                TimeSpan? delay = attempt < attempts ? NextDelay(attempt, response) : null;
                if (delay is null)
                {
                    breaker.Record(false, isProbe);
                    recorded = true;
                    if (response is not null)
                    {
                        return response; // the caller maps the status (BrokerUnavailableException)
                    }

                    throw new BrokerUnavailableException(route, $"Avanza read failed after {attempt} attempt(s): {failure}.", null, error);
                }

                Log.HttpRetry(logger, request.Method.Method, route, attempt, failure, (long)delay.Value.TotalMilliseconds);
                response?.Dispose();
                await Task.Delay(delay.Value, time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!recorded)
            {
                // Caller cancellation or an unexpected exception: no outcome, but never leave the probe slot taken.
                breaker.Abandon(isProbe);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>Retry-After when present (null if above the cap), else full-jitter exponential backoff.</summary>
    private TimeSpan? NextDelay(int attempt, HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter is { } retryAfter)
        {
            TimeSpan wait = retryAfter.Delta
                            ?? (retryAfter.Date is { } date ? date - time.GetUtcNow() : TimeSpan.Zero);
            if (wait < TimeSpan.Zero)
            {
                wait = TimeSpan.Zero;
            }

            return wait <= options.MaxRetryAfter ? wait : null;
        }

        double cap = options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        double ms;
        lock (jitter)
        {
            ms = jitter.NextDouble() * cap;
        }

        return TimeSpan.FromMilliseconds(ms);
    }
}
