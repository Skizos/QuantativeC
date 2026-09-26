using QuantAnalyst.Core.Broker;
using QuantAnalyst.Trading.Pipeline;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

/// <summary>
/// From Avanza's pre-trade answers to the risk engine (Phase 7 step 1): R21 judges the validation, R9 counts
/// Avanza's fee when it has one, and a fee far from the model's is flagged.
/// </summary>
public sealed class PreflightAssessmentTests
{
    private static readonly PreTradeRiskEngine Engine = new(RiskLimits.AdrDefaults);

    private static PreflightValidation Validation(params (string Name, bool Valid)[] checks) =>
        new([.. checks.Select(c => new PreflightCheck(c.Name, c.Valid))]);

    private static PreliminaryFee Fee(decimal totalFees, decimal fxFee = 0m, string currency = "SEK") =>
        new(currency, totalFees, 0m, totalFees, 1_000m + totalFees, 1_000m, null, 1m, fxFee);

    private static RiskCheckResult R21(PreflightOutcome outcome, TradingMode mode = TradingMode.Confirm) =>
        Engine.Evaluate(RiskEngineTests.Buy(), RiskEngineTests.Baseline(mode) with
        {
            Verified = new VerifiedConstants(true, true, true),
            Preflight = BrokerPreflight.From(outcome),
        })["R21"];

    [Fact]
    public void AllValid_PassesR21()
    {
        RiskCheckResult r = R21(new PreflightOutcome(Validation(("commissionWarning", true), ("priceRampingWarning", true)), Fee(0m), BrokerFault.None, null));
        Assert.True(r.Passed);
        Assert.Equal("all valid", r.Observed);
    }

    [Fact]
    public void AValidFalse_FailsR21_Live_AndNamesTheCheck_ButIsOnlyLoggedInPaper()
    {
        var outcome = new PreflightOutcome(Validation(("commissionWarning", false), ("priceRampingWarning", true)), Fee(0m), BrokerFault.None, null);

        RiskCheckResult live = R21(outcome);
        Assert.False(live.Passed);
        Assert.Equal("commissionWarning", live.Observed);

        RiskCheckResult paper = R21(outcome, TradingMode.Paper);
        Assert.True(paper.Passed);
        Assert.Contains("logged only: commissionWarning", paper.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void NoValidationAnswer_FailsR21_Live_WhateverTheReason()
    {
        Assert.False(R21(PreflightOutcome.Failed(BrokerFault.None, "validate: HTTP 503")).Passed);
        Assert.False(R21(PreflightOutcome.Failed(BrokerFault.SchemaDrift, "validate: drift")).Passed);
        Assert.Null(BrokerPreflight.From(PreflightOutcome.Failed(BrokerFault.SessionExpired, "validate: 401")));
    }

    [Fact]
    public void AnEmptyValidation_IsNotAllValid()
    {
        Assert.False(R21(new PreflightOutcome(Validation(), null, BrokerFault.None, null)).Passed);
    }

    [Fact]
    public void Fees_R9UsesAvanzas_AndADifferenceAboveOneSekIsFlagged()
    {
        FeeComparison same = FeeComparison.Of(1.00m, new PreflightOutcome(Validation(("x", true)), Fee(1.50m), BrokerFault.None, null));
        Assert.Equal((1.50m, null), (same.FeesForRiskCheck, same.Warning)); // 0.50 apart: fine

        FeeComparison apart = FeeComparison.Of(0m, new PreflightOutcome(Validation(("x", true)), Fee(1.00m, fxFee: 0.50m), BrokerFault.None, null));
        Assert.Equal(1.50m, apart.FeesForRiskCheck); // fees plus the FX fee
        Assert.Contains("differs from the model's 0.00 by +1.50", apart.Warning, StringComparison.Ordinal);

        FeeComparison exactlyOne = FeeComparison.Of(0m, new PreflightOutcome(Validation(("x", true)), Fee(1.00m), BrokerFault.None, null));
        Assert.Null(exactlyOne.Warning); // "more than 1 SEK"
    }

    [Fact]
    public void Fees_WithoutAUsableAvanzaFee_FallBackToTheModel_WithAWarning()
    {
        FeeComparison missing = FeeComparison.Of(2m, new PreflightOutcome(Validation(("x", true)), null, BrokerFault.None, "preliminary fee: HTTP 503"));
        Assert.Equal((2m, (decimal?)null), (missing.FeesForRiskCheck, missing.AvanzaFees));
        Assert.Contains("not available (preliminary fee: HTTP 503)", missing.Warning, StringComparison.Ordinal);

        FeeComparison foreign = FeeComparison.Of(2m, new PreflightOutcome(Validation(("x", true)), Fee(1m, currency: "USD"), BrokerFault.None, null));
        Assert.Equal(2m, foreign.FeesForRiskCheck);
        Assert.Contains("in USD, not SEK", foreign.Warning, StringComparison.Ordinal);
    }
}
