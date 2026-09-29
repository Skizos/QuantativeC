using System.Globalization;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core;
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
            decimal limit = reference * (side == OrderSide.Buy ? 1 + offset : 1 - offset);
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
        }

        return new PlanResult(intents, notes);
    }
}
