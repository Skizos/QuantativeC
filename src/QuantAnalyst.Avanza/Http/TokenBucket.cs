namespace QuantAnalyst.Avanza.Http;

/// <summary>
/// Token bucket with reservations: every call takes a token immediately (the balance may go negative) and is told
/// how long to wait before using it, so waiting callers are served in arrival order at exactly the configured rate.
/// </summary>
internal sealed class TokenBucket
{
    private readonly Lock _gate = new();
    private readonly double _ratePerSecond;
    private readonly int _burst;
    private readonly TimeProvider _time;
    private double _tokens;
    private long _lastTimestamp;

    public TokenBucket(double ratePerSecond, int burst, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ratePerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(burst);
        _ratePerSecond = ratePerSecond;
        _burst = burst;
        _time = time;
        _tokens = burst;
        _lastTimestamp = time.GetTimestamp();
    }

    /// <summary>Takes one token; returns the delay before the caller may send.</summary>
    public TimeSpan Reserve()
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            double elapsed = _time.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
            _lastTimestamp = now;
            _tokens = Math.Min(_burst, _tokens + (elapsed * _ratePerSecond));
            _tokens -= 1.0;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _ratePerSecond);
        }
    }
}
