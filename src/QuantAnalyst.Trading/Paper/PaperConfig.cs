using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Paper;

/// <summary>
/// The paper account (<c>config/paper.json</c>): the courtage class its fills pay, the cash a new book starts with, and
/// when the daily strategies decide (Stockholm time, after the open, on bars through yesterday's close). Optionally the
/// strategy <c>qa paper run</c> trades when none is given (saved by <c>qa paper strategy</c>).
/// </summary>
public sealed record PaperConfig(string Costs, decimal Cash, TimeOnly DecisionTime)
{
    public const string FileName = "paper.json";

    /// <summary>The paper account's id. R1 compares it like a real one; it can never match a real Avanza account.</summary>
    public const string AccountId = "PAPER";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Gets the saved strategy, or null when none is saved.</summary>
    public PaperStrategy? Strategy { get; init; }

    public static PaperConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new TradingConfigException($"{path} not found. Paper needs a courtage class, starting cash and a decision time.");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement r = doc.RootElement;
            if (r.GetProperty("format").GetString() != "qa-paper/1")
            {
                throw new TradingConfigException($"{path}: format must be qa-paper/1.");
            }

            string costs = r.GetProperty("costs").GetString() ?? string.Empty;
            decimal cash = r.GetProperty("cash").GetDecimal();
            string time = r.GetProperty("decision_time").GetString() ?? string.Empty;
            if (costs.Length == 0 || cash <= 0)
            {
                throw new TradingConfigException($"{path}: costs must name a cost file (config/costs.<name>.json) and cash must be > 0.");
            }

            if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly decision))
            {
                throw new TradingConfigException($"{path}: decision_time must be HH:mm Stockholm time, e.g. \"09:10\"; it is \"{time}\".");
            }

            return new PaperConfig(costs, cash, decision) { Strategy = ReadStrategy(path, r) };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new TradingConfigException($"{path} is not a valid paper config: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Writes (or with null removes) the <c>strategy</c> member of an existing paper.json and keeps every other member as
    /// it is. The caller validates the strategy first (the catalog lives in Analytics).
    /// </summary>
    public static void SaveStrategy(string path, PaperStrategy? strategy)
    {
        if (!File.Exists(path))
        {
            throw new TradingConfigException($"{path} not found.");
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidOperationException("the file is empty");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new TradingConfigException($"{path} is not a valid paper config: {ex.Message}", ex);
        }

        if (root["format"]?.GetValueKind() != JsonValueKind.String || root["format"]!.GetValue<string>() != "qa-paper/1")
        {
            throw new TradingConfigException($"{path}: format must be qa-paper/1.");
        }

        root.Remove("strategy");
        if (strategy is not null)
        {
            var parameters = new JsonObject();
            foreach ((string key, string value) in strategy.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                parameters[key] = value;
            }

            root["strategy"] = new JsonObject { ["name"] = strategy.Name, ["params"] = parameters };
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Indented) + Environment.NewLine);
        File.Move(temp, path, overwrite: true);
    }

    private static PaperStrategy? ReadStrategy(string path, JsonElement root)
    {
        if (!root.TryGetProperty("strategy", out JsonElement s) || s.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        string name = s.GetProperty("name").GetString() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new TradingConfigException($"{path}: strategy.name is empty.");
        }

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (s.TryGetProperty("params", out JsonElement p))
        {
            foreach (JsonProperty item in p.EnumerateObject())
            {
                parameters[item.Name] = item.Value.ValueKind switch
                {
                    JsonValueKind.String => item.Value.GetString()!,
                    JsonValueKind.Number => item.Value.GetRawText(),
                    _ => throw new TradingConfigException($"{path}: strategy.params.{item.Name} must be a string or a number."),
                };
            }
        }

        return new PaperStrategy(name, parameters);
    }
}

/// <summary>A strategy by catalog name and parameters, as saved in paper.json.</summary>
public sealed record PaperStrategy(string Name, IReadOnlyDictionary<string, string> Parameters)
{
    /// <summary>The command-line form: <c>ma-cross --param fast=20 --param slow=100</c>.</summary>
    public string CommandLine() =>
        string.Join(' ', [Name, .. Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"--param {p.Key}={p.Value}")]);
}
