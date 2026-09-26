using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Paper;

/// <summary>
/// The paper account (<c>config/paper.json</c>): the courtage class its fills pay, the cash a new book starts with, and
/// when the daily strategies decide (Stockholm time, after the open, on bars through yesterday's close).
/// </summary>
public sealed record PaperConfig(string Costs, decimal Cash, TimeOnly DecisionTime)
{
    public const string FileName = "paper.json";

    /// <summary>The paper account's id. R1 compares it like a real one; it can never match a real Avanza account.</summary>
    public const string AccountId = "PAPER";

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

            return new PaperConfig(costs, cash, decision);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new TradingConfigException($"{path} is not a valid paper config: {ex.Message}", ex);
        }
    }
}
