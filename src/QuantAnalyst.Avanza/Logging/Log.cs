using Microsoft.Extensions.Logging;

namespace QuantAnalyst.Avanza.Logging;

/// <summary>
/// Every log event of the Avanza gateway (source-generated, CA1848). Arguments are route names, status codes,
/// counts, masked ids and paths: never secrets, tokens, cookies or full account ids. <see cref="RedactingLogger"/>
/// scrubs the formatted text again as a second line of defence.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "drift.warning route={Route} dto={DtoVersion}: {Count} unknown field(s): {Paths}")]
    public static partial void DriftWarning(ILogger logger, string route, string dtoVersion, int count, string paths);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Debug, Message = "http {Method} {Route} -> {Status} in {ElapsedMs} ms (attempt {Attempt})")]
    public static partial void HttpExchange(ILogger logger, string method, string route, int status, long elapsedMs, int attempt);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning, Message = "http {Method} {Route} attempt {Attempt} failed ({Reason}); retrying in {DelayMs} ms")]
    public static partial void HttpRetry(ILogger logger, string method, string route, int attempt, string reason, long delayMs);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Error, Message = "circuit breaker open for {BreakSeconds} s after {Failures}/{Calls} failed calls")]
    public static partial void CircuitOpened(ILogger logger, int breakSeconds, int failures, int calls);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Debug, Message = "rate limiter delayed {Route} by {DelayMs} ms")]
    public static partial void RateLimited(ILogger logger, string route, long delayMs);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "login: starting single attempt (maxInactiveMinutes={MaxInactiveMinutes})")]
    public static partial void LoginStarting(ILogger logger, int maxInactiveMinutes);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information, Message = "login: waiting {WaitMs} ms for a fresh TOTP window (not a retry)")]
    public static partial void TotpWindowWait(ILogger logger, long waitMs);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Information, Message = "login: succeeded with {Method}; security token from {TokenSource}")]
    public static partial void LoginSucceeded(ILogger logger, string method, string tokenSource);

    [LoggerMessage(EventId = 1206, Level = LogLevel.Information, Message = "login: starting one BankID transaction (QR code)")]
    public static partial void BankIdStarting(ILogger logger);

    [LoggerMessage(EventId = 1207, Level = LogLevel.Debug, Message = "login: BankID status {HintCode}")]
    public static partial void BankIdStatus(ILogger logger, string hintCode);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Error, Message = "login: failed at {Step}: {Reason}. Not retrying.")]
    public static partial void LoginFailed(ILogger logger, string step, string reason);

    [LoggerMessage(EventId = 1205, Level = LogLevel.Critical, Message = "login: LOCKED ({Reason}); state persisted. A human must verify with BankID and clear the lock.")]
    public static partial void LoginLocked(ILogger logger, string reason);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Information, Message = "recording {Route} -> {File}")]
    public static partial void Recorded(ILogger logger, string route, string file);
}
