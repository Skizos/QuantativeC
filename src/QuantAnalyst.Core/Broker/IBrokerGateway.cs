using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Core.Orders;

namespace QuantAnalyst.Core.Broker;

/// <summary>Result of the broker's session health check.</summary>
public sealed record SessionHealth(bool LoggedIn, DateTimeOffset CheckedAtUtc);

/// <summary>
/// Read side of the broker (ADR 0002 §1). Everything that only <em>reads</em> goes here; placing, modifying
/// and cancelling orders lives on a separate port (<c>IBrokerOrderChannel</c>, Phase 6) that only
/// <c>Trading.OrderGateway</c> may use. Streams (Phase 4) and the pre-trade fee/validation helpers
/// (Phase 7) are added to this interface in those phases.
/// </summary>
/// <remarks>
/// Implementations throw <see cref="SchemaDriftException"/> when a payload no longer matches its DTO,
/// <see cref="SessionExpiredException"/> on 401/403, <see cref="EndpointGoneException"/> when a
/// trading-critical route returns 404, and <see cref="BrokerUnavailableException"/> for other failures.
/// </remarks>
public interface IBrokerGateway
{
    Task<SessionHealth> GetSessionHealthAsync(CancellationToken ct);

    Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct);

    Task<IReadOnlyList<TradingAccount>> GetTradingAccountsAsync(CancellationToken ct);

    /// <summary>Positions and cash of every account, or of one account when <paramref name="account"/> is set.</summary>
    Task<PortfolioSnapshot> GetPositionsAsync(AccountId? account, CancellationToken ct);

    Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(CancellationToken ct);

    Task<IReadOnlyList<BrokerDeal>> GetDealsAsync(CancellationToken ct);

    Task<IReadOnlyList<BrokerTransaction>> GetTransactionsAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct);

    Task<IReadOnlyList<InstrumentSearchHit>> SearchStocksAsync(string query, int maxHits, CancellationToken ct);

    Task<InstrumentTradingParams> GetTradingParamsAsync(OrderbookId id, CancellationToken ct);

    Task<MarketSnapshot> GetMarketSnapshotAsync(OrderbookId id, CancellationToken ct);

    Task<IReadOnlyList<Bar>> GetPriceHistoryAsync(
        OrderbookId id, ChartPeriod period, ChartResolution? resolution, CancellationToken ct);
}
