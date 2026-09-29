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

/// <summary>A courtage class's courtage for shares in a foreign currency, in that currency (ADR 0005): max(min, rate × value).</summary>
public sealed record ForeignCourtage(decimal Min, decimal Rate);

/// <summary>
/// A cost model (config/costs.&lt;name&gt;.json): courtage, FX fee, spread, slippage and participation cap, and the courtage
/// for US and Canadian shares (<c>foreign_courtage</c>, ADR 0005). Money amounts stay decimal here and become double only
/// for the engine.
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
    /// <summary>Gets Avanza's name for the courtage class (e.g. "Start"); defaults to <see cref="Name"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the trade sizes Avanza says the class suits (SEK; either end may be open), for display only.</summary>
    public (decimal? From, decimal? To) SuitsTrades { get; init; }

    /// <summary>Gets the capital limit below which the class can be chosen (Avanza Start: under 50,000 SEK), or null.</summary>
    public decimal? EligibleBelowCapital { get; init; }

    /// <summary>Gets a value indicating whether the owner checked the courtage and FX fee against Avanza's price list.</summary>
    public bool Verified => VerifiedOn is not null;

    /// <summary>Gets the courtage for shares in each foreign currency (USD, CAD), in that currency; empty when the file has none.</summary>
    public IReadOnlyDictionary<string, ForeignCourtage> Foreign { get; init; } = new Dictionary<string, ForeignCourtage>(StringComparer.Ordinal);

    /// <summary>Gets where the foreign courtage comes from.</summary>
    public string? ForeignSourceUrl { get; init; }

    /// <summary>Gets when the owner checked the foreign courtage against Avanza's price list; null until then.</summary>
    public DateOnly? ForeignVerifiedOn { get; init; }

    /// <summary>Courtage for one order of <paramref name="notional"/> SEK: max(min, rate × notional).</summary>
    public decimal Courtage(decimal notional) => Math.Max(CourtageMin, CourtageRate * notional);

    /// <summary>
    /// Courtage for one order of <paramref name="notional"/> in <paramref name="currency"/>, in that currency: the Swedish
    /// courtage for SEK, the class's foreign courtage for USD and CAD (ADR 0005). Throws when the file has none for it.
    /// </summary>
    public decimal CourtageIn(string currency, decimal notional) =>
        string.Equals(currency, Currency, StringComparison.Ordinal)
            ? Courtage(notional)
            : ForeignFor(currency) is { } f
                ? Math.Max(f.Min, f.Rate * notional)
                : throw new BacktestConfigException($"The courtage class {Name} has no courtage for {currency} shares; add foreign_courtage.{currency} to costs.{Name}.json (ADR 0005).");

    /// <summary>The foreign courtage for <paramref name="currency"/>, or null.</summary>
    public ForeignCourtage? ForeignFor(string currency) => Foreign.GetValueOrDefault(currency);

    /// <summary>Every cost model in a folder (costs.*.json), cheapest minimum first.</summary>
    public static IReadOnlyList<CostModel> LoadAll(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new BacktestConfigException($"Config folder {directory} not found.");
        }

        CostModel[] all = [.. Directory.EnumerateFiles(directory, "costs.*.json").Select(Load)];
        return all.Length == 0
            ? throw new BacktestConfigException($"No costs.<class>.json files in {directory}.")
            : [.. all.OrderBy(c => c.CourtageMin).ThenBy(c => c.CourtageRate).ThenBy(c => c.Name, StringComparer.Ordinal)];
    }

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
            if (verified.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)
                || (verified.ValueKind == JsonValueKind.String
                    && !DateOnly.TryParseExact(verified.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            {
                throw new BacktestConfigException(
                    $"{path}: verified_on must be null (not checked yet) or the date you checked the costs, in quotes, e.g. \"2026-09-26\"; it is {verified.GetRawText()}.");
            }

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
                    : DateOnly.ParseExact(verified.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            {
                DisplayName = OptionalString(root, "display_name"),
                SuitsTrades = root.TryGetProperty("suits_trades_sek", out JsonElement suits)
                    ? (OptionalDecimal(suits, "from"), OptionalDecimal(suits, "to"))
                    : (null, null),
                EligibleBelowCapital = OptionalDecimal(root, "eligible_below_capital_sek"),
            };
            if (root.TryGetProperty("foreign_courtage", out JsonElement foreign))
            {
                model = model with
                {
                    Foreign = ReadForeign(foreign, path),
                    ForeignSourceUrl = OptionalString(foreign, "source_url"),
                    ForeignVerifiedOn = foreign.GetProperty("verified_on") is { ValueKind: JsonValueKind.String } fv
                        ? DateOnly.ParseExact(fv.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : null,
                };
            }

            if (model.CourtageMin < 0 || model.CourtageRate < 0 || model.FxFeeRate < 0 || model.SlippageBps < 0 || model.HalfSpreadBps < 0
                || model.ParticipationCap <= 0 || model.ParticipationCap > 1)
            {
                throw new BacktestConfigException($"{path}: costs must be >= 0 and participation_cap in (0, 1].");
            }

            if (model.SuitsTrades is ({ } from, { } to) && from > to || model.EligibleBelowCapital <= 0)
            {
                throw new BacktestConfigException($"{path}: suits_trades_sek needs from <= to, and eligible_below_capital_sek must be > 0.");
            }

            string file = Path.GetFileName(path);
            if (file.StartsWith("costs.", StringComparison.Ordinal) && file != $"costs.{model.Name}.json")
            {
                throw new BacktestConfigException($"{path}: the content says name \"{model.Name}\"; the file must be named costs.{model.Name}.json.");
            }

            return model;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new BacktestConfigException($"{path} is not a valid cost model: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// <c>foreign_courtage</c>: <c>{"USD": {"min", "rate"}, "CAD": {…}, "source_url", "verified_on", "basis"}</c>. Only the
    /// currencies of the markets the program trades may appear (ADR 0005); every value must be ≥ 0 and the rate below 10 %.
    /// </summary>
    private static Dictionary<string, ForeignCourtage> ReadForeign(JsonElement foreign, string path)
    {
        var result = new Dictionary<string, ForeignCourtage>(StringComparer.Ordinal);
        foreach (JsonProperty p in foreign.EnumerateObject())
        {
            if (p.Name is "source_url" or "verified_on" or "basis")
            {
                continue;
            }

            if (Core.Market.Markets.ForCurrency(p.Name) is null || !Core.Market.Markets.IsForeign(p.Name))
            {
                throw new BacktestConfigException($"{path}: foreign_courtage.{p.Name} is not a foreign currency the program trades ({string.Join(", ", Core.Market.Markets.All.Where(m => Core.Market.Markets.IsForeign(m.Currency)).Select(m => m.Currency))}).");
            }

            var c = new ForeignCourtage(p.Value.GetProperty("min").GetDecimal(), p.Value.GetProperty("rate").GetDecimal());
            if (c.Min < 0 || c.Rate < 0 || c.Rate >= 0.1m)
            {
                throw new BacktestConfigException($"{path}: foreign_courtage.{p.Name} needs min >= 0 and rate in [0, 0.1).");
            }

            result[p.Name] = c;
        }

        if (foreign.TryGetProperty("verified_on", out JsonElement v) && v.ValueKind == JsonValueKind.String
            && !DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new BacktestConfigException($"{path}: foreign_courtage.verified_on must be null or a yyyy-MM-dd date.");
        }

        return result;
    }

    private static string? OptionalString(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? OptionalDecimal(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null ? v.GetDecimal() : null;
}

/// <summary>
/// What <c>qa backtest</c> uses when --costs or --cash is not given (config/backtest-defaults.json). Without the file:
/// avanza-small and 1,000,000 SEK.
/// </summary>
public sealed record BacktestDefaults(string Costs, decimal Cash)
{
    public const string FileName = "backtest-defaults.json";

    public static readonly BacktestDefaults BuiltIn = new("avanza-small", 1_000_000m);

    public static BacktestDefaults Load(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return BuiltIn;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.GetProperty("format").GetString() != "qa-backtest-defaults/1")
            {
                throw new BacktestConfigException($"{path}: format must be qa-backtest-defaults/1.");
            }

            var defaults = new BacktestDefaults(root.GetProperty("costs").GetString()!, root.GetProperty("cash").GetDecimal());
            return defaults.Cash > 0 && defaults.Costs.Length > 0
                ? defaults
                : throw new BacktestConfigException($"{path}: costs must name a cost model and cash must be > 0.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new BacktestConfigException($"{path} is not valid: {ex.Message}", ex);
        }
    }
}

public sealed class BacktestConfigException(string message, Exception? inner = null) : Exception(message, inner);
