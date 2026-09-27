using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Pipeline;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Confirm;

/// <summary>
/// The Confirm order card (ADR 0003 §5): the masked account; the instrument (name, ticker, orderbook id, ISIN); side,
/// volume and the tick-rounded limit with its rounding direction; the SEK value; Avanza's fee next to the model's;
/// the reason; the decision price and time; the market with its age; and every risk check, observed against its limit.
/// It ends with exactly what to type. The gateway builds it only for an order that passed every check.
/// </summary>
public sealed record OrderCard
{
    private const int Width = 70;
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>Gets a value indicating whether the channel is simulated: then nothing reaches Avanza, and the card says so.</summary>
    public required bool Simulated { get; init; }

    public required string Channel { get; init; }

    public required AccountId Account { get; init; }

    public required string Ticker { get; init; }

    public required string Name { get; init; }

    public required OrderbookId OrderbookId { get; init; }

    public required string? Isin { get; init; }

    public required OrderSide Side { get; init; }

    public required long Volume { get; init; }

    public required decimal Limit { get; init; }

    /// <summary>Gets the limit before tick rounding, when it differed.</summary>
    public required decimal? RoundedFrom { get; init; }

    public required decimal? Tick { get; init; }

    public required string Currency { get; init; }

    public required FeeComparison Fees { get; init; }

    public required string Reason { get; init; }

    public required decimal? DecisionPrice { get; init; }

    public required DateTimeOffset DecisionTimeUtc { get; init; }

    public required Quote? Quote { get; init; }

    public required DateTimeOffset NowUtc { get; init; }

    public required RiskReport Risk { get; init; }

    public decimal Value => Volume * Limit;

    /// <summary>Gets exactly what to type to send this order.</summary>
    public string Answer => ConfirmationPrompt.ExpectedAnswer(Ticker);

    public static OrderCard Create(PreparedOrder order, InstrumentSpec spec, RiskContext ctx, RiskReport risk, FeeComparison fees, string channel, bool simulated)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(ctx);
        decimal limit = order.LimitPrice ?? throw new ArgumentException("An order card needs a limit (R3).", nameof(order));
        decimal? tick;
        try
        {
            tick = spec.TickSizes.TickAt(limit);
        }
        catch (ArgumentOutOfRangeException)
        {
            tick = null;
        }

        return new OrderCard
        {
            Simulated = simulated,
            Channel = channel,
            Account = ctx.Account,
            Ticker = order.Intent.Ticker,
            Name = spec.Name,
            OrderbookId = order.OrderbookId,
            Isin = spec.Isin,
            Side = order.Side,
            Volume = order.Volume,
            Limit = limit,
            RoundedFrom = order.RoundedFrom is { } from && from != limit ? from : null,
            Tick = tick,
            Currency = spec.Currency,
            Fees = fees,
            Reason = order.Intent.Reason,
            DecisionPrice = order.Intent.DecisionPrice,
            DecisionTimeUtc = order.Intent.DecisionTimeUtc,
            Quote = ctx.Quote,
            NowUtc = ctx.NowUtc,
            Risk = risk,
        };
    }

    /// <summary>The card as terminal lines; <paramref name="number"/> is the card's number in this session.</summary>
    public IReadOnlyList<string> Render(int? number = null)
    {
        string rule = new('─', Width);
        string where = Simulated ? $"rehearsal on the {Channel} channel: nothing is sent to Avanza" : "a real order on your Avanza account";
        var lines = new List<string>
        {
            rule,
            $" {(number is { } n ? $"ORDER {n} · " : string.Empty)}CONFIRM MODE · {where}",
            rule,
            Row("Account", Account.Masked),
            Row("Instrument", $"{Name}   {Ticker} · orderbook {OrderbookId.Value}{(Isin is null ? string.Empty : " · " + Isin)}"),
            Row("Side", Side == OrderSide.Buy ? "BUY" : "SELL"),
            Row("Volume", Volume.ToString("N0", C)),
            Row("Limit", $"{Price(Limit)} {Currency}    ({Rounding()})"),
            Row("Value", $"{Value.ToString("N2", C)} {Currency}"),
            Row("Fee", $"Avanza {(Fees.AvanzaFees is { } a ? $"{a.ToString("N2", C)} SEK" : "not available")} · model {Fees.ModelFees.ToString("N2", C)} SEK"),
        };
        if (Fees.Warning is { } warning)
        {
            lines.Add(Row(string.Empty, "! " + warning));
        }

        lines.Add(Row("Reason", Reason));
        string decided = MarketTime.ToStockholm(DecisionTimeUtc).ToString("HH:mm:ss", C);
        lines.Add(Row("Decided", DecisionPrice is { } p ? $"{decided} at {Price(p)}" : decided));
        lines.Add(Row("Market", Market()));
        lines.Add(string.Empty);

        int passed = Risk.Checks.Count(c => c.Passed);
        lines.Add($" Risk checks: {passed} of {Risk.Checks.Count} pass");
        int name = Risk.Checks.Max(c => c.Name.Length) + 2;
        foreach (RiskCheckResult c in Risk.Checks)
        {
            lines.Add($"   {c.Id,-4}{c.Name.PadRight(name)}{c.Observed}  (limit {c.Limit})  {(c.Passed ? "ok" : "FAIL")}");
        }

        lines.Add(string.Empty);
        lines.Add($" Type  {Answer}  within {ConfirmationPrompt.Expiry.TotalSeconds:0} s to send. Anything else skips this order.");
        return lines;
    }

    private static string Row(string label, string value) => $" {label,-12}{value}";

    private static string Price(decimal price) => price.ToString("0.00##", C);

    private string Rounding()
    {
        string tick = Tick is { } t ? $" to the {Price(t)} tick" : string.Empty;
        return RoundedFrom is { } from
            ? $"rounded {(Side == OrderSide.Buy ? "down" : "up")} from {from.ToString("0.#####", C)}{tick}"
            : $"on tick{(Tick is { } t2 ? $" ({Price(t2)})" : string.Empty)}";
    }

    private string Market()
    {
        if (Quote is not { } q)
        {
            return "no quote";
        }

        string bid = q.Bid is { } b ? $"bid {Price(b)} × {q.BidVolume.ToString("N0", C)}" : "no bid";
        string ask = q.Ask is { } a ? $"ask {Price(a)} × {q.AskVolume.ToString("N0", C)}" : "no ask";
        string last = q.Last is { } l ? $"last {Price(l)}" : "no last";
        string age = q.AsOfUtc is { } asOf ? $"{Math.Max(0, (NowUtc - asOf).TotalSeconds):0} s old" : "age unknown";
        return $"{bid} · {ask} · {last} · {age}";
    }
}
