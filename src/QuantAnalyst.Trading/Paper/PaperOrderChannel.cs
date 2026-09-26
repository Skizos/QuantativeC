using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Model;

namespace QuantAnalyst.Trading.Paper;

/// <summary>
/// The Paper order channel (ADR 0003 §8). Nothing leaves the process: orders rest in memory and fill against live
/// quotes, conservatively.
/// <list type="bullet">
/// <item>Marketable at entry (buy limit ≥ best ask, sell limit ≤ best bid): fills at the ask (bid) up to the displayed
/// volume at that level; the rest rests.</item>
/// <item>Resting: fills only when a later trade prints <b>through</b> the limit (last &lt; buy limit, last &gt; sell
/// limit; a touch is not a fill), at the limit, capped at 10 % of the traded-volume increment since the order last
/// looked.</item>
/// <item>Courtage from the book's courtage class, charged per order: each fill pays the difference between the courtage
/// on the order's cumulative filled value and what it has paid so far, so the minimum fee is paid once. FX fee for
/// non-SEK instruments.</item>
/// <item>Day orders: <see cref="EndOfDay"/> ends every resting order.</item>
/// </list>
/// Fill and end events are raised under the channel's lock, so they reach the OMS in order and never race a cancel.
/// </summary>
public sealed class PaperOrderChannel : ISimulatedOrderChannel
{
    public const string ChannelName = "paper";

    /// <summary>ADR 0003 §8: a resting order takes at most this share of the volume printed through it.</summary>
    public const decimal Participation = 0.10m;

    private readonly Lock _lock = new();
    private readonly Dictionary<OrderId, Resting> _resting = [];
    private readonly PaperBook _book;
    private readonly CostModel _costs;
    private readonly IQuoteSource _quotes;
    private readonly IInstrumentCatalog _instruments;
    private readonly TimeProvider _time;
    private long _next;

