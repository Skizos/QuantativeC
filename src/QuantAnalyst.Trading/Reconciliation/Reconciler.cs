using QuantAnalyst.Core;
using QuantAnalyst.Core.Orders;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Oms;

namespace QuantAnalyst.Trading.Reconciliation;

/// <summary>What the broker (or the paper channel) reports now: open orders and the day's deals.</summary>
public sealed record BrokerSnapshot(IReadOnlyList<BrokerOrder> OpenOrders, IReadOnlyList<BrokerDeal> Deals, DateTimeOffset AtUtc);

/// <summary>Where a snapshot comes from: the Avanza read gateway live (Phase 7), the paper channel in Paper.</summary>
public interface IBrokerStateSource
{
    Task<BrokerSnapshot> GetAsync(CancellationToken ct);
}

public sealed record ReconciliationReport(DateTimeOffset AtUtc, int Checked, IReadOnlyList<string> Actions, IReadOnlyList<string> Mismatches)
{
    public bool Clean => Mismatches.Count == 0;
}

/// <summary>
/// Reconciliation (ADR 0003 §6): brings the OMS in line with what the broker reports, and halts when they disagree.
/// <list type="bullet">
/// <item>Orders with a broker id: deals the OMS has not seen become fills; an order the broker no longer lists (and
/// that is not filled) is cancelled; an Unknown order the broker still lists is working again.</item>
/// <item>Unknown orders without a broker id: matched on (orderbook, side, limit, original volume, created ≥ sent − 5 s)
/// against open orders, and on (orderbook, side, time) against deals of orders we do not know. One match links it;
/// more than one is a mismatch. None, in two consecutive runs and at least 2 minutes after the submit, means it was not
/// placed (Rejected).</item>
/// <item>Mismatches (ambiguous match, more filled in the OMS than at the broker, a remaining volume that differs, an
/// order at the broker the OMS did not place) raise the <see cref="HaltReason.Reconciliation"/> halt; a clean run
/// clears it. The kill switch fires if it lasts more than 60 s.</item>
/// </list>
/// </summary>
public sealed class Reconciler(OrderManager oms, HaltController halts, AuditLog audit, TimeProvider time, AccountId account)
{
    /// <summary>ADR 0003 §6: how far before our send time a broker order may be stamped and still be ours.</summary>
    public static readonly TimeSpan MatchSlack = TimeSpan.FromSeconds(5);

    /// <summary>ADR 0003 §6: an Unknown order may be declared "not placed" only after this long.</summary>
    public static readonly TimeSpan NotPlacedAfter = TimeSpan.FromMinutes(2);

    private readonly Dictionary<Guid, int> _notFound = [];

    public DateTimeOffset? LastRunUtc { get; private set; }

    public async Task<ReconciliationReport> RunAsync(IBrokerStateSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        BrokerSnapshot snapshot = await source.GetAsync(ct).ConfigureAwait(false);
        ReconciliationReport report = Reconcile(snapshot);
        LastRunUtc = report.AtUtc;
        return report;
    }

    public ReconciliationReport Reconcile(BrokerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DateTimeOffset now = time.GetUtcNow();
        var actions = new List<string>();
        var mismatches = new List<string>();

        BrokerOrder[] open = [.. snapshot.OpenOrders.Where(o => o.Account == account)];
        BrokerDeal[] deals = [.. snapshot.Deals.Where(d => d.Account == account)];
        IReadOnlyList<OmsOrder> all = oms.All;
        var known = all.Where(o => o.BrokerOrderId is not null).Select(o => o.BrokerOrderId!.Value).ToHashSet();
        OmsOrder[] toCheck = [.. all.Where(o => o.IsOpen)];

        foreach (OmsOrder order in toCheck.Where(o => o.BrokerOrderId is null && o.State == OmsState.Unknown))
        {
            LinkUnknown(order, open, deals, known, now, actions, mismatches);
        }

        foreach (OmsOrder order in oms.All.Where(o => o.IsOpen && o.BrokerOrderId is not null))
        {
            CheckKnown(order, open, deals, actions, mismatches);
        }

        var ours = oms.All.Where(o => o.BrokerOrderId is not null).Select(o => o.BrokerOrderId!.Value).ToHashSet();
        foreach (BrokerOrder stranger in open.Where(o => !ours.Contains(o.Id)))
        {
            mismatches.Add($"the broker lists order {stranger.Id} ({stranger.InstrumentName} {stranger.Side} {stranger.Volume} @ {stranger.Price}) that the OMS did not place");
        }

        var report = new ReconciliationReport(now, toCheck.Length, actions, mismatches);
        audit.Append("reconcile", new { report.Checked, open = open.Length, deals = deals.Length, actions, mismatches });
        if (report.Clean)
        {
            halts.Clear(HaltReason.Reconciliation, "clean reconciliation");
        }
        else
        {
            halts.Raise(HaltReason.Reconciliation, string.Join("; ", mismatches));
        }

        return report;
    }

