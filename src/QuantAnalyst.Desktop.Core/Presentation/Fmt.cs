using System.Globalization;

namespace QuantAnalyst.Desktop.Core.Presentation;

/// <summary>
/// Numbers as the window shows them, Swedish style so they read like your bank: a (no-break) space between thousands,
/// a decimal comma, a real minus sign, "kr" for SEK and signed changes with a plus. Commands and files keep the
/// invariant format; this is only for the screen (docs/plans/11-app-redesign.md).
/// </summary>
public static class Fmt
{
    /// <summary>The minus sign shown for negative numbers (U+2212, the same width as the plus).</summary>
    public const string Minus = "−";

    private static readonly NumberFormatInfo Sv = new()
    {
        NumberGroupSeparator = " ",
        NumberDecimalSeparator = ",",
        NegativeSign = Minus,
        PercentGroupSeparator = " ",
        PercentDecimalSeparator = ",",
    };

    /// <summary>"5 000,00 kr".</summary>
    public static string Sek(decimal value) => Amount(value) + " kr";

    /// <summary>"5 000,00" (two decimals).</summary>
    public static string Amount(decimal value, int decimals = 2) => value.ToString(Pattern(decimals), Sv);

    /// <summary>"+12,50 kr", "−3,00 kr", "0,00 kr".</summary>
    public static string ChangeSek(decimal value) => WithSign(value) + " kr";

    /// <summary>"+12,50", "−3,00", "0,00".</summary>
    public static string WithSign(decimal value, int decimals = 2) =>
        (value > 0 ? "+" : string.Empty) + value.ToString(Pattern(decimals), Sv);

    /// <summary>A fraction as a signed percentage: 0.0123 → "+1,23 %".</summary>
    public static string ChangePct(decimal fraction, int decimals = 2) => WithSign(fraction * 100m, decimals) + " %";

    /// <summary>A fraction as a percentage without a sign: 0.2 → "20,0 %".</summary>
    public static string Pct(decimal fraction, int decimals = 1) => (fraction * 100m).ToString(Pattern(decimals), Sv) + " %";

    /// <summary>A price with at least two and at most four decimals: 70.8 → "70,80", 0.1234 → "0,1234".</summary>
    public static string Price(decimal value) => value.ToString("#,0.00##", Sv);

    /// <summary>A whole number of shares: 1200 → "1 200".</summary>
    public static string Count(long value) => value.ToString("#,0", Sv);

    /// <summary>A change with its arrow, as a chip shows it: "▲ +1,23 %", "▼ −0,40 %", "0,00 %".</summary>
    public static string Arrow(decimal fraction) =>
        (fraction > 0 ? "▲ " : fraction < 0 ? "▼ " : string.Empty) + ChangePct(fraction);

    private static string Pattern(int decimals) => decimals <= 0 ? "#,0" : "#,0." + new string('0', decimals);
}
