using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Observation;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Reconciliation;
using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Trading.Paper;

public sealed record PaperSessionSummary(
    int Decisions,
    int Submitted,
    int Accepted,
    int RiskRejected,
    int Filled,
    decimal Cash,
    decimal AccountValue,
    decimal StartOfDayValue,
    decimal FeesPaid,
    bool Killed,
    bool ReconciliationClean);

/// <summary>
/// One market of a Paper session (ADR 0005): its schedule (calendar, decision time, window, close), the decision for
/// its shares, and which instruments are its own (whose day orders end at its close).
/// </summary>
/// <param name="Decide">Intents for this market's shares (bars through yesterday, live prices).</param>
/// <param name="Trades">True for an instrument of this market.</param>
public sealed record SessionMarket(TradingSchedule Schedule, Func<CancellationToken, Task<PlanResult>> Decide, Func<InstrumentSpec, bool> Trades)
{
    public string Mic => Schedule.Mic;

    /// <summary>
    /// Gets whether every share of this market has a usable live price now (<see cref="PriceGate"/>): the decision waits
    /// for it, at most a minute. Null: no wait.
    /// </summary>
    public Func<DateTimeOffset, bool>? PricesReady { get; init; }
}

/// <summary>
/// One Paper trading session (ADR 0003, plan 06): a 1-second loop that
/// <list type="number">
/// <item>reconciles the OMS with the paper channel every 30 s, and at once when an Unknown order reaches 2 minutes;</item>
/// <item>ticks the kill switch (flag file, automatic triggers, cancels);</item>
/// <item>at the decision time of each trading day asks <c>decide</c> for intents (bars through yesterday, live prices);</item>
/// <item>submits them through <see cref="OrderGateway"/>, one at a time and paced (R11 allows 5 actions a minute);</item>
/// <item>at the close ends the day orders and reports the day.</item>
/// </list>
/// With shares on several markets (ADR 0005) each market decides, submits and ends its day orders on its own calendar
/// and clock (Stockholm 09:10, New York and Toronto 09:40 local); the day ends, with one report, at the last close.
/// On the way out it cancels everything still working through the gateway and ends the rest, so no paper order
/// outlives the session. <c>endOfDayReport</c> (the CLI's end-of-day report writer) runs at the close, and for a partial
/// day when the session stops earlier. Quotes reach the paper channel from the caller (<see cref="PaperOrderChannel.OnQuote"/>).
/// An <c>observer</c> (the Windows app) is told the account's value every few seconds and the day's decision; it can't
/// change anything (<see cref="GuardedObserver"/>).
/// </summary>
public sealed class PaperSession(
    OrderGateway gateway,
    PaperOrderChannel channel,
    PaperBook book,
    KillSwitch kill,
    Reconciler reconciler,
    HaltController halts,
    IReadOnlyList<SessionMarket> markets,
    AuditLog audit,
    TimeProvider time,
    TextWriter output,
    Func<DateOnly, string>? endOfDayReport = null,
    ISessionObserver? observer = null)
{
    /// <summary>How often an observer is told the account's value.</summary>
    public static readonly TimeSpan AccountEvery = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReconcileEvery = TimeSpan.FromSeconds(30);

    /// <summary>12 s apart keeps a burst of intents at 5 actions a minute (R11), with room for one cancel.</summary>
    public static readonly TimeSpan PaceBetweenOrders = TimeSpan.FromSeconds(13);

    private readonly MarketDay[] _markets = markets is { Count: > 0 }
        ? [.. markets.Select(m => new MarketDay(m))]
        : throw new ArgumentException("A session needs at least one market.", nameof(markets));

    private readonly List<(MarketDay Market, OrderIntent Intent)> _queue = [];
    private DateOnly? _reportedOn;
    private DateTimeOffset? _lastSubmit;
    private int _decisions;
    private int _submitted;
    private int _accepted;
    private int _riskRejected;
    private bool _lastClean = true;
    private readonly ISessionObserver? _observer = GuardedObserver.Wrap(observer);
    private DateTimeOffset? _lastAccountTick;

    /// <summary>A session on Nasdaq Stockholm alone (a Swedish-only list): one decision, one close.</summary>
    public PaperSession(
        OrderGateway gateway,
        PaperOrderChannel channel,
        PaperBook book,
        KillSwitch kill,
        Reconciler reconciler,
        HaltController halts,
        TradingSchedule schedule,
        AuditLog audit,
        TimeProvider time,
        Func<CancellationToken, Task<PlanResult>> decide,
        TextWriter output,
        Func<DateOnly, string>? endOfDayReport = null,
        ISessionObserver? observer = null)
        : this(gateway, channel, book, kill, reconciler, halts, [new SessionMarket(schedule, decide, _ => true)], audit, time, output, endOfDayReport, observer)
    {
    }

    public async Task<PaperSessionSummary> RunAsync(DateTimeOffset stopAtUtc, CancellationToken ct)
    {
        audit.Append("session-start", new { mode = "Paper", stopAtUtc, book.Costs, cash = book.Cash });
        TellAccount(force: true);
        try
        {
            while (!ct.IsCancellationRequested && time.GetUtcNow() < stopAtUtc)
            {
                await StepAsync(ct).ConfigureAwait(false);
                try
                {
                    await Task.Delay(Tick, time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _queue.Clear();
            await gateway.CancelAllAsync("session stopped", CancellationToken.None).ConfigureAwait(false);
            channel.EndOfDay("session stopped");
            await ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            DateOnly today = OrderGateway.StockholmDate(time.GetUtcNow());
            if (_reportedOn != today)
            {
                Report(today, partial: true);
            }

            TellAccount(force: true);
        }

        PaperSessionSummary summary = Summarize();
        audit.Append("session-end", summary);
        return summary;
    }

    /// <summary>One loop iteration (public for tests that step the session by hand).</summary>
    public async Task StepAsync(CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();
        bool unknownDue = gateway.Oms.Open.Any(o => o.State == OmsState.Unknown && now - o.CreatedUtc >= Reconciler.NotPlacedAfter
                                                        && (reconciler.LastRunUtc is not { } last || last < o.CreatedUtc + Reconciler.NotPlacedAfter));
        if (reconciler.LastRunUtc is not { } lastRun || now - lastRun >= ReconcileEvery || unknownDue)
        {
            await ReconcileAsync(ct).ConfigureAwait(false);
        }

        await kill.TickAsync(ct).ConfigureAwait(false);
        TellAccount(force: false);

        foreach (MarketDay m in _markets)
        {
            if (m.Today(now) is { } plan && m.DecidedOn != plan.Date && now >= plan.DecisionUtc && now < plan.WindowCloseUtc
                && m.Gate.Open(plan.Date, now, say => output.WriteLine($"{Local(now)} {Where(m)}{say}")))
            {
                m.DecidedOn = plan.Date;
                await DecideAsync(m, ct).ConfigureAwait(false);
            }
        }

        if (_lastSubmit is not { } s || now - s >= PaceBetweenOrders)
        {
            // The first queued intent whose market still takes orders; the rest go at their market's close.
            int next = _queue.FindIndex(q => q.Market.Today(now) is { } p && now < p.WindowCloseUtc);
            if (next >= 0)
            {
                OrderIntent intent = _queue[next].Intent;
                _queue.RemoveAt(next);
                await SubmitNextAsync(intent, ct).ConfigureAwait(false);
            }
        }

        foreach (MarketDay m in _markets)
        {
            if (m.Today(now) is { } plan && m.EndedOn != plan.Date && now >= plan.CloseUtc)
            {
                m.EndedOn = plan.Date;
                await CloseAsync(m, now, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A market's close: its queued intents are dropped and its day orders end. At the day's last close (every market
    /// trading today has closed) the day is booked and reported once.
    /// </summary>
    private async Task CloseAsync(MarketDay m, DateTimeOffset now, CancellationToken ct)
    {
        _queue.RemoveAll(q => q.Market == m);
        int ended = _markets.Length == 1 ? channel.EndOfDay("day order expired at the close") : channel.EndOfDay($"day order expired at the {m.Mic} close", m.Market.Trades);
        await ReconcileAsync(ct).ConfigureAwait(false);
        DateOnly today = OrderGateway.StockholmDate(now);
        bool last = _markets.All(x => x.Today(now) is not { } p || x.EndedOn == p.Date);
        if (!last)
        {
            audit.Append("market-close", new { date = today, market = m.Mic, ended });
            output.WriteLine($"{Local(now)} {m.Mic} close: {ended} order(s) expired.");
            return;
        }

        _reportedOn = today;
        PaperSessionSummary day = Summarize();
        audit.Append("end-of-day", new { date = today, ended, day });
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Local(now)} {Where(m)}close: {ended} order(s) expired. Value {day.AccountValue:N2} SEK ({Change(day):+0.00%;-0.00%} today), cash {day.Cash:N2}, fees {day.FeesPaid:N2}."));
        Report(today, partial: false);
    }

    private void Report(DateOnly date, bool partial)
    {
        if (endOfDayReport is null)
        {
            return;
        }

        try
        {
            output.WriteLine((partial ? "Report (partial day): " : "Report: ") + endOfDayReport(date));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // The audit log still has everything; 'qa report eod' can rebuild the report later.
            output.WriteLine($"The end-of-day report could not be written ({ex.Message}); rebuild it with 'qa report eod --date {date:yyyy-MM-dd}'.");
        }
    }

    private async Task DecideAsync(MarketDay m, CancellationToken ct)
    {
        _decisions++;
        if (kill.IsKilled || halts.IsHalted)
        {
            string why = string.Join(", ", halts.Active.Select(h => h.Reason));
            output.WriteLine($"{Local(time.GetUtcNow())} {Where(m)}decision skipped: trading is halted ({why}).");
            audit.Append("decision-skipped", new { market = m.Mic, halts = why });
            _observer?.Decision(new DecisionTick(time.GetUtcNow(), 0, [$"skipped: trading is halted ({why})"]));
            return;
        }

        PlanResult plan;
        try
        {
            plan = await m.Market.Decide(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Analytics.Backtesting.StrategyException)
        {
            output.WriteLine($"{Local(time.GetUtcNow())} {Where(m)}decision failed: {ex.Message}");
            audit.Append("decision-failed", new { market = m.Mic, reason = ex.Message });
            _observer?.Decision(new DecisionTick(time.GetUtcNow(), 0, ["failed: " + ex.Message]));
            return;
        }

        audit.Append("decision", new { market = m.Mic, intents = plan.Intents.Count, plan.Notes });
        _observer?.Decision(new DecisionTick(time.GetUtcNow(), plan.Intents.Count, plan.Notes));
        output.WriteLine($"{Local(time.GetUtcNow())} {Where(m)}decision: {plan.Intents.Count} order(s).");
        foreach (string note in plan.Notes)
        {
            output.WriteLine("  " + note);
        }

        foreach (OrderIntent intent in plan.Intents)
        {
            _queue.Add((m, intent));
        }
    }

    private async Task SubmitNextAsync(OrderIntent queued, CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();
        _lastSubmit = now;
        OrderIntent intent = queued with { DecisionTimeUtc = now };
        _submitted++;
        SubmitResult r = await gateway.SubmitAsync(intent, ct).ConfigureAwait(false);
        switch (r.Status)
        {
            case SubmitStatus.Accepted:
                _accepted++;
                break;
            case SubmitStatus.RiskRejected:
                _riskRejected++;
                break;
        }

        string detail = r.Order is { } o
            ? string.Create(CultureInfo.InvariantCulture, $"{o.State}, filled {o.FilledVolume}/{o.Volume}{(o.AverageFillPrice is { } p ? $" @ {p:0.####}" : string.Empty)}")
            : r.Message;
        output.WriteLine($"{Local(now)} {intent.Side} {intent.Quantity} {intent.Ticker}: {r.Status} ({detail})");
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        ReconciliationReport report = await reconciler.RunAsync(channel, ct).ConfigureAwait(false);
        if (!report.Clean && _lastClean)
        {
            output.WriteLine($"{Local(report.AtUtc)} RECONCILIATION MISMATCH: {string.Join("; ", report.Mismatches)}");
        }

        _lastClean = report.Clean;
    }

    private PaperSessionSummary Summarize()
    {
        AccountSnapshot a = book.Snapshot();
        int filled = gateway.Oms.All.Count(o => o.FilledVolume > 0);
        return new PaperSessionSummary(_decisions, _submitted, _accepted, _riskRejected, filled, a.AvailableCash, a.AccountValue, a.StartOfDayValue,
            book.FeesPaid, kill.IsKilled, _lastClean);
    }

    /// <summary>Tells the observer the account's value, at most every <see cref="AccountEvery"/> unless forced.</summary>
    private void TellAccount(bool force)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (_observer is null || (!force && _lastAccountTick is { } last && now - last < AccountEvery))
        {
            return;
        }

        _lastAccountTick = now;
        AccountSnapshot a = book.Snapshot();
        _observer.Account(new AccountTick(now, a.AccountValue, a.AvailableCash, a.StartOfDayValue, book.FeesPaid,
            [.. book.Positions.Select(p => new ObservedPosition(p.OrderbookId, p.Ticker, p.Quantity, p.CostBasis, a.PositionValues.GetValueOrDefault(p.OrderbookId, p.CostBasis)))]));
    }

    /// <summary>"XNYS " before a market's lines when the session has several markets; nothing for Stockholm alone.</summary>
    private string Where(MarketDay m) => _markets.Length == 1 ? string.Empty : m.Mic + " ";

    private static decimal Change(PaperSessionSummary s) => s.StartOfDayValue > 0 ? (s.AccountValue - s.StartOfDayValue) / s.StartOfDayValue : 0m;

    private static string Local(DateTimeOffset utc) =>
        Core.Market.MarketTime.ToStockholm(utc).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>A market and what the session has done there today (its own local date).</summary>
    private sealed class MarketDay(SessionMarket market)
    {
        public SessionMarket Market { get; } = market;

        public string Mic => Market.Mic;

        /// <summary>Gets the wait for live prices before the day's decision.</summary>
        public PriceGate Gate { get; } = new(market.PricesReady);

        public DateOnly? DecidedOn { get; set; }

        public DateOnly? EndedOn { get; set; }

        /// <summary>Today's plan on the market's own date, or null when it doesn't trade today.</summary>
        public SessionPlan? Today(DateTimeOffset utc) => Market.Schedule.Plan(Market.Schedule.Calendar.LocalDate(utc));
    }
}
