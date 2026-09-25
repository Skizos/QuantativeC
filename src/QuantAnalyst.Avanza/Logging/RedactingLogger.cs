using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QuantAnalyst.Core;

namespace QuantAnalyst.Avanza.Logging;

/// <summary>
/// Scrubs text before it is written anywhere (master plan §5, CLAUDE.md "Never log or print …"):
/// <list type="bullet">
/// <item>registered secret values (credentials, TOTP secret, security token, cookie values) → <c>***</c></item>
/// <item>registered account ids → <c>***123</c></item>
/// <item><c>X-SecurityToken</c>, <c>Cookie</c>/<c>Set-Cookie</c> and <c>AZACSRF=</c> patterns → value masked</item>
/// </list>
/// </summary>
public sealed partial class Redactor
{
    // Values shorter than this are not treated as secrets (would mangle ordinary words and numbers).
    private const int MinSecretLength = 4;
    private readonly ConcurrentDictionary<string, string> _replacements = new(StringComparer.Ordinal);

    public void AddSecret(string? value)
    {
        if (!string.IsNullOrEmpty(value) && value.Length >= MinSecretLength)
        {
            _replacements[value] = "***";
        }
    }

    public void AddSecret(Secret? secret) => AddSecret(secret?.Reveal());

    public void AddAccountId(string? accountId)
    {
        if (!string.IsNullOrEmpty(accountId) && accountId.Length > 3)
        {
            _replacements.TryAdd(accountId, AccountId.Mask(accountId));
        }
    }

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (KeyValuePair<string, string> r in _replacements.OrderByDescending(p => p.Key.Length))
        {
            text = text.Replace(r.Key, r.Value, StringComparison.Ordinal);
        }

        text = TokenHeader().Replace(text, "$1***");
        text = CookieHeader().Replace(text, "$1***");
        return CsrfCookie().Replace(text, "$1***");
    }

    [GeneratedRegex(@"(X-SecurityToken[""']?\s*[:=]\s*[""']?)[^\s""',;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenHeader();

    [GeneratedRegex(@"((?:Set-)?Cookie[""']?\s*:\s*[""']?)[^\r\n""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CookieHeader();

    [GeneratedRegex(@"(AZACSRF\s*=\s*)[^;\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CsrfCookie();
}

/// <summary>
/// Minimal <see cref="ILogger"/> that writes one redacted line per event to a <see cref="TextWriter"/>
/// (stderr in the CLI). Exceptions contribute their type and message, not stack traces.
/// </summary>
public sealed class RedactingLogger(TextWriter sink, Redactor redactor, LogLevel minimumLevel, TimeProvider? time = null) : ILogger
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(formatter);
        string message = formatter(state, exception);
        if (exception is not null)
        {
            message += $" | {exception.GetType().Name}: {exception.Message}";
        }

        string line = string.Create(CultureInfo.InvariantCulture, $"{_time.GetUtcNow():HH:mm:ss.fff}Z {Level(logLevel)} {redactor.Redact(message)}");
        lock (_gate)
        {
            sink.WriteLine(line);
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };
}
