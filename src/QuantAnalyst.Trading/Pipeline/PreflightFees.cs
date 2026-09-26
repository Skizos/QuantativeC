using System.Globalization;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Trading.Pipeline;

/// <summary>
/// Avanza's preliminary fee next to the model's courtage (ADR 0003 R9/R21, plan 07). Live, R9 uses Avanza's fee when
/// there is one in the account currency, else the model's; the order card shows both and flags a difference of more
/// than <see cref="FlagAboveSek"/>.
/// </summary>
public sealed record FeeComparison(decimal ModelFees, decimal? AvanzaFees, string? Warning)
{
    /// <summary>ADR 0003 §2: a fee difference above 1 SEK is a warning on the card, not a block.</summary>
    public const decimal FlagAboveSek = 1m;

    /// <summary>Gets the fee R9 counts: Avanza's when known, else the model's.</summary>
    public decimal FeesForRiskCheck => AvanzaFees ?? ModelFees;

    public static FeeComparison Of(decimal modelFees, PreflightOutcome outcome, string accountCurrency = "SEK")
    {
        ArgumentNullException.ThrowIfNull(outcome);
        CultureInfo c = CultureInfo.InvariantCulture;
        if (outcome.Fee is not { } fee)
        {
            return new FeeComparison(modelFees, null, $"Avanza's fee is not available ({outcome.Problem ?? "not asked"}); R9 uses the model's {modelFees:N2} {accountCurrency}.");
        }

        if (!string.Equals(fee.Currency, accountCurrency, StringComparison.Ordinal))
        {
            return new FeeComparison(modelFees, null, string.Create(c, $"Avanza's fee is in {fee.Currency}, not {accountCurrency}; R9 uses the model's {modelFees:N2} {accountCurrency}."));
        }

        decimal avanza = fee.AllFees;
        decimal difference = avanza - modelFees;
        string? warning = Math.Abs(difference) > FlagAboveSek
            ? string.Create(c, $"Avanza's fee {avanza:N2} {accountCurrency} differs from the model's {modelFees:N2} by {difference:+0.00;-0.00}: check the courtage class.")
            : null;
        return new FeeComparison(modelFees, avanza, warning);
    }
}
