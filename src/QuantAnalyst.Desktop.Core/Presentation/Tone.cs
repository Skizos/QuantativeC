namespace QuantAnalyst.Desktop.Core.Presentation;

/// <summary>What a state word means for its colour: green good, blue in progress, amber watch, red bad, grey neither.</summary>
public enum ToneKind
{
    Neutral,
    Positive,
    Info,
    Warning,
    Negative,
}

/// <summary>
/// Maps the words the app shows as chips (status marks, report states, order states, directions) to a tone, so the
/// window colours them consistently (docs/plans/11-app-redesign.md).
/// </summary>
public static class Tone
{
    public static ToneKind Of(string? word) => word?.Trim() switch
    {
        "ok" or "CLEAN" or "Filled" or "up" or "met" or "live" => ToneKind.Positive,
        "todo" or "Working" or "Sent" or "New" or "running" or "info" => ToneKind.Info,
        "warn" or "INCOMPLETE" or "PartiallyFilled" or "Unknown" or "waiting" => ToneKind.Warning,
        "FAIL" or "NOT CLEAN" or "Rejected" or "down" or "halted" => ToneKind.Negative,
        _ => ToneKind.Neutral,
    };

    /// <summary>"up", "down" or "flat" for a change, the words <see cref="Of"/> colours green, red and grey.</summary>
    public static string Direction(decimal change) => change > 0 ? "up" : change < 0 ? "down" : "flat";
}
