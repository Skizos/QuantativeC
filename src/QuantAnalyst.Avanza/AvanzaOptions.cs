using QuantAnalyst.Avanza.Auth;
using QuantAnalyst.Avanza.Http;

namespace QuantAnalyst.Avanza;

/// <summary>Gateway settings. Defaults follow ADR 0002 (conservative load; see ADR 0004).</summary>
public sealed class AvanzaOptions
{
    public Uri BaseAddress { get; init; } = AvanzaRoutes.DefaultBaseAddress;

    /// <summary>BankID (default, a human approves each login) or TOTP (unattended; set up with <c>qa secrets set</c>).</summary>
    public AvanzaLoginMethod LoginMethod { get; init; } = AvanzaLoginMethod.BankId;

    /// <summary>How long one BankID transaction may wait for approval before the trigger gives up.</summary>
    public TimeSpan BankIdTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Collect/refresh interval while waiting for BankID approval (the reference client uses 1 s).</summary>
    public TimeSpan BankIdPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Session inactivity timeout sent at login; Avanza accepts 30–1440.</summary>
    public int MaxInactiveMinutes { get; init; } = 60;

    /// <summary>Global token bucket shared by every call (login included).</summary>
    public double RequestsPerSecond { get; init; } = 2.0;

    public int Burst { get; init; } = 5;

    /// <summary>Per-attempt timeout for reads and for each login step.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long an order request (place, modify, delete) waits for its one answer; after that it is Unknown, never retried.</summary>
    public TimeSpan OrderTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Read retries after the first attempt (transport errors, timeouts, 408/429/5xx). Login is never retried.</summary>
    public int MaxReadRetries { get; init; } = 2;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A server <c>Retry-After</c> longer than this is not waited for; the call fails instead.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan CircuitWindow { get; init; } = TimeSpan.FromSeconds(30);

    public int CircuitMinimumCalls { get; init; } = 5;

    public double CircuitFailureRatio { get; init; } = 0.5;

    public TimeSpan CircuitBreakDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Stream reconnect base delay floor: the delay is <c>max(server retry, this) · 2^min(n,5)</c> (ADR 0002 §3).</summary>
    public TimeSpan StreamMinRetry { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Upper bound of the stream reconnect delay (after jitter).</summary>
    public TimeSpan StreamMaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A stream that sends no byte for this long is dropped and reconnected (half-open connections).</summary>
    public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Largest accepted event-stream line or event data (ADR 0002 §3: 1 MB).</summary>
    public int StreamMaxEventChars { get; init; } = 1024 * 1024;

    /// <summary>Events buffered between the connection and the consumer before the connection waits.</summary>
    public int StreamBufferCapacity { get; init; } = 256;

    /// <summary>Where <c>state/auth.json</c> (login lock) lives.</summary>
    public string StateDirectory { get; init; } = "state";

    /// <summary>When set, every exchange is recorded under this folder (see <c>RecordingHandler</c>). Git-ignored by default.</summary>
    public string? RecordingDirectory { get; init; }

    public string UserAgent { get; init; } = "QuantAnalyst/0.3 (read-only; +https://github.com/Skizos/QuantativeC)";

    public void Validate()
    {
        if (MaxInactiveMinutes is < 30 or > 1440)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxInactiveMinutes), MaxInactiveMinutes, "Avanza accepts 30–1440 minutes.");
        }

        if (RequestsPerSecond is <= 0 or > 10 || Burst is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestsPerSecond), "Rate limit must be in (0, 10] req/s with burst 1–20.");
        }

        if (MaxReadRetries is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxReadRetries), MaxReadRetries, "0–5 retries.");
        }

        if (BankIdTimeout < TimeSpan.FromSeconds(10) || BankIdTimeout > TimeSpan.FromMinutes(10)
            || BankIdPollInterval < TimeSpan.FromMilliseconds(1) || BankIdPollInterval > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(BankIdTimeout), "BankID timeout must be 10 s–10 min and the poll interval at most 5 s.");
        }

        if (AttemptTimeout <= TimeSpan.Zero || OrderTimeout <= TimeSpan.Zero || CircuitBreakDuration <= TimeSpan.Zero || CircuitWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AttemptTimeout), "Timeouts and windows must be positive.");
        }

        if (StreamMinRetry < TimeSpan.FromSeconds(1) || StreamMaxBackoff < StreamMinRetry || StreamMaxBackoff > TimeSpan.FromMinutes(5)
            || StreamIdleTimeout < TimeSpan.FromSeconds(10) || StreamMaxEventChars is < 1024 or > 16 * 1024 * 1024 || StreamBufferCapacity is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(StreamMinRetry), "Stream settings out of range (retry ≥ 1 s, backoff ≤ 5 min, idle ≥ 10 s, events 1 KB–16 MB).");
        }
    }
}