    private void LinkUnknown(OmsOrder order, BrokerOrder[] open, BrokerDeal[] deals, HashSet<OrderId> known, DateTimeOffset now, List<string> actions, List<string> mismatches)
    {
        DateTimeOffset from = order.CreatedUtc - MatchSlack;
        var candidates = open
            .Where(o => !known.Contains(o.Id) && o.OrderbookId == order.OrderbookId && o.Side == order.Side && o.Price == order.LimitPrice
                        && o.OriginalVolume == order.Volume && (o.CreatedUtc is null || o.CreatedUtc >= from))
            .Select(o => o.Id)
            .Concat(deals
                .Where(d => !known.Contains(d.OrderId) && d.OrderbookId == order.OrderbookId && d.Side == order.Side && d.TimeUtc >= from)
                .Select(d => d.OrderId))
            .Distinct()
            .ToList();

        switch (candidates.Count)
        {
            case 1:
                _notFound.Remove(order.ClientOrderId);
                known.Add(candidates[0]);
                oms.TryTransition(order.ClientOrderId, OmsState.Working, "reconciliation: found at the broker", candidates[0], OmsState.Unknown);
                actions.Add($"{order.Ticker}: unknown order found as {candidates[0]}");
                break;
            case > 1:
                mismatches.Add($"{order.Ticker}: unknown order {order.ClientOrderId} matches {candidates.Count} broker orders ({string.Join(", ", candidates)})");
                break;
            default:
                int streak = _notFound[order.ClientOrderId] = _notFound.GetValueOrDefault(order.ClientOrderId) + 1;
                if (streak >= 2 && now - order.CreatedUtc >= NotPlacedAfter)
                {
                    _notFound.Remove(order.ClientOrderId);
                    oms.TryTransition(order.ClientOrderId, OmsState.Rejected, "reconciliation: not placed (absent in two runs, no deals)", null, OmsState.Unknown);
                    actions.Add($"{order.Ticker}: unknown order {order.ClientOrderId} was not placed");
                }

                break;
        }
    }

    private void CheckKnown(OmsOrder order, BrokerOrder[] open, BrokerDeal[] deals, List<string> actions, List<string> mismatches)
    {
        OrderId id = order.BrokerOrderId!.Value;
        BrokerDeal[] fills = [.. deals.Where(d => d.OrderId == id)];
        decimal brokerFilled = fills.Sum(d => d.Volume);
        if (brokerFilled > order.FilledVolume)
        {
            long missing = (long)(brokerFilled - order.FilledVolume);
            decimal missingValue = fills.Sum(d => d.Volume * d.Price) - order.FilledValue;
            oms.ApplyFillValue(order.ClientOrderId, missing, missingValue, 0m, "reconciliation");
            if (open.All(o => o.Id != id) && order.State == OmsState.PartiallyFilled)
            {
                // Gone from the broker after a partial fill (a cancel whose reply was lost, or the day ended).
                oms.TryTransition(order.ClientOrderId, OmsState.Cancelled, "reconciliation: ended at the broker after a partial fill", id, OmsState.PartiallyFilled);
            }

            actions.Add($"{order.Ticker}: {missing} filled at the broker that the OMS had not seen");
        }
        else if (brokerFilled < order.FilledVolume)
        {
            mismatches.Add($"{order.Ticker}: the OMS has {order.FilledVolume} filled, the broker's deals {brokerFilled} (order {id})");
            return;
        }

        if (!order.IsOpen)
        {
            return;
        }

        BrokerOrder? listed = open.FirstOrDefault(o => o.Id == id);
        if (listed is null)
        {
            if (oms.TryTransition(order.ClientOrderId, OmsState.Cancelled, "reconciliation: no longer at the broker", id, OmsState.Working, OmsState.PartiallyFilled, OmsState.Unknown))
            {
                actions.Add($"{order.Ticker}: order {id} ended at the broker");
            }

            return;
        }

        if (order.State == OmsState.Unknown)
        {
            OmsState back = order.FilledVolume > 0 ? OmsState.PartiallyFilled : OmsState.Working;
            oms.TryTransition(order.ClientOrderId, back, "reconciliation: still working at the broker", id, OmsState.Unknown);
            actions.Add($"{order.Ticker}: order {id} is still working");
        }

        if (listed.Volume != order.Remaining)
        {
            mismatches.Add($"{order.Ticker}: order {id} has {listed.Volume} left at the broker, {order.Remaining} in the OMS");
        }
    }
}
