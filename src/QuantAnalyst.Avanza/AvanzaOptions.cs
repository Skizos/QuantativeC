using QuantAnalyst.Avanza.Http;

namespace QuantAnalyst.Avanza;

/// <summary>Gateway settings. Defaults follow ADR 0002 (conservative load; see ADR 0004).</summary>
public sealed class AvanzaOptions
{
    public Uri BaseAddress { get; init; } = AvanzaRoutes.DefaultBaseAddress;

    /// <summary>Session inactivity timeout sent at login; Avanza accepts 30–1440.</summary>
    public int MaxInactiveMinutes { get; init; } = 60;

    /// <summary>Global token bucket shared by every call (login included).</summary>
    public double RequestsPerSecond { get; init; } = 2.0;

    public int Burst { get; init; } = 5;

    /// <summary>Per-attempt timeout for reads and for each login step.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Read retries after the first attempt (transport errors, timeouts, 408/429/5xx). Login is never retried.</summary>
    public int MaxReadRetries { get; init; } = 2;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A server <c>Retry-After</c> longer than this is not waited for; the call fails instead.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan CircuitWindow { get; init; } = TimeSpan.FromSeconds(30);

    public int CircuitMinimumCalls { get; init; } = 5;

    public double CircuitFailureRatio { get; init; } = 0.5;

    public TimeSpan CircuitBreakDuration { get; init; } = TimeSpan.FromSeconds(60);

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

        if (AttemptTimeout <= TimeSpan.Zero || CircuitBreakDuration <= TimeSpan.Zero || CircuitWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AttemptTimeout), "Timeouts and windows must be positive.");
        }
    }
}
