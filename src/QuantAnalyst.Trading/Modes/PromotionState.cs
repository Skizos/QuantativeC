using System.Text.Json;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Modes;

/// <summary>A requested mode above what is allowed, or a mode not built yet. Startup fails; nothing is downgraded silently.</summary>
public sealed class ModeNotAllowedException(string message) : Exception(message);

/// <summary>
/// The promotion state (ADR 0003 §3): <c>promotion/state.json</c> (local, git-ignored), else the committed template
/// <c>promotion/state.template.json</c>, else Paper. Only the owner's <c>qa promote</c> raises it (Phase 6 step 7), with
/// an HMAC over each record; Confirm/Auto also verify that HMAC at startup (Phase 7).
/// </summary>
public sealed record PromotionState(TradingMode MaxAllowed, int Records, string Source)
{
    public const string StateFile = "state.json";
    public const string TemplateFile = "state.template.json";

    /// <summary>
    /// The highest mode this build can run, whatever the promotion record says. Confirm (Phase 7) still starts only
    /// through <see cref="ConfirmStartup"/>, whose checks every live session must pass.
    /// </summary>
    public const TradingMode HighestImplemented = TradingMode.Confirm;

    public static PromotionState Load(string promotionDirectory)
    {
        foreach (string file in new[] { StateFile, TemplateFile })
        {
            string path = Path.Combine(promotionDirectory, file);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                string max = doc.RootElement.GetProperty("maxAllowed").GetString()!;
                TradingMode mode = Enum.TryParse(max, ignoreCase: false, out TradingMode m) && Enum.IsDefined(m)
                    ? m
                    : throw new TradingConfigException($"{path}: maxAllowed '{max}' is not Backtest, Paper, Confirm or Auto.");
                int records = doc.RootElement.TryGetProperty("records", out JsonElement r) ? r.GetArrayLength() : 0;
                return new PromotionState(mode, records, path);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new TradingConfigException($"{path} is not a valid promotion state: {ex.Message}", ex);
            }
        }

        return new PromotionState(TradingMode.Paper, 0, "(none: Paper)");
    }

    /// <summary>
    /// The mode to run in: the requested one, if the promotion state allows it and it is implemented. Anything else fails
    /// with a message; it is never lowered silently (ADR 0003 §1).
    /// </summary>
    public TradingMode Effective(TradingMode requested)
    {
        if (requested > MaxAllowed)
        {
            throw new ModeNotAllowedException(
                $"Mode {requested} is above the promotion state ({MaxAllowed}, from {Source}). Promotion is the owner's step (qa promote); nothing was downgraded.");
        }

        if (requested > HighestImplemented)
        {
            throw new ModeNotAllowedException($"Mode {requested} is not available yet: Auto arrives in Phase 8.");
        }

        return requested;
    }
}
