using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Model;
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
/// One Paper trading session (ADR 0003, plan 06): a 1-second loop that
/// <list type="number">
/// <item>reconciles the OMS with the paper channel every 30 s, and at once when an Unknown order reaches 2 minutes;</item>
/// <item>ticks the kill switch (flag file, automatic triggers, cancels);</item>
/// <item>at the decision time of each trading day asks <c>decide</c> for intents (bars through yesterday, live prices);</item>
/// <item>submits them through <see cref="OrderGateway"/>, one at a time and paced (R11 allows 5 actions a minute);</item>
/// <item>at the close ends the day orders and reports the day.</item>
/// </list>
/// On the way out it cancels everything still working through the gateway and ends the rest, so no paper order
/// outlives the session. Quotes reach the paper channel from the caller (<see cref="PaperOrderChannel.OnQuote"/>).
/// </summary>
public sealed class PaperSession(
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
    TextWriter output)
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReconcileEvery = TimeSpan.FromSeconds(30);

    /// <summary>12 s apart keeps a burst of intents at 5 actions a minute (R11), with room for one cancel.</summary>
    public static readonly TimeSpan PaceBetweenOrders = TimeSpan.FromSeconds(13);

    private readonly Queue<OrderIntent> _queue = new();
    private DateOnly? _decidedOn;
    private DateOnly? _endedOn;
    private DateTimeOffset? _lastSubmit;
    private int _decisions;
    private int _submitted;
    private int _accepted;
    private int _riskRejected;
    private bool _lastClean = true;

    public async Task<PaperSessionSummary> RunAsync(DateTimeOffset stopAtUtc, CancellationToken ct)
    {
        audit.Append("session-start", new { mode = "Paper", stopAtUtc, book.Costs, cash = book.Cash });
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

        DateOnly today = OrderGateway.StockholmDate(now);
        SessionPlan? plan = schedule.Plan(today);
        if (plan is null)
        {
            return;
        }

        if (_decidedOn != today && now >= plan.DecisionUtc && now < plan.WindowCloseUtc)
        {
            _decidedOn = today;
            await DecideAsync(ct).ConfigureAwait(false);
        }

        if (_queue.Count > 0 && now < plan.WindowCloseUtc && (_lastSubmit is not { } s || now - s >= PaceBetweenOrders))
        {
            await SubmitNextAsync(ct).ConfigureAwait(false);
        }

        if (_endedOn != today && now >= plan.CloseUtc)
        {
            _endedOn = today;
            _queue.Clear();
            int ended = channel.EndOfDay("day order expired at the close");
            await ReconcileAsync(ct).ConfigureAwait(false);
            PaperSessionSummary day = Summarize();
            audit.Append("end-of-day", new { date = today, ended, day });
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{Local(now)} close: {ended} order(s) expired. Value {day.AccountValue:N2} SEK ({Change(day):+0.00%;-0.00%} today), cash {day.Cash:N2}, fees {day.FeesPaid:N2}."));
        }
    }

    private async Task DecideAsync(CancellationToken ct)
    {
        _decisions++;
        if (kill.IsKilled || halts.IsHalted)
        {
            string why = string.Join(", ", halts.Active.Select(h => h.Reason));
            output.WriteLine($"{Local(time.GetUtcNow())} decision skipped: trading is halted ({why}).");
            audit.Append("decision-skipped", new { halts = why });
            return;
        }

        PlanResult plan;
        try
        {
            plan = await decide(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Analytics.Backtesting.StrategyException)
        {
            output.WriteLine($"{Local(time.GetUtcNow())} decision failed: {ex.Message}");
            audit.Append("decision-failed", new { reason = ex.Message });
            return;
        }

        audit.Append("decision", new { intents = plan.Intents.Count, plan.Notes });
        output.WriteLine($"{Local(time.GetUtcNow())} decision: {plan.Intents.Count} order(s).");
        foreach (string note in plan.Notes)
        {
            output.WriteLine("  " + note);
        }

        foreach (OrderIntent intent in plan.Intents)
        {
            _queue.Enqueue(intent);
        }
    }

    private async Task SubmitNextAsync(CancellationToken ct)
    {
        OrderIntent queued = _queue.Dequeue();
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

    private static decimal Change(PaperSessionSummary s) => s.StartOfDayValue > 0 ? (s.AccountValue - s.StartOfDayValue) / s.StartOfDayValue : 0m;

    private static string Local(DateTimeOffset utc) =>
        Core.Market.MarketTime.ToStockholm(utc).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
