using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Trading.Model;

/// <summary>What the pipeline needs to know about an instrument to normalise and round an order.</summary>
/// <param name="LotSize">Shares per lot (Avanza <c>tradingUnit</c>; 1 for Nasdaq Stockholm equities).</param>
/// <param name="TickTableVerified">True when the tick table came from Avanza's orderbook (authoritative); R20 input.</param>
/// <param name="Isin">For the order card and Avanza's pre-trade checks (live modes); null when unknown.</param>
/// <param name="MarketPlace">Avanza's market place code, which the pre-trade checks send; null when unknown.</param>
public sealed record InstrumentSpec(
    OrderbookId OrderbookId, string Ticker, string Name, string Currency, long LotSize, TickSizeTable TickSizes, bool TickTableVerified,
    string? Isin = null, string? MarketPlace = null);

public interface IInstrumentCatalog
{
    InstrumentSpec? Find(OrderbookId id);
}

public sealed class InstrumentCatalog(IEnumerable<InstrumentSpec> specs) : IInstrumentCatalog
{
    private readonly Dictionary<OrderbookId, InstrumentSpec> _byId = specs.ToDictionary(s => s.OrderbookId);

    public InstrumentSpec? Find(OrderbookId id) => _byId.GetValueOrDefault(id);
}

/// <summary>The latest composed quote per instrument (Phase 4 <c>QuoteComposer</c>), or null.</summary>
public interface IQuoteSource
{
    Quote? Latest(OrderbookId id);
}

/// <summary>Cash, positions and values of the account the orders are for, in SEK.</summary>
public sealed record AccountSnapshot(
    AccountId Account,
    decimal AccountValue,
    decimal AvailableCash,
    IReadOnlyDictionary<OrderbookId, long> Positions,
    IReadOnlyDictionary<OrderbookId, decimal> PositionValues,
    decimal StartOfDayValue);

/// <summary>The account state: the paper book in Paper, the broker's positions and cash live (Phase 7).</summary>
public interface IAccountState
{
    Task<AccountSnapshot> GetAsync(CancellationToken ct);

    /// <summary>Makes the next <see cref="GetAsync"/> read fresh data (after an order, a fill, or before a re-check). The paper book is always current.</summary>
    void Invalidate()
    {
    }
}

/// <summary>A fill reported by a simulated channel.</summary>
/// <param name="How">How the model filled it (e.g. "marketable at entry"), for the audit log.</param>
/// <param name="WindowVwap">VWAP of the market's prints between the order's entry and this fill, or null when none printed
/// (ADR 0003 §8: the end-of-day sanity comparison).</param>
/// <param name="ArrivalPrice">The mid (else last) when the order arrived; the comparison price for fills at entry.</param>
public sealed record SimulatedFill(
    Guid ClientOrderId, OrderId BrokerOrderId, long Volume, decimal Price, decimal Courtage, decimal FxFee, DateTimeOffset AtUtc, string How = "",
    decimal? WindowVwap = null, decimal? ArrivalPrice = null);

/// <summary>
/// Marker and event source for simulated channels (Paper, Backtest). Declared in Trading, which QuantAnalyst.Avanza does
/// not reference, so the real broker channel can never pose as simulated. In Phase 6 <see cref="OrderGateway"/> accepts
/// nothing else.
/// </summary>
public interface ISimulatedOrderChannel : IBrokerOrderChannel
{
    /// <summary>Raised when a resting or marketable order (partly) fills.</summary>
    event Action<SimulatedFill>? Filled;

    /// <summary>Raised when an order ends without a (full) fill, e.g. a day order expiring at the close.</summary>
    event Action<OrderId, string>? Ended;
}
