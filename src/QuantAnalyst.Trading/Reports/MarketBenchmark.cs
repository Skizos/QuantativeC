using System.Text.Json;
using System.Text.Json.Nodes;
using QuantAnalyst.Core;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Reports;

/// <summary>
/// Plan 24 B: the market index the weekly summary compares with (e.g. OMX Stockholm 30), <c>config/benchmark.json</c>.
/// Identified by Avanza's orderbook id, which the owner copies from the index's page address on avanza.se: the program
/// does not search for indices (one route fewer to trust).
/// </summary>
public sealed record BenchmarkSettings(OrderbookId OrderbookId, string Name)
{
    public const string FileName = "benchmark.json";
    public const string Format = "qa-benchmark/1";

    /// <summary>The setting, or null when no benchmark is set.</summary>
    public static BenchmarkSettings? Load(string configDirectory)
    {
        ArgumentNullException.ThrowIfNull(configDirectory);
        string path = Path.Combine(configDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement r = doc.RootElement;
            if (r.GetProperty("format").GetString() != Format)
            {
                throw new TradingConfigException($"{path}: format must be {Format}.");
            }

            string id = r.GetProperty("orderbook_id").GetString() ?? string.Empty;
            string name = r.GetProperty("name").GetString() ?? string.Empty;
            return id.Length == 0 || !id.All(char.IsAsciiDigit) || name.Trim().Length == 0
                ? throw new TradingConfigException($"{path}: orderbook_id must be Avanza's number for the index and name must not be empty.")
                : new BenchmarkSettings(new OrderbookId(id), name.Trim());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new TradingConfigException($"{path} is not a valid benchmark config: {ex.Message}", ex);
        }
    }

    public void Save(string configDirectory)
    {
        ArgumentNullException.ThrowIfNull(configDirectory);
        var json = new JsonObject { ["format"] = Format, ["orderbook_id"] = OrderbookId.Value, ["name"] = Name };
        File.WriteAllText(Path.Combine(configDirectory, FileName), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}

/// <summary>The index over Paper's days: compounded over the days whose two closes are both known.</summary>
/// <param name="Days">The days counted.</param>
/// <param name="Missing">Paper days without the index's close on both ends (imported later, or a day Avanza had no bar).</param>
public sealed record BenchmarkReturn(int Days, decimal Return, int Missing);

/// <summary>Plan 24 B: the market index over the same intervals as Paper's days (the previous Paper close to this one).</summary>
public static class MarketBenchmark
{
    /// <summary>Null without any day that has both closes.</summary>
    public static BenchmarkReturn? Over(IEnumerable<ListDay> days, IReadOnlyDictionary<DateOnly, decimal> closes)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(closes);
        decimal growth = 1m;
        int counted = 0, missing = 0;
        foreach (ListDay d in days)
        {
            if (closes.TryGetValue(d.From, out decimal start) && start > 0 && closes.TryGetValue(d.Date, out decimal end))
            {
                growth *= end / start;
                counted++;
            }
            else
            {
                missing++;
            }
        }

        return counted == 0 ? null : new BenchmarkReturn(counted, decimal.Round(growth - 1, 6), missing);
    }
}
