using QuantAnalyst.Core;
using QuantAnalyst.Trading.Oms;

namespace QuantAnalyst.Trading.Observation;

/// <summary>An instrument the session trades, with the close it is compared with today.</summary>
public sealed record ObservedInstrument(OrderbookId OrderbookId, string Ticker, string Name, decimal? PreviousClose);

/// <summary>The day's frame, sent once when the session is ready to trade.</summary>
public sealed record SessionStarted(
    DateTimeOffset AtUtc,
    DateTimeOffset? OpenUtc,
    DateTimeOffset? CloseUtc,
    DateTimeOffset? DecisionUtc,
    string Strategy,
    IReadOnlyList<ObservedInstrument> Instruments);

/// <summary>A composed quote of one instrument (at most about one a second each).</summary>
public sealed record QuoteTick(DateTimeOffset AtUtc, OrderbookId OrderbookId, decimal? Bid, decimal? Ask, decimal? Last);

/// <summary>A holding of the account as the session values it.</summary>
public sealed record ObservedPosition(OrderbookId OrderbookId, string Ticker, long Quantity, decimal CostBasis, decimal Value);

/// <summary>The account's value, cash and holdings (every few seconds, and at the start and the end).</summary>
public sealed record AccountTick(
    DateTimeOffset AtUtc, decimal Value, decimal Cash, decimal StartOfDayValue, decimal FeesPaid, IReadOnlyList<ObservedPosition> Positions);

/// <summary>An order as the order book has it after a change (sent, working, a fill, cancelled, …).</summary>
public sealed record OrderTick(
    DateTimeOffset AtUtc, Guid ClientOrderId, OrderbookId OrderbookId, string Ticker, OrderSide Side, long Volume, decimal? Limit, OmsState State,
    long Filled, decimal? AveragePrice, DateTimeOffset CreatedUtc)
{
    public static OrderTick From(OmsOrder order, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new OrderTick(atUtc, order.ClientOrderId, order.OrderbookId, order.Ticker, order.Side, order.Volume, order.LimitPrice, order.State,
            order.FilledVolume, order.AverageFillPrice, order.CreatedUtc);
    }
}

/// <summary>The day's decision: how many orders, and the plan's notes (or why there was none).</summary>
public sealed record DecisionTick(DateTimeOffset AtUtc, int Orders, IReadOnlyList<string> Notes);

/// <summary>
/// Watches a running session without taking part in it (docs/plans/11-app-redesign.md): the Windows app draws its
/// charts from these calls. Calls come from the session's own threads; an observer returns quickly and never blocks.
/// The terminal passes none. Whatever an observer does can't change trading: sessions call it through
/// <see cref="GuardedObserver"/>, which swallows its exceptions.
/// </summary>
public interface ISessionObserver
{
    void Started(SessionStarted e);

    void Quote(QuoteTick e);

    void Account(AccountTick e);

    void Order(OrderTick e);

    void Decision(DecisionTick e);
}

/// <summary>Calls an observer and swallows anything it throws: watching must never stop or change a session.</summary>
public sealed class GuardedObserver(ISessionObserver inner) : ISessionObserver
{
    public void Started(SessionStarted e) => Guard(() => inner.Started(e));

    public void Quote(QuoteTick e) => Guard(() => inner.Quote(e));

    public void Account(AccountTick e) => Guard(() => inner.Account(e));

    public void Order(OrderTick e) => Guard(() => inner.Order(e));

    public void Decision(DecisionTick e) => Guard(() => inner.Decision(e));

    /// <summary>The observer to call, guarded, or null for none.</summary>
    public static ISessionObserver? Wrap(ISessionObserver? observer) => observer is null or GuardedObserver ? observer : new GuardedObserver(observer);

    private static void Guard(Action call)
    {
        try
        {
            call();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An observer's failure is its own; the session goes on exactly as it would without one.
        }
    }
}
