using QuantAnalyst.Core.Broker;
using QuantAnalyst.Trading.Accounts;

namespace QuantAnalyst.Trading.Halts;

/// <summary>
/// Which halt a failed live read means (ADR 0002 §4). The same answer everywhere the live account or broker state is
/// read: the gateway's risk context, the kill switch's loss stop, and a Confirm session's reconciliation.
/// </summary>
public static class BrokerHalts
{
    /// <summary>
    /// The halt for <paramref name="failure"/>, or null for a passing failure (a timeout, the circuit breaker) that only
    /// skips this read. A shape we can't read (drift, or deals not modelled yet) halts like drift: fail closed.
    /// </summary>
    public static HaltReason? For(Exception failure) => failure switch
    {
        AccountStateException => HaltReason.Account,
        SessionExpiredException or LoginFailedException or LoginLockedException => HaltReason.Session,
        SchemaDriftException or EndpointNotModelledException => HaltReason.SchemaDrift,
        EndpointGoneException => HaltReason.EndpointGone,
        _ => null,
    };

    /// <summary>True for the failures a live read can have: broker errors and a changed account.</summary>
    public static bool IsLiveReadFailure(Exception failure) => failure is BrokerException or AccountStateException;
}
