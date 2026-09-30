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

/// <summary>Price-chart answers built for a test (plan 22).</summary>
internal static class ChartAnswers
{
    /// <summary>
    /// Last week's 10-minute bars of a share (Monday 21 to Wednesday 23 September 2026): a continuous one trades at 10:30
    /// and 14:10 as well as in the auctions; an auction one only at 09:00, 11:00, 13:00, 15:00 and 17:30 (Stockholm).
    /// </summary>
    public static HttpResponseMessage TenMinuteWeek(bool continuous)
    {
        var ohlc = new System.Text.Json.Nodes.JsonArray();
        (int H, int M)[] slots = continuous ? [(9, 0), (10, 30), (14, 10), (17, 30)] : [(9, 0), (11, 0), (13, 0), (15, 0), (17, 30)];
        for (int day = 21; day <= 23; day++)
        {
            foreach ((int h, int m) in slots)
            {
                // CEST: Stockholm is UTC+2 in September.
                ohlc.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["timestamp"] = new DateTimeOffset(2026, 9, day, h - 2, m, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    ["open"] = 70.1,
                    ["close"] = 70.2,
                    ["low"] = 70.0,
                    ["high"] = 70.3,
                    ["totalVolumeTraded"] = 500,
                });
            }
        }

        return FakeAvanza.Json(new System.Text.Json.Nodes.JsonObject
        {
            ["ohlc"] = ohlc,
            ["metadata"] = new System.Text.Json.Nodes.JsonObject
            {
                ["resolution"] = new System.Text.Json.Nodes.JsonObject { ["chartResolution"] = "ten_minutes", ["availableResolutions"] = new System.Text.Json.Nodes.JsonArray("ten_minutes", "hour") },
            },
        }.ToJsonString());
    }
}
