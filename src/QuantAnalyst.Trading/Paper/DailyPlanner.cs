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
/// <item>orders are clipped to what R6 (value per order) and R7 (position size) allow, so the plan does not produce
/// orders the risk engine would reject; the target is then reached over several days.</item>
/// </list>
/// </summary>
public static class DailyPlanner
{
    public static PlanResult Plan(
        IReadOnlyList<double> targets,
        IReadOnlyList<InstrumentSpec> instruments,
        AccountSnapshot account,
        IQuoteSource quotes,
        PreTradeRiskEngine risk,
        ExecutionOptions execution,
        string strategyId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(quotes);
        ArgumentNullException.ThrowIfNull(risk);
        ArgumentNullException.ThrowIfNull(execution);
        if (targets.Count != instruments.Count)
        {
            throw new ArgumentException("One target per instrument.", nameof(targets));
        }

        RiskLimits limits = risk.Limits;
        CultureInfo c = CultureInfo.InvariantCulture;
        decimal investable = Math.Max(0m, account.AccountValue) * (1m - (decimal)execution.CashBuffer);
        decimal perOrder = Math.Min(limits.MaxOrderValueSek, limits.MaxOrderValuePctOfAccount * account.AccountValue);
        decimal offset = (decimal)execution.LimitOffsetBps / 10_000m;
        var intents = new List<OrderIntent>();
        var notes = new List<string>();

        for (int i = 0; i < targets.Count; i++)
        {
            InstrumentSpec spec = instruments[i];
            double w = targets[i];
            if (double.IsNaN(w))
            {
                notes.Add($"{spec.Ticker}: hold (no target today)");
                continue;
            }

            Quote? quote = quotes.Latest(spec.OrderbookId);
            if (risk.ReferencePrice(quote, nowUtc) is not { } reference)
            {
                notes.Add($"{spec.Ticker}: skipped, no fresh live price");
                continue;
            }

            long lot = spec.LotSize;
            long current = account.Positions.GetValueOrDefault(spec.OrderbookId);
            long target = (long)decimal.Floor((decimal)w * investable / reference / lot) * lot;
            long delta = target - current;
            if (delta == 0)
            {
                notes.Add(string.Create(c, $"{spec.Ticker}: at target ({current})"));
                continue;
            }

            decimal value = Math.Abs(delta) * reference;
            bool entryOrExit = target == 0 || current == 0;
            if ((!entryOrExit && value < (decimal)execution.RebalanceBand * Math.Max(target, current) * reference)
                || (target != 0 && value < execution.MinTradeValue))
            {
                notes.Add(string.Create(c, $"{spec.Ticker}: {delta:+#;-#} inside the no-trade band"));
                continue;
            }

            OrderSide side = delta > 0 ? OrderSide.Buy : OrderSide.Sell;
            decimal limit = reference * (side == OrderSide.Buy ? 1 + offset : 1 - offset);
            decimal cap = perOrder;
            string? clippedBy = null;
            if (side == OrderSide.Buy)
            {
                decimal headroom = limits.MaxPositionPctOfAccount * account.AccountValue - account.PositionValues.GetValueOrDefault(spec.OrderbookId);
                if (headroom < cap)
                {
                    cap = headroom;
                    clippedBy = "R7 position";
                }
                else
                {
                    clippedBy = "R6 order value";
                }
            }
            else
            {
                clippedBy = "R6 order value";
            }

            long wanted = Math.Abs(delta);
            long allowed = cap <= 0 ? 0 : (long)decimal.Floor(cap / limit / lot) * lot;
            long quantity = Math.Min(wanted, allowed);
            if (quantity <= 0)
            {
                notes.Add(string.Create(c, $"{spec.Ticker}: {side} {wanted} wanted, but {clippedBy} leaves no room"));
                continue;
            }

            string clip = quantity < wanted ? string.Create(c, $"; clipped from {wanted} by {clippedBy}") : string.Empty;
            string reason = string.Create(c, $"{strategyId}: target {w:P1} = {target} sh, holding {current}{clip}");
            intents.Add(new OrderIntent(spec.OrderbookId, spec.Ticker, side, quantity, limit, reason, reference, nowUtc, strategyId));
            notes.Add(string.Create(c, $"{spec.Ticker}: {side} {quantity} @ ~{limit:0.###} ({reason})"));
        }

        return new PlanResult(intents, notes);
    }
}