    public PaperOrderChannel(PaperBook book, CostModel costs, IQuoteSource quotes, IInstrumentCatalog instruments, TimeProvider time)
    {
        _book = book ?? throw new ArgumentNullException(nameof(book));
        _costs = costs ?? throw new ArgumentNullException(nameof(costs));
        _quotes = quotes ?? throw new ArgumentNullException(nameof(quotes));
        _instruments = instruments ?? throw new ArgumentNullException(nameof(instruments));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public event Action<SimulatedFill>? Filled;

    public event Action<OrderId, string>? Ended;

    public string Name => ChannelName;

    public int RestingCount
    {
        get
        {
            lock (_lock)
            {
                return _resting.Count;
            }
        }
    }

    /// <summary>Courtage plus FX fee the model charges for an order of <paramref name="value"/> (the risk engine's R9 input).</summary>
    public decimal EstimateFees(decimal value, string currency) =>
        Round(_costs.Courtage(value)) + FxFee(value, currency);

    public Task<OrderSubmitResult> PlaceAsync(ApprovedOrder order, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        InstrumentSpec? spec = _instruments.Find(order.OrderbookId);
        lock (_lock)
        {
            if (order.Account != _book.Account)
            {
                return Task.FromResult(OrderSubmitResult.Rejected($"the paper book is account {PaperConfig.AccountId}; the order is for another account"));
            }

            if (spec is null)
            {
                return Task.FromResult(OrderSubmitResult.Rejected($"orderbook {order.OrderbookId} is not known to the paper channel"));
            }

            // The checks a broker makes: buying power and shares held (the risk engine checks both first).
            string currency = spec.Currency;
            if (order.Side == OrderSide.Buy)
            {
                decimal needed = order.Volume * order.LimitPrice + EstimateFees(order.Volume * order.LimitPrice, currency);
                decimal available = _book.Cash - _book.Reserved;
                if (needed > available)
                {
                    return Task.FromResult(OrderSubmitResult.Rejected($"insufficient paper buying power: {needed:N2} SEK needed, {available:N2} available"));
                }
            }
            else
            {
                long selling = _resting.Values.Where(r => r.Order.OrderbookId == order.OrderbookId && r.Order.Side == OrderSide.Sell).Sum(r => r.Remaining);
                long held = _book.Position(order.OrderbookId);
                if (order.Volume + selling > held)
                {
                    return Task.FromResult(OrderSubmitResult.Rejected($"paper position {held} does not cover this sell of {order.Volume} plus {selling} already for sale"));
                }
            }

            var id = new OrderId($"PAPER-{++_next}");
            Quote? quote = _quotes.Latest(order.OrderbookId);
            var resting = new Resting(order, id, spec, quote?.TotalVolumeTraded);
            _resting[id] = resting;
            Reserve(resting);

            // Marketable at entry: take the best level's displayed volume at its price.
            decimal? touch = order.Side == OrderSide.Buy ? quote?.Ask : quote?.Bid;
            decimal shown = (order.Side == OrderSide.Buy ? quote?.AskVolume : quote?.BidVolume) ?? 0m;
            bool marketable = touch is { } t && (order.Side == OrderSide.Buy ? order.LimitPrice >= t : order.LimitPrice <= t);
            if (marketable)
            {
                long quantity = Lots(Math.Min(resting.Remaining, (long)decimal.Floor(shown)), spec.LotSize);
                if (quantity > 0)
                {
                    Fill(resting, quantity, touch!.Value, "marketable at entry");
                }
            }

            return Task.FromResult(OrderSubmitResult.Accepted(id, marketable ? "marketable at entry" : "resting"));
        }
    }

    public Task<OrderSubmitResult> ModifyAsync(ApprovedModify modify, CancellationToken ct) =>
        Task.FromResult(OrderSubmitResult.Rejected("modify is not used in Paper yet; cancel and place a new order"));

    public Task<OrderSubmitResult> CancelAsync(ApprovedCancel cancel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        lock (_lock)
        {
            if (!_resting.Remove(cancel.BrokerOrderId, out Resting? r))
            {
                return Task.FromResult(OrderSubmitResult.Rejected($"no working paper order {cancel.BrokerOrderId}"));
            }

            _book.Release(r.Order.ClientOrderId);
            return Task.FromResult(OrderSubmitResult.Accepted(cancel.BrokerOrderId, $"cancelled with {r.Filled} of {r.Order.Volume} filled"));
        }
    }

    /// <summary>
    /// Feeds a composed quote. A trade printed through a resting limit since the order last looked fills it (at the
    /// limit, capped at <see cref="Participation"/> of the volume increment). Returns the number of fills.
    /// </summary>
    public int OnQuote(Quote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        int fills = 0;
        lock (_lock)
        {
            foreach (Resting r in _resting.Values.Where(r => r.Order.OrderbookId == quote.OrderbookId).OrderBy(r => r.Sequence).ToList())
            {
                if (quote.TotalVolumeTraded is not { } total)
                {
                    continue;
                }

                if (r.VolumeSeen is not { } seen || total < seen)
                {
                    r.VolumeSeen = total; // first look, or the daily volume restarted
                    continue;
                }

                decimal increment = total - seen;
                r.VolumeSeen = total;
                if (increment <= 0 || quote.Last is not { } last)
                {
                    continue;
                }

                bool through = r.Order.Side == OrderSide.Buy ? last < r.Order.LimitPrice : last > r.Order.LimitPrice;
                long quantity = Lots(Math.Min(r.Remaining, (long)decimal.Floor(Participation * increment)), r.Spec.LotSize);
                if (through && quantity > 0)
                {
                    Fill(r, quantity, r.Order.LimitPrice, "traded through the limit");
                    fills++;
                }
            }
        }

        return fills;
    }

    /// <summary>Ends every resting order (day orders at the close, or a shutdown). Returns how many ended.</summary>
    public int EndOfDay(string reason)
    {
        lock (_lock)
        {
            List<Resting> all = [.. _resting.Values.OrderBy(r => r.Sequence)];
            _resting.Clear();
            foreach (Resting r in all)
            {
                _book.Release(r.Order.ClientOrderId);
                Ended?.Invoke(r.BrokerId, reason);
            }

            return all.Count;
        }
    }

    private void Fill(Resting r, long quantity, decimal price, string why)
    {
        decimal cumulative = r.FilledValue + quantity * price;
        decimal courtage = Math.Max(0m, Round(_costs.Courtage(cumulative)) - r.CourtagePaid);
        decimal fx = FxFee(quantity * price, r.Spec.Currency);
        DateTimeOffset now = _time.GetUtcNow();

        _book.ApplyFill(r.Order.ClientOrderId, r.Order.OrderbookId, r.Spec.Ticker, r.Order.Side, quantity, price, courtage, fx, now);
        r.Filled += quantity;
        r.FilledValue = cumulative;
        r.CourtagePaid += courtage;
        if (r.Remaining == 0)
        {
            _resting.Remove(r.BrokerId);
            _book.Release(r.Order.ClientOrderId);
        }
        else
        {
            Reserve(r);
        }

        Filled?.Invoke(new SimulatedFill(r.Order.ClientOrderId, r.BrokerId, quantity, price, courtage, fx, now, why));
    }

    private void Reserve(Resting r)
    {
        if (r.Order.Side != OrderSide.Buy)
        {
            return;
        }

        // The rest at the limit plus the courtage still to pay on the whole order.
        decimal rest = r.Remaining * r.Order.LimitPrice;
        decimal courtageLeft = Math.Max(0m, Round(_costs.Courtage(r.FilledValue + rest)) - r.CourtagePaid);
        _book.Reserve(r.Order.ClientOrderId, rest + courtageLeft + FxFee(rest, r.Spec.Currency));
    }

    private decimal FxFee(decimal value, string currency) =>
        string.Equals(currency, _costs.Currency, StringComparison.Ordinal) ? 0m : Round(_costs.FxFeeRate * value);

    private static long Lots(long quantity, long lot) => quantity <= 0 ? 0 : quantity / lot * lot;

    private static decimal Round(decimal sek) => decimal.Round(sek, 2, MidpointRounding.AwayFromZero);

    private sealed class Resting(ApprovedOrder order, OrderId brokerId, InstrumentSpec spec, decimal? volumeSeen)
    {
        private static long _sequence;

        public ApprovedOrder Order { get; } = order;

        public OrderId BrokerId { get; } = brokerId;

        public InstrumentSpec Spec { get; } = spec;

        public long Sequence { get; } = Interlocked.Increment(ref _sequence);

        public decimal? VolumeSeen { get; set; } = volumeSeen;

        public long Filled { get; set; }

        public decimal FilledValue { get; set; }

        public decimal CourtagePaid { get; set; }

        public long Remaining => Order.Volume - Filled;
    }
}
