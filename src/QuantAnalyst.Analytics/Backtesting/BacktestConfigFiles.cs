using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Backtesting;

/// <summary>
/// The final holdout (config/holdout.json, CLAUDE.md "Final holdout window locked unless I unlock it"). While
/// <see cref="Locked"/>, no backtest may use a bar dated on or after <see cref="Start"/>. Only the owner edits the file.
/// </summary>
public sealed record HoldoutPolicy(bool Locked, DateOnly Start, string Path)
{
    public const string FileName = "holdout.json";

    /// <summary>True when a run over bars dated up to <paramref name="lastBar"/> uses the holdout.</summary>
    public bool Touches(DateOnly lastBar) => lastBar >= Start;

    /// <summary>Reads the policy. A missing or malformed file is an error: without it nothing may run (fail closed).</summary>
    public static HoldoutPolicy Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new BacktestConfigException($"Holdout policy {path} not found. Backtests do not run without it.");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.GetProperty("format").GetString() != "qa-holdout/1")
            {
                throw new BacktestConfigException($"{path}: format must be qa-holdout/1.");
            }

            bool locked = root.GetProperty("locked").GetBoolean();
            DateOnly start = DateOnly.ParseExact(root.GetProperty("start").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!locked && (Text(root, "unlocked_by") is null || Text(root, "unlocked_on") is null || Text(root, "reason") is null))
            {
                throw new BacktestConfigException($"{path}: an unlocked holdout needs unlocked_by, unlocked_on and reason.");
            }

            return new HoldoutPolicy(locked, start, path);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new BacktestConfigException($"{path} is not a valid holdout policy: {ex.Message}", ex);
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s ? s : null;
}

/// <summary>
/// A cost model (config/costs.&lt;name&gt;.json): courtage, FX fee, spread, slippage and participation cap. Money
/// amounts stay decimal here and become double only for the engine.
/// </summary>
public sealed record CostModel(
    string Name,
    string Currency,
    decimal CourtageMin,
    decimal CourtageRate,
    decimal FxFeeRate,
    decimal SlippageBps,
    decimal HalfSpreadBps,
    decimal ParticipationCap,
    string SourceUrl,
    DateOnly? VerifiedOn)
{
    /// <summary>Gets a value indicating whether the owner checked the courtage and FX fee against Avanza's price list.</summary>
    public bool Verified => VerifiedOn is not null;

    /// <summary>One-line label for reports.</summary>
    public string Label => Verified
        ? $"{Name} (verified {VerifiedOn:yyyy-MM-dd})"
        : $"{Name} (UNVERIFIED: check {SourceUrl} and set verified_on)";

    public BacktestConfig ToEngineConfig(decimal initialCash) => new()
    {
        InitialCash = (double)initialCash,
        CourtageMin = (double)CourtageMin,
        CourtageRate = (double)CourtageRate,
        FxFeeRate = (double)FxFeeRate,
        SlippageBps = (double)SlippageBps,
        HalfSpreadBps = (double)HalfSpreadBps,
        ParticipationCap = (double)ParticipationCap,
    };

    public static CostModel Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new BacktestConfigException($"Cost model {path} not found.");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.GetProperty("format").GetString() != "qa-costs/1")
            {
                throw new BacktestConfigException($"{path}: format must be qa-costs/1.");
            }

            JsonElement courtage = root.GetProperty("courtage");
            JsonElement verified = root.GetProperty("verified_on");
            var model = new CostModel(
                root.GetProperty("name").GetString()!,
                root.GetProperty("currency").GetString()!,
                courtage.GetProperty("min").GetDecimal(),
                courtage.GetProperty("rate").GetDecimal(),
                root.GetProperty("fx_fee_rate").GetDecimal(),
                root.GetProperty("slippage_bps").GetDecimal(),
                root.GetProperty("half_spread_bps").GetDecimal(),
                root.GetProperty("participation_cap").GetDecimal(),
                root.GetProperty("source_url").GetString()!,
                verified.ValueKind == JsonValueKind.Null
                    ? null
                    : DateOnly.ParseExact(verified.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (model.CourtageMin < 0 || model.CourtageRate < 0 || model.FxFeeRate < 0 || model.SlippageBps < 0 || model.HalfSpreadBps < 0
                || model.ParticipationCap <= 0 || model.ParticipationCap > 1)
            {
                throw new BacktestConfigException($"{path}: costs must be >= 0 and participation_cap in (0, 1].");
            }

            return model;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new BacktestConfigException($"{path} is not a valid cost model: {ex.Message}", ex);
        }
    }
}

public sealed class BacktestConfigException(string message, Exception? inner = null) : Exception(message, inner);
