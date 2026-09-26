namespace QuantAnalyst.Core.Broker;

/// <summary>DTO strictness tier (ADR 0002 §2).</summary>
public enum DtoTier
{
    /// <summary>Trading-critical: unknown or missing fields halt trading.</summary>
    A,

    /// <summary>Informational: missing required fields disable the feature; unknown fields are logged.</summary>
    B,
}

/// <summary>Base class for broker failures. Messages never contain secrets or full account ids.</summary>
public abstract class BrokerException : Exception
{
    protected BrokerException(string route, string message, Exception? inner = null)
        : base(message, inner)
    {
        Route = route;
    }

    /// <summary>Logical route name (e.g. "positions"), not a URL.</summary>
    public string Route { get; }
}

/// <summary>
/// A broker payload no longer matches its versioned DTO (ADR 0002 §2). Tier A drift halts trading.
/// <see cref="Paths"/> lists every offending JSON path, e.g. <c>$.accounts[0].newField</c>.
/// </summary>
public sealed class SchemaDriftException : BrokerException
{
    public SchemaDriftException(string route, string dtoVersion, DtoTier tier, IReadOnlyList<string> paths, string detail, Exception? inner = null)
        : base(route, BuildMessage(route, dtoVersion, tier, paths, detail), inner)
    {
        DtoVersion = dtoVersion;
        Tier = tier;
        Paths = paths;
        Detail = detail;
    }

    public string DtoVersion { get; }

    public DtoTier Tier { get; }

    public IReadOnlyList<string> Paths { get; }

    public string Detail { get; }

    /// <summary>Tier A drift halts trading; Tier B only disables the feature.</summary>
    public bool HaltsTrading => Tier == DtoTier.A;

    private static string BuildMessage(string route, string dtoVersion, DtoTier tier, IReadOnlyList<string> paths, string detail)
    {
        string where = paths.Count == 0 ? string.Empty : $" at {string.Join(", ", paths.Take(20))}{(paths.Count > 20 ? $" (+{paths.Count - 20} more)" : string.Empty)}";
        return $"Schema drift on '{route}' (DTO {dtoVersion}, tier {tier}){where}: {detail}";
    }
}

/// <summary>401/403 from the broker: the session is gone. Order flow halts; no automatic re-login in a loop.</summary>
public sealed class SessionExpiredException(string route, int statusCode)
    : BrokerException(route, $"Session expired or not authorized on '{route}' (HTTP {statusCode}).")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>A trading-critical route returned 404: the endpoint has moved (ADR 0002 §2). Halt.</summary>
public sealed class EndpointGoneException(string route)
    : BrokerException(route, $"Endpoint for '{route}' returned 404; it may have moved. Refresh docs/research/avanza-endpoints.md.");

/// <summary>Transport failure, timeout, 5xx after retries, other unexpected status, or an open circuit breaker.</summary>
public sealed class BrokerUnavailableException(string route, string message, int? statusCode = null, Exception? inner = null)
    : BrokerException(route, message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>The single login attempt for this trigger failed. Do not retry automatically.</summary>
public sealed class LoginFailedException(string reason)
    : BrokerException("login", $"Login failed: {reason}. Not retrying (one attempt per trigger).")
{
    public string Reason { get; } = reason;
}

/// <summary>Login is locked locally after repeated failures (or a lockout signal). A human must clear it.</summary>
public sealed class LoginLockedException(string detail)
    : BrokerException("login", $"Login is locked: {detail}. Check your login with BankID on avanza.se, then run 'qa login --clear-lock'.");

/// <summary>The route is reachable but its payload is not modelled yet (waiting for a recorded fixture).</summary>
public sealed class EndpointNotModelledException(string route, string detail)
    : BrokerException(route, $"'{route}' is not modelled yet: {detail}");
