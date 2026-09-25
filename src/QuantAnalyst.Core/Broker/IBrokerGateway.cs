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
/// <c>Trading.OrderGateway</c> may use. The pre-trade fee/validation helpers (Phase 7) and the own-order stream
/// (Phase 6) are added to this interface in those phases.
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

    /// <summary>OHLCV history; the result says which resolution the broker actually used.</summary>
    Task<PriceHistory> GetPriceHistoryAsync(
        OrderbookId id, ChartPeriod period, ChartResolution? resolution, CancellationToken ct);

    /// <summary>
    /// Pushed order-depth snapshots for one orderbook, with connection state changes and heartbeats (ADR 0002 §3).
    /// Reconnects on its own; throws <see cref="SessionExpiredException"/>, <see cref="SchemaDriftException"/> or
    /// <see cref="EndpointGoneException"/> when it must stop. Runs until <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<MarketStreamEvent> StreamOrderDepthAsync(OrderbookId id, CancellationToken ct);
}
