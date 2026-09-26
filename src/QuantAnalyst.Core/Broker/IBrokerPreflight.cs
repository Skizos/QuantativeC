namespace QuantAnalyst.Core.Broker;

/// <summary>
/// The broker's own pre-trade checks (ADR 0003 §2 <c>BrokerPreflight</c>; R9 and R21), asked in live modes only, just
/// before an order card is shown and again after the typed confirmation. Both calls are read-only: nothing is placed.
/// An outcome without a validation answer fails R21, so a preflight that could not run never lets an order through.
/// </summary>
public interface IBrokerPreflight
{
    Task<PreflightOutcome> CheckAsync(PreflightRequest request, CancellationToken ct);
}

/// <summary>The order exactly as it would be placed: whole lots and a tick-rounded limit, valid for the day.</summary>
public sealed record PreflightRequest(
    AccountId Account, OrderbookId OrderbookId, string Isin, string Currency, string MarketPlace, OrderSide Side, long Volume, decimal LimitPrice);

/// <summary>One named check from the broker's validation (e.g. <c>commissionWarning</c>).</summary>
public sealed record PreflightCheck(string Name, bool Valid);

/// <summary>The broker's validation answer: every named check and whether it passed.</summary>
public sealed record PreflightValidation(IReadOnlyList<PreflightCheck> Checks)
{
    /// <summary>Gets a value indicating whether there was at least one check and every one passed.</summary>
    public bool AllValid => Checks.Count > 0 && Checks.All(c => c.Valid);

    public IReadOnlyList<string> Failures => [.. Checks.Where(c => !c.Valid).Select(c => c.Name)];
}

/// <summary>
/// The broker's preliminary fee for the order. Amounts are in <see cref="Currency"/> (the orderbook currency); the FX
/// fee is what converting to the account currency costs (0 for SEK instruments).
/// </summary>
public sealed record PreliminaryFee(
    string Currency,
    decimal Commission,
    decimal MarketFees,
    decimal TotalFees,
    decimal TotalSum,
    decimal TotalSumWithoutFees,
    decimal? TransactionTax,
    decimal? FxRate,
    decimal FxFee)
{
    /// <summary>Gets everything the order costs on top of its value: total fees plus the FX fee.</summary>
    public decimal AllFees => TotalFees + FxFee;
}

/// <summary>
/// What a preflight produced. <see cref="Fault"/> is set when the answer means trading must halt (schema drift, session
/// gone, endpoint moved); <see cref="Problem"/> says why a part is missing. Either part may be null.
/// </summary>
public sealed record PreflightOutcome(PreflightValidation? Validation, PreliminaryFee? Fee, BrokerFault Fault, string? Problem)
{
    public static PreflightOutcome Failed(BrokerFault fault, string problem) => new(null, null, fault, problem);
}
