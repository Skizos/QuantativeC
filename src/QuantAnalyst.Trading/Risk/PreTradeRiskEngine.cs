using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Model;

namespace QuantAnalyst.Trading.Risk;

/// <summary>One check's outcome, with what was observed against the limit (shown on the Confirm card and audited).</summary>
public sealed record RiskCheckResult(string Id, string Name, bool Passed, string Observed, string Limit, string Message);

/// <summary>Every check's result for one order. The order may proceed only when <see cref="Passed"/>.</summary>
public sealed record RiskReport(IReadOnlyList<RiskCheckResult> Checks)
{
    public bool Passed => Checks.All(c => c.Passed);

    public IEnumerable<RiskCheckResult> Failures => Checks.Where(c => !c.Passed);

    public RiskCheckResult this[string id] => Checks.Single(c => c.Id == id);
}

/// <summary>
/// The pre-trade risk engine (ADR 0003 §4): checks R1–R21 on a tick-rounded order. All checks are always evaluated
/// (not fail-fast) so the audit record and the Confirm card show the complete picture. Pure: no I/O, no clock of its
/// own; everything comes from the <see cref="RiskContext"/>.
/// </summary>
public sealed class PreTradeRiskEngine(RiskLimits limits)
{
    public const int CheckCount = 21;

    public RiskLimits Limits { get; } = limits ?? throw new ArgumentNullException(nameof(limits));

