using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Reconciliation;
using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Trading.Live;

public sealed record ConfirmSessionSummary(
    int Cards,
    int Accepted,
    int Skipped,
    int RiskRejected,
    int Blocked,
    int Filled,
    decimal? AccountValue,
    decimal? StartOfDayValue,
    decimal? Cash,
    bool Killed,
    bool ReconciliationClean);

/// <summary>
/// One Confirm session (ADR 0003 §5; plan 07 step 5): the Paper session's day with an order card per order. A 1-second
/// loop that
/// <list type="number">
/// <item>reconciles the OMS with the broker every 30 s (and when an Unknown order reaches 2 minutes), then marks the
/// account state stale so the next read is fresh; a read that fails raises its halt (<see cref="BrokerHalts"/>);</item>
/// <item>ticks the kill switch (flag file, automatic triggers, the daily loss stop on the live account);</item>
/// <item>from the decision time (or the window's open, for <c>rebalance --execute</c>) <b>re-plans before every card</b>
/// on the current account and quotes, and submits the first intent for an instrument not handled yet today. An
/// instrument gets one card a day: a skipped, rejected or sent one is not proposed again, so a late answer can never
/// confirm a different card;</item>
/// <item>at the close reconciles and reports the day (Avanza ends day orders itself).</item>
/// </list>
/// Stopping it cancels its working orders through the gateway: nothing it placed is left unwatched.
/// <paramref name="costAssumptionBps"/> (the backtest's half-spread + slippage) is recorded at the start, so the end-of-day
/// report can compare the realised slippage with it (plan 07 step 6).
/// </summary>
public sealed class ConfirmSession(
    OrderGateway gateway,
    IAccountState account,
    KillSwitch kill,
    Reconciler reconciler,
    IBrokerStateSource broker,
    HaltController halts,
    TradingSchedule schedule,
    AuditLog audit,
    TimeProvider time,
    Func<CancellationToken, Task<PlanResult>> plan,
    TextWriter output,
    bool decideAtStart = false,
    Func<DateOnly, string>? endOfDayReport = null,
    decimal? costAssumptionBps = null)
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReconcileEvery = TimeSpan.FromSeconds(30);

    /// <summary>Cards are at least this far apart: R11 allows 5 actions a minute, with room for one cancel.</summary>
    public static readonly TimeSpan PaceBetweenOrders = TimeSpan.FromSeconds(13);

    private readonly HashSet<OrderbookId> _handled = [];
    private DateOnly? _tradingOn;
    private DateOnly? _doneOn;
    private DateOnly? _endedOn;
    private DateTimeOffset? _lastSubmit;
    private DateTimeOffset? _lastReconcile;
    private int _plansToday;
    private int _cards;
    private int _accepted;
    private int _skipped;
    private int _riskRejected;
    private int _blocked;
    private bool _lastClean = true;

    /// <summary>Gets the instruments already handled today (one card each).</summary>
    public IReadOnlyCollection<OrderbookId> Handled => _handled;

    public async Task<ConfirmSessionSummary> RunAsync(DateTimeOffset stopAtUtc, CancellationToken ct)
    {
        audit.Append("session-start", new { mode = "Confirm", stopAtUtc, decideAtStart, costAssumptionBps });
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped while a card was shown: nothing was sent for it (the gateway audited the skip).
        }
        finally
        {
            int open = await gateway.CancelAllAsync("session stopped", CancellationToken.None).ConfigureAwait(false);
            if (open > 0)
            {
                output.WriteLine($"{Local(time.GetUtcNow())} WARNING: {open} order(s) may still be open at Avanza; check them in the web app (qa orders).");
            }

            await ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            DateOnly today = OrderGateway.StockholmDate(time.GetUtcNow());
            if (_endedOn != today)
            {
                Report(today, partial: true);
            }
        }

        ConfirmSessionSummary summary = await SummarizeAsync().ConfigureAwait(false);
        audit.Append("session-end", summary);
        return summary;
    }

    /// <summary>One loop iteration (public for tests that step the session by hand).</summary>
    public async Task StepAsync(CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();
        bool unknownDue = gateway.Oms.Open.Any(o => o.State == OmsState.Unknown && now - o.CreatedUtc >= Reconciler.NotPlacedAfter
                                                        && (_lastReconcile is not { } last || last < o.CreatedUtc + Reconciler.NotPlacedAfter));
        if (_lastReconcile is not { } lastRun || now - lastRun >= ReconcileEvery || unknownDue)
        {
            await ReconcileAsync(ct).ConfigureAwait(false);
        }

        await kill.TickAsync(ct).ConfigureAwait(false);

        DateOnly today = OrderGateway.StockholmDate(now);
        SessionPlan? day = schedule.Plan(today);
        if (day is null)
        {
            return;
        }

        DateTimeOffset start = decideAtStart ? day.WindowOpenUtc : day.DecisionUtc;
        if (_tradingOn != today && now >= start && now < day.WindowCloseUtc)
        {
            _tradingOn = today;
            _handled.Clear();
            _plansToday = 0;
        }

        if (_tradingOn == today && _doneOn != today && now < day.WindowCloseUtc && (_lastSubmit is not { } s || now - s >= PaceBetweenOrders))
        {
            await NextCardAsync(today, ct).ConfigureAwait(false);
        }

        if (_endedOn != today && now >= day.CloseUtc)
        {
            _endedOn = today;
            await ReconcileAsync(ct).ConfigureAwait(false);
            ConfirmSessionSummary summary = await SummarizeAsync().ConfigureAwait(false);
            audit.Append("end-of-day", new { date = today, day = summary });
            output.WriteLine($"{Local(now)} close: Avanza ends the day orders. {summary.Cards} card(s), {summary.Accepted} sent and accepted, {summary.Skipped} skipped.");
            Report(today, partial: false);
        }
    }

    private async Task NextCardAsync(DateOnly today, CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (kill.IsKilled || halts.IsHalted)
        {
            string why = string.Join(", ", halts.Active.Select(h => h.Reason));
            output.WriteLine($"{Local(now)} no more cards today: trading is halted ({why}).");
            audit.Append("decision-skipped", new { halts = why });
            _doneOn = today;
            return;
        }

        PlanResult planned;
        try
        {
            planned = await plan(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Analytics.Backtesting.StrategyException || BrokerHalts.IsLiveReadFailure(ex))
        {
            output.WriteLine($"{Local(now)} decision failed: {ex.Message}");
            audit.Append("decision-failed", new { reason = ex.Message });
            if (BrokerHalts.For(ex) is { } reason)
            {
                halts.Raise(reason, "decision: " + ex.Message);
            }

            _doneOn = today;
            return;
        }

        if (++_plansToday == 1)
        {
            audit.Append("decision", new { intents = planned.Intents.Count, planned.Notes });
            output.WriteLine($"{Local(now)} decision: {planned.Intents.Count} order(s), one card each.");
            foreach (string note in planned.Notes)
            {
                output.WriteLine("  " + note);
            }
        }
        else
        {
            audit.Append("replan", new { intents = planned.Intents.Count, handled = _handled.Count });
        }

        OrderIntent? next = planned.Intents.FirstOrDefault(i => !_handled.Contains(i.OrderbookId));
        if (next is null)
        {
            output.WriteLine($"{Local(now)} nothing more to trade today.");
            _doneOn = today;
            return;
        }

        _handled.Add(next.OrderbookId);
        _lastSubmit = now;
        _cards++;
        SubmitResult r = await gateway.SubmitAsync(next with { DecisionTimeUtc = now }, ct).ConfigureAwait(false);
        switch (r.Status)
        {
            case SubmitStatus.Accepted:
                _accepted++;
                break;
            case SubmitStatus.Skipped:
                _skipped++;
                break;
            case SubmitStatus.RiskRejected:
                _riskRejected++;
                break;
            case SubmitStatus.Blocked:
                _blocked++;
                break;
        }

        string detail = r.Order is { } o
            ? string.Create(CultureInfo.InvariantCulture, $"{o.State}, filled {o.FilledVolume}/{o.Volume}")
            : r.Message;
        output.WriteLine($"{Local(time.GetUtcNow())} {next.Side} {next.Quantity} {next.Ticker}: {r.Status} ({detail})");
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        _lastReconcile = time.GetUtcNow();
        try
        {
            ReconciliationReport report = await reconciler.RunAsync(broker, ct).ConfigureAwait(false);
            if (!report.Clean && _lastClean)
            {
                output.WriteLine($"{Local(report.AtUtc)} RECONCILIATION MISMATCH: {string.Join("; ", report.Mismatches)}");
            }

            _lastClean = report.Clean;
        }
        catch (Exception ex) when (BrokerHalts.IsLiveReadFailure(ex))
        {
            HaltReason? reason = BrokerHalts.For(ex);
            audit.Append("reconcile-failed", new { error = ex.GetType().Name, ex.Message, halt = reason?.ToString() });
            if (reason is { } r)
            {
                _lastClean = false;
                halts.Raise(r, "reconciliation: " + ex.Message);
                output.WriteLine($"{Local(time.GetUtcNow())} RECONCILIATION FAILED ({r}): {ex.Message}");
            }
        }
        finally
        {
            account.Invalidate(); // fills found by reconciliation change cash and positions
        }
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
            output.WriteLine($"The end-of-day report could not be written ({ex.Message}); rebuild it with 'qa report eod --date {date:yyyy-MM-dd}'.");
        }
    }

    private async Task<ConfirmSessionSummary> SummarizeAsync()
    {
        AccountSnapshot? a = null;
        try
        {
            a = await account.GetAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (BrokerHalts.IsLiveReadFailure(ex))
        {
            audit.Append("summary-account-unavailable", new { error = ex.GetType().Name, ex.Message });
        }

        int filled = gateway.Oms.All.Count(o => o.FilledVolume > 0);
        return new ConfirmSessionSummary(_cards, _accepted, _skipped, _riskRejected, _blocked, filled, a?.AccountValue, a?.StartOfDayValue, a?.AvailableCash, kill.IsKilled, _lastClean);
    }

    private static string Local(DateTimeOffset utc) =>
        Core.Market.MarketTime.ToStockholm(utc).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
