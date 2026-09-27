using QuantAnalyst.Trading.Audit;

namespace QuantAnalyst.Trading.Halts;

/// <summary>Why trading is halted (ADR 0003 R17). Any active reason blocks new orders.</summary>
public enum HaltReason
{
    /// <summary>The broker session expired or login failed (ADR 0002).</summary>
    Session,

    /// <summary>A trading-critical response did not match its schema (Tier A drift).</summary>
    SchemaDrift,

    /// <summary>An order endpoint answered 404 (moved or gone).</summary>
    EndpointGone,

    /// <summary>The OMS and the broker (or the paper book) disagree.</summary>
    Reconciliation,

    /// <summary>The HTTP circuit breaker is open.</summary>
    Circuit,

    /// <summary>Market data is stale or the depth stream is down.</summary>
    StaleData,

    /// <summary>An illegal OMS state transition: a bug, so stop.</summary>
    OmsInvariant,

    /// <summary>The live account may no longer trade (R1 changed) or its state can't be used (Phase 7).</summary>
    Account,

    /// <summary>The kill switch fired. Only <c>qa kill --reset</c> clears it.</summary>
    KillSwitch,
}

public sealed record HaltState(HaltReason Reason, string Detail, DateTimeOffset SinceUtc);

/// <summary>
/// Holds the active halt reasons. Raising is idempotent per reason; every change is audited. The kill switch reason is
/// sticky: <see cref="Clear"/> refuses it, only the kill switch's reset path may remove it.
/// </summary>
public sealed class HaltController(AuditLog audit, TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<HaltReason, HaltState> _active = [];

    public event Action<HaltState>? Raised;

    public bool IsHalted
    {
        get
        {
            lock (_lock)
            {
                return _active.Count > 0;
            }
        }
    }

    public IReadOnlyList<HaltState> Active
    {
        get
        {
            lock (_lock)
            {
                return [.. _active.Values.OrderBy(h => h.SinceUtc)];
            }
        }
    }

    public bool IsActive(HaltReason reason)
    {
        lock (_lock)
        {
            return _active.ContainsKey(reason);
        }
    }

    /// <summary>Raises a halt. A reason that is already active keeps its first detail and time.</summary>
    public void Raise(HaltReason reason, string detail)
    {
        HaltState state;
        lock (_lock)
        {
            if (_active.ContainsKey(reason))
            {
                return;
            }

            state = new HaltState(reason, detail, time.GetUtcNow());
            _active[reason] = state;
        }

        audit.Append("halt", new { reason = reason.ToString(), detail });
        Raised?.Invoke(state);
    }

    /// <summary>Clears a resolved halt (e.g. data fresh again). The kill switch cannot be cleared here.</summary>
    public void Clear(HaltReason reason, string why)
    {
        if (reason == HaltReason.KillSwitch)
        {
            throw new InvalidOperationException("The kill switch halt is cleared only by 'qa kill --reset'.");
        }

        ClearCore(reason, why);
    }

    /// <summary>The kill switch's reset path (after its own checks).</summary>
    internal void ClearKill(string why) => ClearCore(HaltReason.KillSwitch, why);

    private void ClearCore(HaltReason reason, string why)
    {
        bool removed;
        lock (_lock)
        {
            removed = _active.Remove(reason);
        }

        if (removed)
        {
            audit.Append("halt-cleared", new { reason = reason.ToString(), why });
        }
    }
}
