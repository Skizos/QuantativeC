using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace QuantAnalyst.Avanza.Tests;

internal static class Fixtures
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "fixtures", "avanza", "provisional");

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(Directory, name));

    public static string Text(string name) => File.ReadAllText(Path.Combine(Directory, name));

    public static JsonNode Node(string name) => JsonNode.Parse(Text(name))!;

    public static byte[] Mutate(string name, Action<JsonNode> mutate)
    {
        JsonNode node = Node(name);
        mutate(node);
        return System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
    }
}

/// <summary>Captures formatted log lines (thread-safe) for assertions and the secret scan.</summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public string All => string.Join('\n', Lines);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lines)
        {
            _lines.Add($"{logLevel}: {formatter(state, exception)}{(exception is null ? string.Empty : " | " + exception)}");
        }
    }
}
