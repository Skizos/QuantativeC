using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Core.Orders;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Reconciliation;

namespace QuantAnalyst.Trading.Paper;

/// <summary>
/// The Paper order channel (ADR 0003 §8). Nothing leaves the process: orders rest in memory and fill against live
/// quotes, conservatively.
/// <list type="bullet">
/// <item>Marketable at entry (buy limit ≥ best ask, sell limit ≤ best bid): fills at the ask (bid) up to the displayed
/// volume at that level; the rest rests.</item>
/// <item>Resting: fills only when a later trade prints <b>through</b> the limit (last &lt; buy limit, last &gt; sell
/// limit; a touch is not a fill), at the limit, capped at 10 % of the traded-volume increment since the order last
/// looked. Several resting orders in one instrument share that 10 %, oldest first.</item>
/// <item>Courtage from the book's courtage class, charged per order: each fill pays the difference between the courtage
/// on the order's cumulative filled value and what it has paid so far, so the minimum fee is paid once. A US or
/// Canadian share (ADR 0005) pays the class's foreign courtage, in its currency, converted to SEK at the day's rate, and
/// the FX fee on its value. Buying power, reservations and fees are SEK; prices stay in the share's currency.</item>
/// <item>Day orders: <see cref="EndOfDay"/> ends every resting order.</item>
/// </list>
/// Fill and end events are raised under the channel's lock, so they reach the OMS in order and never race a cancel.
/// </summary>
public sealed class PaperOrderChannel : ISimulatedOrderChannel, IBrokerStateSource
{
    public const string ChannelName = "paper";

    /// <summary>ADR 0003 §8: a resting order takes at most this share of the volume printed through it.</summary>
    public const decimal Participation = 0.10m;

    private readonly Lock _lock = new();
    private readonly Dictionary<OrderId, Resting> _resting = [];
    private readonly List<BrokerDeal> _deals = [];
    private readonly PaperBook _book;
    private readonly CostModel _costs;
    private readonly IQuoteSource _quotes;
    private readonly IInstrumentCatalog _instruments;
    private readonly TimeProvider _time;
    private readonly IFxRates _fx;
    private long _next;

