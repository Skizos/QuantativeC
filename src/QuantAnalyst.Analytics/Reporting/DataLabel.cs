using System.Globalization;
using QuantAnalyst.Analytics.Data;

namespace QuantAnalyst.Analytics.Reporting;

/// <summary>
/// Provenance and assumptions printed with every result (spec &lt;quant_rules&gt;): data source,
/// survivorship status, date range, currency, return definition and account taxation.
/// </summary>
/// <param name="Source">Where the data came from.</param>
/// <param name="From">First period end date.</param>
/// <param name="To">Last period end date.</param>
/// <param name="Observations">Number of return observations.</param>
/// <param name="Returns">Return definition.</param>
/// <param name="Currency">Currency the prices and values are assumed to be in.</param>
/// <param name="Survivorship">Survivorship-bias statement.</param>
/// <param name="Taxation">Account taxation assumption.</param>
public sealed record DataLabel(
    string Source,
    DateOnly From,
    DateOnly To,
    int Observations,
    ReturnKind Returns,
    string Currency,
    string Survivorship,
    string Taxation)
{
    /// <summary>Default survivorship statement for user-supplied or Avanza-derived history.</summary>
    public const string NotSurvivorshipFree =
        "NOT survivorship-free (user-supplied history; Avanza history omits delisted companies)";

    /// <summary>Default taxation statement (kickoff decision: ISK).</summary>
    public const string IskTaxation = "ISK (schablonbeskattning): no per-trade tax modelled; not tax advice";

    /// <summary>Builds the default label for a returns table.</summary>
    public static DataLabel For(ReturnsTable returns, string currency = "SEK")
    {
        ArgumentNullException.ThrowIfNull(returns);
        return new DataLabel(
            returns.Source,
            returns.Dates[0],
            returns.Dates[^1],
            returns.Returns.Rows,
            returns.Kind,
            currency,
            NotSurvivorshipFree,
            IskTaxation);
    }

    /// <summary>Human-readable lines.</summary>
    public IReadOnlyList<string> Describe() =>
    [
        string.Create(CultureInfo.InvariantCulture, $"Data: {Source} | {From:yyyy-MM-dd}..{To:yyyy-MM-dd} | {Observations} {Returns.ToString().ToLowerInvariant()} returns"),
        $"Currency: {Currency} (assumed) | Survivorship: {Survivorship}",
        $"Account: {Taxation}",
    ];
}
