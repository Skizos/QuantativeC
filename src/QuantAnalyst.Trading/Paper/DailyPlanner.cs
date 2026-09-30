using System.Globalization;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Paper;

public sealed record PlanResult(IReadOnlyList<OrderIntent> Intents, IReadOnlyList<string> Notes);

/// <summary>
/// Turns a strategy's target weights (decided on bars through yesterday) into today's order intents at <b>live</b>
/// prices, the way the backtest's order planner does: long-only whole lots of (weight × investable equity), a no-trade
/// band, a minimum trade value, and a limit <c>LimitOffsetBps</c> toward the market from the reference price (last trade
/// if fresh, else the mid). Two deliberate differences, stated in the Paper report:
/// <list type="bullet">
/// <item>the limit is anchored on the live reference, not yesterday's close (R5's ±2 % collar would reject most gaps);</item>
/// <item>orders are clipped to what R6 (value per order), R7 (position size) and R8 (gross exposure) allow, so the plan
/// does not produce orders the risk engine would reject; the target is then reached over several days. R7 and R8 count
/// what the checks count: the holdings, the buys still working (<c>openOrders</c>, the gateway's
/// <see cref="OrderGateway.OpenOrders"/>), and for R8 the buys already planned in this decision.</item>
/// </list>
/// The equity it invests is the account's value, but at most the limits' account cap (<see cref="RiskLimits.SizingValue"/>),
/// the same value the limits are sized on. A US or Canadian share (ADR 0005) is sized in SEK at the day's rate
/// (<paramref name="fx"/>): its price in SEK decides the share count and every cap; its limit stays in its own currency.
/// </summary>
public static class DailyPlanner
{
    /// <summary>
    /// How many shares have a price the plan and the risk checks can use: a reference price (a fresh last trade, or a bid
    /// and an ask) on a quote that is not stale (R15). What a session's <see cref="Scheduling.PriceGate"/> waits for.
    /// </summary>
    public static Scheduling.PriceCoverage Coverage(IEnumerable<InstrumentSpec> instruments, IQuoteSource quotes, PreTradeRiskEngine risk, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(quotes);
        ArgumentNullException.ThrowIfNull(risk);
        int ready = 0, total = 0;
        string? missing = null;
        foreach (InstrumentSpec s in instruments)
        {
            total++;
            Quote? q = quotes.Latest(s.OrderbookId);
            string? why = q is null ? "no quote yet"
                : q.IsStale ? $"stale: {q.StaleReason}"
                : risk.ReferencePrice(q, nowUtc) is null ? "no fresh last trade and no bid/ask"
                : null;
            if (why is null)
            {
                ready++;
            }
            else
            {
                missing ??= $"{s.Ticker}: {why}";
            }
        }

        return new Scheduling.PriceCoverage(ready, total, missing);
    }

