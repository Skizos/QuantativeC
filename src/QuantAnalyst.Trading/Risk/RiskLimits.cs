using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Core;

namespace QuantAnalyst.Trading.Risk;

public sealed class TradingConfigException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Pre-trade limits (ADR 0003 §4), from <c>config/risk-limits.json</c>. Every field is required (no implicit defaults,
/// which ADR 0003 demands for Auto), and each is range-checked at load.
/// </summary>
public sealed record RiskLimits(
    decimal MaxOrderValueSek,
    decimal MaxOrderValuePctOfAccount,
    decimal MaxPositionPctOfAccount,
    decimal MaxGrossExposurePct,
    int MaxOrdersPerDay,
    int MaxActionsPerMinute,
    TimeSpan MinIntervalSameInstrument,
    TimeSpan DuplicateIntentWindow,
    decimal PriceCollarPct,
    TimeSpan MaxQuoteAge,
    TimeOnly WindowOpen,
    TimeOnly WindowClose,
    TimeOnly HalfDayWindowClose,
    decimal DailyLossStopPct)
{
    public const string FileName = "risk-limits.json";

    /// <summary>ADR 0003 §4 as written (used by tests; the committed file carries the same values).</summary>
    public static RiskLimits AdrDefaults { get; } = new(
        25_000m, 0.10m, 0.20m, 1.00m, 20, 5, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), 0.02m, TimeSpan.FromSeconds(10),
        new TimeOnly(9, 5), new TimeOnly(17, 20), new TimeOnly(12, 50), 0.02m);

    public static RiskLimits Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new TradingConfigException($"Risk limits {path} not found. Nothing trades without them.");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement r = doc.RootElement;
            if (r.GetProperty("format").GetString() != "qa-risk-limits/1")
            {
                throw new TradingConfigException($"{path}: format must be qa-risk-limits/1.");
            }

            JsonElement w = r.GetProperty("trading_window");
            var limits = new RiskLimits(
                r.GetProperty("max_order_value_sek").GetDecimal(),
                r.GetProperty("max_order_value_pct_of_account").GetDecimal(),
                r.GetProperty("max_position_pct_of_account").GetDecimal(),
                r.GetProperty("max_gross_exposure_pct").GetDecimal(),
                r.GetProperty("max_orders_per_day").GetInt32(),
                r.GetProperty("max_actions_per_minute").GetInt32(),
                TimeSpan.FromSeconds(r.GetProperty("min_seconds_between_actions_same_instrument").GetDouble()),
                TimeSpan.FromSeconds(r.GetProperty("duplicate_intent_window_seconds").GetDouble()),
                r.GetProperty("price_collar_pct").GetDecimal(),
                TimeSpan.FromSeconds(r.GetProperty("max_quote_age_seconds").GetDouble()),
                Time(w, "open"),
                Time(w, "close"),
                Time(w, "half_day_close"),
                r.GetProperty("daily_loss_stop_pct").GetDecimal());
            limits.Validate(path);
            return limits;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new TradingConfigException($"{path} is not valid risk limits: {ex.Message}", ex);
        }

        static TimeOnly Time(JsonElement e, string name) =>
            TimeOnly.ParseExact(e.GetProperty(name).GetString()!, "HH:mm", CultureInfo.InvariantCulture);
    }

    internal void Validate(string source)
    {
        static bool Fraction(decimal x) => x > 0 && x <= 1;
        string? problem =
            MaxOrderValueSek <= 0 ? "max_order_value_sek must be > 0"
            : !Fraction(MaxOrderValuePctOfAccount) ? "max_order_value_pct_of_account must be in (0, 1]"
            : !Fraction(MaxPositionPctOfAccount) ? "max_position_pct_of_account must be in (0, 1]"
            : !Fraction(MaxGrossExposurePct) ? "max_gross_exposure_pct must be in (0, 1] (no leverage on ISK)"
            : MaxOrdersPerDay < 1 ? "max_orders_per_day must be >= 1"
            : MaxActionsPerMinute < 1 ? "max_actions_per_minute must be >= 1"
            : MinIntervalSameInstrument < TimeSpan.Zero ? "min_seconds_between_actions_same_instrument must be >= 0"
            : DuplicateIntentWindow < TimeSpan.Zero ? "duplicate_intent_window_seconds must be >= 0"
            : PriceCollarPct is <= 0 or > 0.2m ? "price_collar_pct must be in (0, 0.2]"
            : MaxQuoteAge <= TimeSpan.Zero ? "max_quote_age_seconds must be > 0"
            : WindowOpen >= WindowClose || WindowOpen >= HalfDayWindowClose ? "trading_window: open must be before close and half_day_close"
            : DailyLossStopPct is <= 0 or > 0.5m ? "daily_loss_stop_pct must be in (0, 0.5]"
            : null;
        if (problem is not null)
        {
            throw new TradingConfigException($"{source}: {problem}.");
        }
    }
}

/// <summary>One allowed instrument (R2).</summary>
public sealed record UniverseEntry(OrderbookId OrderbookId, string Ticker, string Name);

/// <summary>
/// The instrument allowlist (R2), <c>config/universe.json</c>, keyed by orderbook id. An empty universe rejects every
/// order, which is the safe default until the owner adds names.
/// </summary>
public sealed class Universe
{
    public const string FileName = "universe.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly Dictionary<OrderbookId, UniverseEntry> _byId;

    public Universe(IEnumerable<UniverseEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _byId = [];
        foreach (UniverseEntry e in entries)
        {
            if (!_byId.TryAdd(e.OrderbookId, e))
            {
                throw new TradingConfigException($"Orderbook {e.OrderbookId} ({e.Ticker}) is listed twice in the universe.");
            }
        }
    }

    public static Universe Empty { get; } = new([]);

    public IReadOnlyList<UniverseEntry> Entries => [.. _byId.Values.OrderBy(e => e.Ticker, StringComparer.Ordinal)];

    public bool Contains(OrderbookId id) => _byId.ContainsKey(id);

    public UniverseEntry? Find(OrderbookId id) => _byId.GetValueOrDefault(id);

    /// <summary>Reads the file; a missing file is an empty universe.</summary>
    public static Universe Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.GetProperty("format").GetString() != "qa-universe/1")
            {
                throw new TradingConfigException($"{path}: format must be qa-universe/1.");
            }

            return new Universe([.. root.GetProperty("instruments").EnumerateArray().Select(i => new UniverseEntry(
                new OrderbookId(i.GetProperty("orderbook_id").GetString()!),
                i.GetProperty("ticker").GetString()!,
                i.GetProperty("name").GetString()!))]);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new TradingConfigException($"{path} is not a valid universe: {ex.Message}", ex);
        }
    }

    public Universe With(UniverseEntry entry) => new(_byId.Values.Where(e => e.OrderbookId != entry.OrderbookId).Append(entry));

    public Universe Without(OrderbookId id) => new(_byId.Values.Where(e => e.OrderbookId != id));

    public void Save(string path)
    {
        var doc = new
        {
            format = "qa-universe/1",
            note = "Instrument allowlist (ADR 0003 R2), by Avanza orderbook id. Edit with 'qa universe add|remove'. An empty list rejects every order.",
            instruments = Entries.Select(e => new { orderbook_id = e.OrderbookId.Value, ticker = e.Ticker, name = e.Name }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, Indented) + "\n");
    }
}