    /// <param name="fx">SEK per unit of each foreign currency traded (ADR 0005); default SEK only.</param>
    public PaperOrderChannel(PaperBook book, CostModel costs, IQuoteSource quotes, IInstrumentCatalog instruments, TimeProvider time, IFxRates? fx = null)
    {
        _fx = fx ?? FxTable.SekOnly;
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

    /// <summary>
    /// Courtage plus FX fee, in SEK, the model charges for an order of <paramref name="value"/> in <paramref name="currency"/>
    /// (the risk engine's R9 input). <paramref name="marketPlace"/>: a First North share pays its own courtage (plan 22).
    /// </summary>
    public decimal EstimateFees(decimal value, string currency, string? marketPlace = null) =>
        CourtageSek(value, currency, marketPlace) + FxFee(value, currency);

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
                decimal needed = (order.Volume * order.LimitPrice * Rate(currency)) + EstimateFees(order.Volume * order.LimitPrice, currency, spec.MarketPlace);
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
            decimal? arrival = quote is { Bid: { } b, Ask: { } a } ? (b + a) / 2 : quote?.Last;
            var resting = new Resting(order, id, spec, quote?.TotalVolumeTraded, _time.GetUtcNow(), arrival);
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
            // Our resting orders share the printed liquidity, first in first out: together they take at most 10 %.
            long taken = 0;
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

                // The market's VWAP over the order's life (end-of-day fill sanity), from volume increments at the last price.
                r.MarketValue += increment * last;
                r.MarketVolume += increment;

                bool through = r.Order.Side == OrderSide.Buy ? last < r.Order.LimitPrice : last > r.Order.LimitPrice;
                long cap = (long)decimal.Floor(Participation * increment) - taken;
                long quantity = Lots(Math.Min(r.Remaining, cap), r.Spec.LotSize);
                if (through && quantity > 0)
                {
                    Fill(r, quantity, r.Order.LimitPrice, "traded through the limit");
                    taken += quantity;
                    fills++;
                }
            }
        }

        return fills;
    }

    /// <summary>The paper "broker" state for reconciliation: resting orders and the session's deals.</summary>
    public Task<BrokerSnapshot> GetAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            BrokerOrder[] open = [.. _resting.Values.OrderBy(r => r.Sequence).Select(r => new BrokerOrder(
                r.BrokerId, r.Order.Account, r.Order.OrderbookId, r.Spec.Ticker, r.Order.Side, r.Order.LimitPrice, r.Remaining, r.Order.Volume,
                "ACTIVE", "NORMAL", r.CreatedUtc, r.Order.ValidUntil, Modifiable: false, Deletable: true))];
            return Task.FromResult(new BrokerSnapshot(open, [.. _deals], _time.GetUtcNow()));
        }
    }

    /// <summary>
    /// Ends the resting orders (day orders at the close, or a shutdown): all of them, or only those of the instruments
    /// <paramref name="which"/> picks (one market's close, ADR 0005). Returns how many ended.
    /// </summary>
    public int EndOfDay(string reason, Func<InstrumentSpec, bool>? which = null)
    {
        lock (_lock)
        {
            List<Resting> ending = [.. _resting.Values.Where(r => which is null || which(r.Spec)).OrderBy(r => r.Sequence)];
            foreach (Resting r in ending)
            {
                _resting.Remove(r.BrokerId);
                _book.Release(r.Order.ClientOrderId);
                Ended?.Invoke(r.BrokerId, reason);
            }

            return ending.Count;
        }
    }

    private void Fill(Resting r, long quantity, decimal price, string why)
    {
        decimal cumulative = r.FilledValue + (quantity * price);
        decimal courtage = Math.Max(0m, CourtageSek(cumulative, r.Spec.Currency, r.Spec.MarketPlace) - r.CourtagePaid);
        decimal fx = FxFee(quantity * price, r.Spec.Currency);
        DateTimeOffset now = _time.GetUtcNow();

        _book.ApplyFill(r.Order.ClientOrderId, r.Order.OrderbookId, r.Spec.Ticker, r.Order.Side, quantity, price, courtage, fx, now, r.Spec.Currency, Rate(r.Spec.Currency));
        r.Filled += quantity;
        r.FilledValue = cumulative;
        _deals.Add(new BrokerDeal($"{r.BrokerId}-{_deals.Count + 1}", r.BrokerId, r.Order.Account, r.Order.OrderbookId, r.Order.Side, price, quantity, now));
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

        decimal? vwap = r.MarketVolume > 0 ? decimal.Round(r.MarketValue / r.MarketVolume, 6) : null;
        Filled?.Invoke(new SimulatedFill(r.Order.ClientOrderId, r.BrokerId, quantity, price, courtage, fx, now, why, vwap, r.ArrivalPrice));
    }

    private void Reserve(Resting r)
    {
        if (r.Order.Side != OrderSide.Buy)
        {
            return;
        }

        // The rest at the limit (in SEK) plus the courtage still to pay on the whole order and the FX fee.
        decimal rest = r.Remaining * r.Order.LimitPrice;
        decimal courtageLeft = Math.Max(0m, CourtageSek(r.FilledValue + rest, r.Spec.Currency, r.Spec.MarketPlace) - r.CourtagePaid);
        _book.Reserve(r.Order.ClientOrderId, (rest * Rate(r.Spec.Currency)) + courtageLeft + FxFee(rest, r.Spec.Currency));
    }

    /// <summary>SEK per unit of <paramref name="currency"/> (1 for SEK). The gateway refuses a foreign order without a rate first.</summary>
    private decimal Rate(string currency) =>
        _fx.SekPerUnit(currency) ?? throw new InvalidOperationException($"No {currency}/SEK rate is known to the paper channel.");

    /// <summary>The class's courtage on <paramref name="value"/> in the share's currency, in SEK (ADR 0005); First North's own (plan 22).</summary>
    private decimal CourtageSek(decimal value, string currency, string? marketPlace) => Round(_costs.CourtageIn(currency, value, marketPlace) * Rate(currency));

    /// <summary>The FX fee on a foreign share's <paramref name="value"/>, in SEK; none for SEK.</summary>
    private decimal FxFee(decimal value, string currency) =>
        string.Equals(currency, _costs.Currency, StringComparison.Ordinal) ? 0m : Round(_costs.FxFeeRate * value * Rate(currency));

    private static long Lots(long quantity, long lot) => quantity <= 0 ? 0 : quantity / lot * lot;

    private static decimal Round(decimal sek) => decimal.Round(sek, 2, MidpointRounding.AwayFromZero);

    private sealed class Resting(ApprovedOrder order, OrderId brokerId, InstrumentSpec spec, decimal? volumeSeen, DateTimeOffset createdUtc, decimal? arrivalPrice)
    {
        private static long _sequence;

        public ApprovedOrder Order { get; } = order;

        public OrderId BrokerId { get; } = brokerId;

        public InstrumentSpec Spec { get; } = spec;

        public DateTimeOffset CreatedUtc { get; } = createdUtc;

        public decimal? ArrivalPrice { get; } = arrivalPrice;

        public decimal MarketValue { get; set; }

        public decimal MarketVolume { get; set; }

        public long Sequence { get; } = Interlocked.Increment(ref _sequence);

        public decimal? VolumeSeen { get; set; } = volumeSeen;

        public long Filled { get; set; }

        public decimal FilledValue { get; set; }

        public decimal CourtagePaid { get; set; }

        public long Remaining => Order.Volume - Filled;
    }
}
