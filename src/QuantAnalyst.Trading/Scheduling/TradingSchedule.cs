using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Scheduling;

/// <summary>Where a moment falls in the trading day.</summary>
public enum SessionPhase
{
    /// <summary>Weekend or exchange holiday.</summary>
    NoSession,

    /// <summary>A trading day before the order window opens.</summary>
    BeforeWindow,

    /// <summary>Orders may be sent (R16).</summary>
    Window,

    /// <summary>The window has closed; the exchange is still open.</summary>
    AfterWindow,

    /// <summary>After the close.</summary>
    Closed,
}

/// <summary>One trading day's times, in UTC (converted from Stockholm wall-clock times, so DST is handled).</summary>
/// <param name="DecisionUtc">When Paper's daily strategies decide (on bars through yesterday) and place their orders.</param>
/// <param name="CloseUtc">When day orders end.</param>
public sealed record SessionPlan(
    DateOnly Date,
    TradingDay Day,
    DateTimeOffset OpenUtc,
    DateTimeOffset WindowOpenUtc,
    DateTimeOffset DecisionUtc,
    DateTimeOffset WindowCloseUtc,
    DateTimeOffset CloseUtc);

/// <summary>
/// The trading day from the XSTO calendar and the R16 window (09:05–17:20, half days to 12:50): when to decide,
/// when orders may go, when day orders end. Pure: the runner asks it what to do next.
/// </summary>
public sealed class TradingSchedule
{
    private readonly MarketCalendar _calendar;
    private readonly RiskLimits _limits;

    public TradingSchedule(MarketCalendar calendar, RiskLimits limits, TimeOnly decisionTime)
    {
        _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        if (decisionTime < limits.WindowOpen || decisionTime >= limits.HalfDayWindowClose)
        {
            throw new TradingConfigException(
                $"The decision time {decisionTime:HH\\:mm} must be inside the order window on every trading day: from {limits.WindowOpen:HH\\:mm} to before {limits.HalfDayWindowClose:HH\\:mm} (the half-day close).");
        }

        DecisionTime = decisionTime;
    }

    public TimeOnly DecisionTime { get; }

    /// <summary>False until the owner has verified the calendar year; Paper runs anyway (with a warning), live does not (R20).</summary>
    public bool IsVerified(DateOnly date) => _calendar.GetYear(date.Year).VerifiedOn is not null;

    /// <summary>The plan for a date, or null when there is no session (weekend, holiday).</summary>
    public SessionPlan? Plan(DateOnly date)
    {
        TradingDay day = _calendar.Classify(date);
        if (!day.IsTradingDay)
        {
            return null;
        }

        TimeOnly windowClose = day.Kind == TradingDayKind.Half ? _limits.HalfDayWindowClose : _limits.WindowClose;
        if (windowClose > day.Close!.Value)
        {
            windowClose = day.Close.Value;
        }

        return new SessionPlan(
            date,
            day,
            Utc(date, day.Open!.Value),
            Utc(date, _limits.WindowOpen),
            Utc(date, DecisionTime),
            Utc(date, windowClose),
            Utc(date, day.Close.Value));
    }

    public SessionPhase PhaseAt(DateTimeOffset utc)
    {
        SessionPlan? plan = Plan(OrderGateway.StockholmDate(utc));
        return plan is null ? SessionPhase.NoSession
            : utc < plan.WindowOpenUtc ? SessionPhase.BeforeWindow
            : utc < plan.WindowCloseUtc ? SessionPhase.Window
            : utc < plan.CloseUtc ? SessionPhase.AfterWindow
            : SessionPhase.Closed;
    }

    /// <summary>The next session whose decision time is at or after <paramref name="utc"/> (today's if not yet passed).</summary>
    public SessionPlan NextDecision(DateTimeOffset utc)
    {
        DateOnly date = OrderGateway.StockholmDate(utc);
        for (int i = 0; i < 400; i++, date = date.AddDays(1))
        {
            if (Plan(date) is { } plan && plan.DecisionUtc >= utc)
            {
                return plan;
            }
        }

        throw new InvalidOperationException("No trading day within 400 days; the calendar is wrong.");
    }

    private static DateTimeOffset Utc(DateOnly date, TimeOnly time) =>
        MarketTime.TryStockholmToUtc(date.ToDateTime(time), out DateTimeOffset utc)
            ? utc
            : throw new InvalidOperationException($"{date:yyyy-MM-dd} {time:HH\\:mm} does not exist in Stockholm time (DST change).");
}
