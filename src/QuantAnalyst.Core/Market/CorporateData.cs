namespace QuantAnalyst.Core.Market;

/// <summary>
/// A cash dividend per share (plan 21). Avanza states past amounts per <b>current</b> share (adjusted for later splits),
/// so an amount can change after a split.
/// </summary>
/// <param name="PaymentDate">Null until the company announces it.</param>
/// <param name="Type">E.g. "ORDINARY", as the broker names it.</param>
public sealed record DividendEvent(DateOnly ExDate, DateOnly? PaymentDate, decimal Amount, string Currency, string Type);

/// <summary>What the broker says about a share's dividends and share count (plan 21): the dividends and the splits check.</summary>
/// <param name="Dividends">Past and announced dividends, ordered by ex-date.</param>
/// <param name="SharesOutstanding">The company's number of shares; null when not given. A split k:1 multiplies it by k.</param>
public sealed record CorporateData(OrderbookId OrderbookId, IReadOnlyList<DividendEvent> Dividends, decimal? SharesOutstanding, DateTimeOffset RetrievedAtUtc);
