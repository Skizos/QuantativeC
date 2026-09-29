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

/// <summary>One trading day's times, in UTC (converted from the market's wall-clock times, so DST is handled).</summary>
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
/// One market's trading day: when to decide, when orders may go (R16), when day orders end. Pure: the runner asks it
/// what to do next.
/// <list type="bullet">
/// <item>Nasdaq Stockholm: the XSTO calendar and the limits' window (09:05–17:20, half days to 12:50) and decision time.</item>
/// <item>Another market (ADR 0005): its own calendar in its own time, with the same distances from its session as
/// Stockholm's: the window from 5 minutes after its open to 10 minutes before its close (early closes too), and the
/// decision as long after its open as the decision time is after Stockholm's (09:10 → 09:40 in New York).</item>
/// </list>
/// </summary>
public sealed class TradingSchedule
{
    private readonly MarketCalendar _calendar;
    private readonly RiskLimits _limits;
    private readonly MarketCalendar? _stockholm;

    public TradingSchedule(MarketCalendar calendar, RiskLimits limits, TimeOnly decisionTime)
        : this(calendar, limits, decisionTime, stockholm: null)
    {
    }

    /// <param name="stockholm">
    /// For another market: the Stockholm calendar, whose session the limits' window and <paramref name="decisionTime"/> are
    /// measured against. Null for Stockholm itself.
    /// </param>
    public TradingSchedule(MarketCalendar calendar, RiskLimits limits, TimeOnly decisionTime, MarketCalendar? stockholm)
    {
        _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _stockholm = stockholm;
        if (decisionTime < limits.WindowOpen || decisionTime >= limits.HalfDayWindowClose)
        {
            throw new TradingConfigException(
                $"The decision time {decisionTime:HH\\:mm} must be inside the order window on every trading day: from {limits.WindowOpen:HH\\:mm} to before {limits.HalfDayWindowClose:HH\\:mm} (the half-day close).");
        }

        DecisionTime = decisionTime;
    }

    /// <summary>Gets the decision time in Stockholm terms (09:10); another market decides as long after its own open.</summary>
    public TimeOnly DecisionTime { get; }

    /// <summary>Gets the market's calendar (its dates and times are the market's local ones).</summary>
    public MarketCalendar Calendar => _calendar;

    public string Mic => _calendar.Mic;

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

        TimeOnly windowOpen, windowClose, decision;
        if (_stockholm is null)
        {
            windowOpen = _limits.WindowOpen;
            windowClose = day.Kind == TradingDayKind.Half ? _limits.HalfDayWindowClose : _limits.WindowClose;
            decision = DecisionTime;
        }
        else
        {
            CalendarYear s = _stockholm.GetYear(date.Year);
            windowOpen = day.Open!.Value.Add(_limits.WindowOpen - s.RegularOpen);
            windowClose = day.Close!.Value.Add(-(day.Kind == TradingDayKind.Half ? s.HalfDayClose - _limits.HalfDayWindowClose : s.RegularClose - _limits.WindowClose));
            decision = day.Open.Value.Add(DecisionTime - s.RegularOpen);
            if (decision >= windowClose)
            {
                decision = windowOpen;
            }
        }

        if (windowClose > day.Close!.Value)
        {
            windowClose = day.Close.Value;
        }

        return new SessionPlan(
            date,
            day,
            _calendar.ToUtc(date, day.Open!.Value),
            _calendar.ToUtc(date, windowOpen),
            _calendar.ToUtc(date, decision),
            _calendar.ToUtc(date, windowClose),
            _calendar.ToUtc(date, day.Close.Value));
    }

    public SessionPhase PhaseAt(DateTimeOffset utc)
    {
        SessionPlan? plan = Plan(_calendar.LocalDate(utc));
        return plan is null ? SessionPhase.NoSession
            : utc < plan.WindowOpenUtc ? SessionPhase.BeforeWindow
            : utc < plan.WindowCloseUtc ? SessionPhase.Window
            : utc < plan.CloseUtc ? SessionPhase.AfterWindow
            : SessionPhase.Closed;
    }

    /// <summary>The next session whose decision time is at or after <paramref name="utc"/> (today's if not yet passed).</summary>
    public SessionPlan NextDecision(DateTimeOffset utc)
    {
        DateOnly date = _calendar.LocalDate(utc);
        for (int i = 0; i < 400; i++, date = date.AddDays(1))
        {
            if (Plan(date) is { } plan && plan.DecisionUtc >= utc)
            {
                return plan;
            }
        }

        throw new InvalidOperationException("No trading day within 400 days; the calendar is wrong.");
    }
}