    public RiskReport Evaluate(PreparedOrder order, RiskContext ctx)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(ctx);
        return new RiskReport(
        [
            R1(ctx), R2(order, ctx), R3(order), R4(order, ctx), R5(order, ctx), R6(order, ctx), R7(order, ctx),
            R8(order, ctx), R9(order, ctx), R10(ctx), R11(ctx), R12(ctx), R13(order, ctx), R14(order, ctx),
            R15(ctx), R16(ctx), R17(ctx), R18(order, ctx), R19(ctx), R20(ctx), R21(ctx),
        ]);
    }

    /// <summary>The reference price for the collar (R5): the last trade if it is fresh, else the mid.</summary>
    public decimal? ReferencePrice(Quote? quote, DateTimeOffset nowUtc) =>
        quote is null ? null
        : quote.Last is { } last && quote.TimeOfLastUtc is { } t && nowUtc - t <= Limits.MaxQuoteAge ? last
        : quote is { Bid: { } bid, Ask: { } ask } ? (bid + ask) / 2
        : null;

    private static RiskCheckResult R1(RiskContext c) =>
        Check("R1", "account allowlist", c.AllowedAccountIds.Contains(c.Account.Value), c.Account.Masked, $"{c.AllowedAccountIds.Count} allowed account(s)",
            "the account is not in the allowlist");

    private static RiskCheckResult R2(PreparedOrder o, RiskContext c) =>
        Check("R2", "instrument allowlist", c.Universe.Contains(o.OrderbookId), $"{o.Intent.Ticker} ({o.OrderbookId})", $"{c.Universe.Entries.Count} instrument(s)",
            "the instrument is not in config/universe.json");

    private static RiskCheckResult R3(PreparedOrder o)
    {
        bool ok = o.Intent.Type == IntentOrderType.Limit && o.LimitPrice is > 0 && o.Intent.Condition == "NORMAL";
        return Check("R3", "order type", ok, $"{o.Intent.Type}, condition {o.Intent.Condition}, limit {Price(o.LimitPrice)}", "LIMIT, NORMAL",
            "only limit orders with condition NORMAL are allowed");
    }

    private static RiskCheckResult R4(PreparedOrder o, RiskContext c)
    {
        if (o.Side == OrderSide.Buy)
        {
            return Pass("R4", "no short selling", "buy", "sells <= position");
        }

        long held = c.Positions.GetValueOrDefault(o.OrderbookId);
        long selling = c.OpenOrders.Where(w => w.OrderbookId == o.OrderbookId && w.Side == OrderSide.Sell).Sum(w => w.RemainingVolume);
        return Check("R4", "no short selling", o.Volume + selling <= held, $"sell {o.Volume} + working sells {selling}", $"position {held}",
            "the sell is larger than the position (no short selling on ISK)");
    }

    private RiskCheckResult R5(PreparedOrder o, RiskContext c)
    {
        decimal? reference = ReferencePrice(c.Quote, c.NowUtc);
        if (reference is not > 0 || o.LimitPrice is not { } limit)
        {
            return Fail("R5", "price collar", $"limit {Price(o.LimitPrice)}, reference {Price(reference)}", $"±{Pct(Limits.PriceCollarPct)}",
                "no reference price (no fresh last trade and no bid/ask)");
        }

        decimal distance = Math.Abs(limit - reference.Value) / reference.Value;
        return Check("R5", "price collar", distance <= Limits.PriceCollarPct, $"limit {Price(limit)} is {Pct(distance)} from {Price(reference)}", $"±{Pct(Limits.PriceCollarPct)}",
            "the limit is too far from the reference price");
    }

    private RiskCheckResult R6(PreparedOrder o, RiskContext c)
    {
        decimal cap = Math.Min(Limits.MaxOrderValueSek, Limits.MaxOrderValuePctOfAccount * Limits.SizingValue(c.AccountValue));
        return Check("R6", "max order value", o.Value <= cap, Sek(o.Value),
            $"{Sek(cap)} = min({Sek(Limits.MaxOrderValueSek)}, {Pct(Limits.MaxOrderValuePctOfAccount)} of {Sek(Limits.SizingValue(c.AccountValue))}){CapNote(c.AccountValue)}",
            "the order is too large");
    }

    private RiskCheckResult R7(PreparedOrder o, RiskContext c)
    {
        if (o.Side == OrderSide.Sell)
        {
            return Pass("R7", "max position per instrument", "sell (reduces the position)", $"{Pct(Limits.MaxPositionPctOfAccount)} of account value");
        }

        decimal after = c.PositionValues.GetValueOrDefault(o.OrderbookId) + WorkingBuys(c, o.OrderbookId) + o.Value;
        decimal cap = Limits.MaxPositionPctOfAccount * Limits.SizingValue(c.AccountValue);
        return Check("R7", "max position per instrument", after <= cap, $"{Sek(after)} after the order (incl. working buys)", Sek(cap) + CapNote(c.AccountValue),
            "the position would be too large a share of the account");
    }

    private RiskCheckResult R8(PreparedOrder o, RiskContext c)
    {
        decimal after = c.PositionValues.Values.Sum() + c.OpenOrders.Where(w => w.Side == OrderSide.Buy).Sum(w => w.RemainingVolume * w.LimitPrice)
            + (o.Side == OrderSide.Buy ? o.Value : 0m);
        decimal cap = Limits.MaxGrossExposurePct * Limits.SizingValue(c.AccountValue);
        return Check("R8", "max gross exposure", after <= cap, $"{Sek(after)} after the order", Sek(cap) + CapNote(c.AccountValue),
            "gross exposure would exceed the limit (no leverage)");
    }

    private static RiskCheckResult R9(PreparedOrder o, RiskContext c)
    {
        if (o.Side == OrderSide.Sell)
        {
            return Pass("R9", "available cash", "sell", "buys only");
        }

        decimal needed = o.Value + c.EstimatedFees;
        return Check("R9", "available cash", needed <= c.AvailableCash, $"{Sek(needed)} incl. fees {Sek(c.EstimatedFees)}", Sek(c.AvailableCash),
            "not enough cash for the order and its fees");
    }

    private RiskCheckResult R10(RiskContext c) =>
        Check("R10", "max orders per day", c.OrdersPlacedToday + 1 <= Limits.MaxOrdersPerDay, $"{c.OrdersPlacedToday + 1} with this one", $"{Limits.MaxOrdersPerDay}",
            "the daily order limit is reached");

    private RiskCheckResult R11(RiskContext c)
    {
        int lastMinute = c.RecentActionsUtc.Count(t => c.NowUtc - t < TimeSpan.FromMinutes(1) && t <= c.NowUtc);
        return Check("R11", "max order actions per minute", lastMinute + 1 <= Limits.MaxActionsPerMinute, $"{lastMinute + 1} in the last minute with this one",
            $"{Limits.MaxActionsPerMinute}", "too many order actions in the last minute");
    }

    private RiskCheckResult R12(RiskContext c)
    {
        if (c.LastActionOnInstrumentUtc is not { } last)
        {
            return Pass("R12", "min interval same instrument", "no earlier action", Seconds(Limits.MinIntervalSameInstrument));
        }

        TimeSpan since = c.NowUtc - last;
        return Check("R12", "min interval same instrument", since >= Limits.MinIntervalSameInstrument, $"{Seconds(since)} since the last action",
            Seconds(Limits.MinIntervalSameInstrument), "too soon after the last action on this instrument");
    }

    private static RiskCheckResult R13(PreparedOrder o, RiskContext c)
    {
        int opposite = c.OpenOrders.Count(w => w.OrderbookId == o.OrderbookId && w.Side != o.Side);
        return Check("R13", "no opposite working order", opposite == 0, $"{opposite} opposite working order(s)", "0",
            "an order on the other side is working in this instrument (self-cross risk)");
    }

    private RiskCheckResult R14(PreparedOrder o, RiskContext c)
    {
        int same = c.RecentIntents.Count(i => i.OrderbookId == o.OrderbookId && i.Side == o.Side && i.Volume == o.Volume && i.LimitPrice == o.LimitPrice
            && c.NowUtc - i.AtUtc <= Limits.DuplicateIntentWindow);
        return Check("R14", "duplicate intent", same == 0, $"{same} identical intent(s) in the window", $"0 within {Seconds(Limits.DuplicateIntentWindow)}",
            "an identical order was just requested");
    }

    private RiskCheckResult R15(RiskContext c)
    {
        Quote? q = c.Quote;
        if (q is null)
        {
            return Fail("R15", "fresh market data", "no quote", $"<= {Seconds(Limits.MaxQuoteAge)}", "no market data for the instrument");
        }

        TimeSpan? age = q.AsOfUtc is { } asOf ? c.NowUtc - asOf : null;
        bool ok = !q.IsStale && age is { } a && a <= Limits.MaxQuoteAge;
        return Check("R15", "fresh market data", ok, q.IsStale ? $"stale: {q.StaleReason}" : $"age {(age is { } x ? Seconds(x) : "unknown")}",
            $"<= {Seconds(Limits.MaxQuoteAge)}, stream connected", "market data is stale or the depth stream is down");
    }

    private RiskCheckResult R16(RiskContext c)
    {
        TimeOnly local = TimeOnly.FromDateTime(MarketTime.ToStockholm(c.NowUtc).DateTime);
        if (c.Today is not { IsTradingDay: true } day)
        {
            return Fail("R16", "trading window", c.Today is null ? "date not in the calendar" : $"{c.Today.Kind} ({c.Today.Name ?? "no session"})",
                "a trading day", "the market is closed today");
        }

        TimeOnly close = day.Kind == TradingDayKind.Half ? Limits.HalfDayWindowClose : Limits.WindowClose;
        bool ok = local >= Limits.WindowOpen && local < close;
        return Check("R16", "trading window", ok, $"{local:HH\\:mm\\:ss} Stockholm ({day.Kind} day)", $"{Limits.WindowOpen:HH\\:mm}–{close:HH\\:mm}",
            "outside the continuous-trading window (auctions and the first minutes are avoided)");
    }

    private static RiskCheckResult R17(RiskContext c) =>
        Check("R17", "no halt", c.Halts.Count == 0, c.Halts.Count == 0 ? "none" : string.Join(", ", c.Halts.Select(h => $"{h.Reason}: {h.Detail}")), "none",
            "trading is halted");

    private static RiskCheckResult R18(PreparedOrder o, RiskContext c)
    {
        int unknown = c.OpenOrders.Count(w => w.OrderbookId == o.OrderbookId && w.IsUnknown);
        return Check("R18", "no unknown orders", unknown == 0, $"{unknown} unknown order(s) in the instrument", "0",
            "an order in this instrument has an unknown outcome until reconciliation resolves it");
    }

    private RiskCheckResult R19(RiskContext c)
    {
        if (c.StartOfDayValue <= 0)
        {
            return Fail("R19", "daily loss stop", "start-of-day value unknown", $"> -{Pct(Limits.DailyLossStopPct)}", "the day's starting value is unknown");
        }

        decimal change = (c.AccountValue - c.StartOfDayValue) / c.StartOfDayValue;
        bool hit = Limits.DailyLossStopHit(c.StartOfDayValue, c.AccountValue);
        return Limits.Capped(c.StartOfDayValue)
            ? Check("R19", "daily loss stop", !hit, $"{Pct(change)} today ({Sek(c.AccountValue - c.StartOfDayValue)})",
                $"a loss below {Sek(Limits.DailyLossLimitSek(c.StartOfDayValue))} = {Pct(Limits.DailyLossStopPct)} of {Sek(Limits.MaxAccountValueSek)}, the account cap",
                "the daily loss stop is hit (the kill switch fires)")
            : Check("R19", "daily loss stop", !hit, $"{Pct(change)} today", $"> -{Pct(Limits.DailyLossStopPct)}",
                "the daily loss stop is hit (the kill switch fires)");
    }

    /// <summary>Said on a limit that the account cap lowered, so the card shows why it is smaller than the account suggests.</summary>
    private string CapNote(decimal accountValue) =>
        Limits.Capped(accountValue) ? $" (sized on the {Sek(Limits.MaxAccountValueSek)} account cap, not the account's {Sek(accountValue)})" : string.Empty;

    private static RiskCheckResult R20(RiskContext c)
    {
        string observed = $"courtage {(c.Verified.Courtage ? "verified" : "UNVERIFIED")}, calendar {(c.Verified.Calendar ? "verified" : "UNVERIFIED")}, tick table {(c.Verified.TickTable ? "verified" : "UNVERIFIED")}";
        return c.IsLive
            ? Check("R20", "verified constants", c.Verified.All, observed, "all verified", "live trading needs verified courtage, calendar and tick tables")
            : Pass("R20", "verified constants", observed, "required in Confirm/Auto only");
    }

    private static RiskCheckResult R21(RiskContext c)
    {
        if (!c.IsLive)
        {
            return Pass("R21", "broker preflight", c.Preflight is null ? "not asked (simulated mode)" : $"logged only: {(c.Preflight.AllValid ? "valid" : string.Join(", ", c.Preflight.Failures))}",
                "required in Confirm/Auto only");
        }

        return c.Preflight is null
            ? Fail("R21", "broker preflight", "not run", "Avanza validate: all valid", "Avanza's order validation was not run")
            : Check("R21", "broker preflight", c.Preflight.AllValid, c.Preflight.AllValid ? "all valid" : string.Join(", ", c.Preflight.Failures), "all valid",
                "Avanza's own order validation failed");
    }

    private static decimal WorkingBuys(RiskContext c, OrderbookId id) =>
        c.OpenOrders.Where(w => w.OrderbookId == id && w.Side == OrderSide.Buy).Sum(w => w.RemainingVolume * w.LimitPrice);

    private static RiskCheckResult Check(string id, string name, bool passed, string observed, string limit, string failMessage) =>
        new(id, name, passed, observed, limit, passed ? "ok" : failMessage);

    private static RiskCheckResult Pass(string id, string name, string observed, string limit) => new(id, name, true, observed, limit, "ok");

    private static RiskCheckResult Fail(string id, string name, string observed, string limit, string message) => new(id, name, false, observed, limit, message);

    private static string Sek(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture) + " SEK";

    private static string Price(decimal? p) => p is { } v ? v.ToString("0.####", CultureInfo.InvariantCulture) : "none";

    private static string Pct(decimal f) => (f * 100).ToString("0.##", CultureInfo.InvariantCulture) + " %";

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
}
