using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Orders;

namespace QuantAnalyst.Trading.Reconciliation;

/// <summary>
/// The live reconciliation source (Phase 7): open orders and deals through the <b>read</b> gateway. Until a recording
/// contains a fill, the deals mapper refuses non-empty lists (schema drift), so live reconciliation fails closed on
/// the first fill; recording one is a Phase 7 prerequisite.
/// </summary>
public sealed class GatewayBrokerState(IBrokerGateway gateway, TimeProvider time) : IBrokerStateSource
{
    public async Task<BrokerSnapshot> GetAsync(CancellationToken ct)
    {
        IReadOnlyList<BrokerOrder> open = await gateway.GetOpenOrdersAsync(ct).ConfigureAwait(false);
        IReadOnlyList<BrokerDeal> deals = await gateway.GetDealsAsync(ct).ConfigureAwait(false);
        return new BrokerSnapshot(open, deals, time.GetUtcNow());
    }
}