    /// <param name="openOrders">The orders still open (working, partly filled or Unknown), as the risk checks see them.</param>
    public static PlanResult Plan(
        IReadOnlyList<double> targets,
        IReadOnlyList<InstrumentSpec> instruments,
        AccountSnapshot account,
        IReadOnlyList<OpenOrderView> openOrders,
        IQuoteSource quotes,
        PreTradeRiskEngine risk,
        ExecutionOptions execution,
        string strategyId,
        DateTimeOffset nowUtc,
        IFxRates? fx = null)
    {
        fx ??= FxTable.SekOnly;
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(openOrders);
        ArgumentNullException.ThrowIfNull(quotes);
        ArgumentNullException.ThrowIfNull(risk);
        ArgumentNullException.ThrowIfNull(execution);
        if (targets.Count != instruments.Count)
        {
            throw new ArgumentException("One target per instrument.", nameof(targets));
        }

        RiskLimits limits = risk.Limits;
        CultureInfo c = CultureInfo.InvariantCulture;
        decimal sizing = limits.SizingValue(Math.Max(0m, account.AccountValue));
        decimal investable = sizing * (1m - (decimal)execution.CashBuffer);
        decimal perOrder = Math.Min(limits.MaxOrderValueSek, limits.MaxOrderValuePctOfAccount * sizing);
        OpenOrderView[] workingBuys = [.. openOrders.Where(o => o.Side == OrderSide.Buy)];
        decimal WorkingBuyValue(OrderbookId? id) => workingBuys.Where(o => id is null || o.OrderbookId == id).Sum(o => o.RemainingValueSek);
        decimal grossRoom = limits.MaxGrossExposurePct * sizing - account.PositionValues.Values.Sum() - WorkingBuyValue(null);
        decimal offset = (decimal)execution.LimitOffsetBps / 10_000m;
        var intents = new List<OrderIntent>();
        var notes = new List<string>();
        if (limits.Capped(account.AccountValue))
        {
            notes.Add(string.Create(c, $"sized on the {limits.MaxAccountValueSek:N0} SEK account cap, not the account's {account.AccountValue:N0} SEK"));
        }

        for (int i = 0; i < targets.Count; i++)
        {
            InstrumentSpec spec = instruments[i];
            double w = targets[i];
            if (double.IsNaN(w))
            {
                notes.Add($"{spec.Ticker}: hold (no target today)");
                continue;
            }

            if (fx.SekPerUnit(spec.Currency) is not { } rate)
            {
                notes.Add($"{spec.Ticker}: skipped, no {spec.Currency}/SEK rate");
                continue;
            }

            Quote? quote = quotes.Latest(spec.OrderbookId);
            if (risk.ReferencePrice(quote, nowUtc) is not { } reference)
            {
                notes.Add($"{spec.Ticker}: skipped, no fresh live price");
                continue;
            }

            decimal sekPrice = reference * rate;
            long lot = spec.LotSize;
            long current = account.Positions.GetValueOrDefault(spec.OrderbookId);
            long target = (long)decimal.Floor((decimal)w * investable / sekPrice / lot) * lot;
            long delta = target - current;
            if (delta == 0)
            {
                notes.Add(string.Create(c, $"{spec.Ticker}: at target ({current})"));
                continue;
            }

            decimal value = Math.Abs(delta) * sekPrice;
            bool entryOrExit = target == 0 || current == 0;
            if ((!entryOrExit && value < (decimal)execution.RebalanceBand * Math.Max(target, current) * sekPrice)
                || (target != 0 && value < execution.MinTradeValue))
            {
                notes.Add(string.Create(c, $"{spec.Ticker}: {delta:+#;-#} inside the no-trade band"));
                continue;
            }

            OrderSide side = delta > 0 ? OrderSide.Buy : OrderSide.Sell;
            decimal limit = TowardTheMarket(spec, reference, reference * (side == OrderSide.Buy ? 1 + offset : 1 - offset), side, risk.Limits.PriceCollarPct);
            decimal cap = perOrder;
            string clippedBy = "R6 order value";
            if (side == OrderSide.Buy)
            {
                decimal headroom = limits.MaxPositionPctOfAccount * sizing - account.PositionValues.GetValueOrDefault(spec.OrderbookId) - WorkingBuyValue(spec.OrderbookId);
                if (headroom < cap)
                {
                    cap = headroom;
                    clippedBy = "R7 position";
                }

                if (grossRoom < cap)
                {
                    cap = grossRoom;
                    clippedBy = "R8 gross exposure";
                }
            }

            long wanted = Math.Abs(delta);
            long allowed = cap <= 0 ? 0 : (long)decimal.Floor(cap / (limit * rate) / lot) * lot;
            long quantity = Math.Min(wanted, allowed);
            if (quantity <= 0)
            {
                notes.Add(string.Create(c, $"{spec.Ticker}: {side} {wanted} wanted, but {clippedBy} leaves no room"));
                continue;
            }

            string clip = quantity < wanted ? string.Create(c, $"; clipped from {wanted} by {clippedBy}") : string.Empty;
            string reason = string.Create(c, $"{strategyId}: target {w:P1} = {target} sh, holding {current}{clip}");
            intents.Add(new OrderIntent(spec.OrderbookId, spec.Ticker, side, quantity, limit, reason, reference, nowUtc, strategyId));
            if (side == OrderSide.Buy)
            {
                grossRoom -= quantity * limit * rate;
            }

            notes.Add(string.Create(c, $"{spec.Ticker}: {side} {quantity} @ ~{limit:0.###} ({reason})"));
            if (RestsInsideTheSpread(spec, quote, limit, side) is { } rests)
            {
                notes.Add(rests);
            }
        }

        return new PlanResult(intents, notes);
    }

    /// <summary>
    /// Plan 18: after the passive rounding the gateway applies, a buy's limit is still at least one price step above the
    /// reference and a sell's one below. On a share whose step is large against its price (a share under 1 SEK), the
    /// 0.5 % offset would otherwise round away and leave the limit at the last price. A step that would take the limit
    /// outside R5's <paramref name="collar"/> is not taken: a resting order is better than a rejected one.
    /// </summary>
    internal static decimal TowardTheMarket(InstrumentSpec spec, decimal reference, decimal limit, OrderSide side, decimal collar)
    {
        try
        {
            decimal rounded = spec.TickSizes.RoundForOrder(limit, side);
            if (side == OrderSide.Buy ? rounded > reference : rounded < reference)
            {
                return limit;
            }

            decimal step = spec.TickSizes.TickAt(reference);
            decimal stepped = side == OrderSide.Buy
                ? spec.TickSizes.Round(reference, TickRounding.Down) + step
                : spec.TickSizes.Round(reference, TickRounding.Up) - step;
            return Math.Abs(stepped - reference) <= collar * reference ? stepped : limit;
        }
        catch (ArgumentOutOfRangeException)
        {
            return limit; // outside the tick table: order preparation refuses it, as before
        }
    }

    /// <summary>
    /// Plan 18: "FASTAT: the buy limit 0.642 is below the ask 0.645 (spread 0.94 %): it rests until a trade prints
    /// through it." A limit inside the spread is the strategy's choice (the backtest assumes it), but the owner should see
    /// why an order waits.
    /// </summary>
    private static string? RestsInsideTheSpread(InstrumentSpec spec, Quote? quote, decimal limit, OrderSide side)
    {
        if (quote is not { Bid: { } bid, Ask: { } ask } || bid <= 0 || ask < bid)
        {
            return null;
        }

        decimal shown;
        try
        {
            shown = spec.TickSizes.RoundForOrder(limit, side);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        decimal spread = (ask - bid) / ((ask + bid) / 2);
        CultureInfo c = CultureInfo.InvariantCulture;
        return side == OrderSide.Buy && shown < ask
            ? string.Create(c, $"{spec.Ticker}: the buy limit {shown:0.####} is below the ask {ask:0.####} (spread {spread:P2}): it rests until a trade prints through it")
            : side == OrderSide.Sell && shown > bid
                ? string.Create(c, $"{spec.Ticker}: the sell limit {shown:0.####} is above the bid {bid:0.####} (spread {spread:P2}): it rests until a trade prints through it")
                : null;
    }
}
