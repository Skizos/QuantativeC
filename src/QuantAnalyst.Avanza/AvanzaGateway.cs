using System.Globalization;
using QuantAnalyst.Avanza.Dto;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Logging;
using QuantAnalyst.Avanza.Mapping;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Core.Orders;

namespace QuantAnalyst.Avanza;

/// <summary>
/// Read-only Avanza implementation of <see cref="IBrokerGateway"/> (ADR 0002). It has no way to place, modify or
/// cancel orders and no money-movement capability (ADR 0004).
/// </summary>
internal sealed class AvanzaGateway(AvanzaApiClient api, TimeProvider time, Redactor redactor) : IBrokerGateway
{
    public async Task<SessionHealth> GetSessionHealthAsync(CancellationToken ct)
    {
        SessionInfoDto dto = await api.GetAsync(
            AvanzaRoutes.SessionInfo, AvanzaRoutes.SessionInfo.Path(), AvanzaTierAContext.Default.SessionInfoDto, SessionInfoDto.Version, ct).ConfigureAwait(false);
        redactor.AddSecret(dto.User.SecurityToken);
        redactor.AddSecret(dto.User.PushSubscriptionId);
        return AvanzaMapper.ToSessionHealth(dto, time.GetUtcNow());
    }

    public async Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct)
    {
        AccountsOverviewDto dto = await api.GetAsync(
            AvanzaRoutes.AccountsOverview, AvanzaRoutes.AccountsOverview.Path(), AvanzaTierAContext.Default.AccountsOverviewDto, AccountsOverviewDto.Version, ct).ConfigureAwait(false);
        IReadOnlyList<Account> accounts = AvanzaMapper.ToAccounts(dto);
        foreach (Account a in accounts)
        {
            redactor.AddAccountId(a.Id.Value);
        }

        return accounts;
    }

    public async Task<IReadOnlyList<TradingAccount>> GetTradingAccountsAsync(CancellationToken ct)
    {
        List<TradingAccountDto> dto = await api.GetAsync(
            AvanzaRoutes.TradingAccounts, AvanzaRoutes.TradingAccounts.Path(), AvanzaTierAContext.Default.ListTradingAccountDto, TradingAccountDto.Version, ct).ConfigureAwait(false);
        IReadOnlyList<TradingAccount> accounts = AvanzaMapper.ToTradingAccounts(dto);
        foreach (TradingAccount a in accounts)
        {
            redactor.AddAccountId(a.Id.Value);
        }

        return accounts;
    }

    public async Task<PortfolioSnapshot> GetPositionsAsync(AccountId? account, CancellationToken ct)
    {
        PositionsDto dto = await api.GetAsync(
            AvanzaRoutes.Positions, AvanzaRoutes.Positions.Path(), AvanzaTierAContext.Default.PositionsDto, PositionsDto.Version, ct).ConfigureAwait(false);
        PortfolioSnapshot snapshot = AvanzaMapper.ToPortfolio(dto, account, time.GetUtcNow());
        foreach (AccountId id in snapshot.Positions.Select(p => p.Account).Concat(snapshot.Cash.Select(c => c.Account)).Distinct())
        {
            redactor.AddAccountId(id.Value);
        }

        return snapshot;
    }

    public async Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(CancellationToken ct)
    {
        OrdersDto dto = await api.GetAsync(
            AvanzaRoutes.Orders, AvanzaRoutes.Orders.Path(), AvanzaTierAContext.Default.OrdersDto, OrdersDto.Version, ct).ConfigureAwait(false);
        return AvanzaMapper.ToOrders(dto);
    }

    public Task<IReadOnlyList<BrokerDeal>> GetDealsAsync(CancellationToken ct) =>
        throw new EndpointNotModelledException(
            AvanzaRoutes.Deals.Name,
            "no maintained client models the current response; run 'qa probe' to record it so the DTO can be written");

    public async Task<IReadOnlyList<BrokerTransaction>> GetTransactionsAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct)
    {
        if (fromDate > toDate)
        {
            throw new ArgumentException("fromDate must not be after toDate.", nameof(fromDate));
        }

        string query = string.Create(
            CultureInfo.InvariantCulture, $"?from={fromDate:yyyy-MM-dd}&to={toDate:yyyy-MM-dd}&includeResult=false");
        TransactionsDto dto = await api.GetAsync(
            AvanzaRoutes.Transactions, AvanzaRoutes.Transactions.Path() + query, AvanzaTierBContext.Default.TransactionsDto, TransactionsDto.Version, ct).ConfigureAwait(false);
        return AvanzaMapper.ToTransactions(dto);
    }

    public async Task<IReadOnlyList<InstrumentSearchHit>> SearchStocksAsync(string query, int maxHits, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxHits, 50);
        var request = new SearchRequestDto
        {
            Query = query.Trim(),
            SearchFilter = new SearchFilterDto { Types = ["STOCK"] },
            Pagination = new SearchPaginationDto { From = 0, Size = maxHits },
        };
        SearchResponseDto dto = await api.PostAsync(
            AvanzaRoutes.Search, request, AvanzaTierBContext.Default.SearchRequestDto, AvanzaTierBContext.Default.SearchResponseDto, SearchResponseDto.Version, ct).ConfigureAwait(false);
        return AvanzaMapper.ToSearchHits(dto);
    }

    public async Task<InstrumentTradingParams> GetTradingParamsAsync(OrderbookId id, CancellationToken ct)
    {
        OrderbookDto dto = await api.GetAsync(
            AvanzaRoutes.Orderbook, AvanzaRoutes.Orderbook.Path(id), AvanzaTierAContext.Default.OrderbookDto, OrderbookDto.Version, ct).ConfigureAwait(false);
        return AvanzaMapper.ToTradingParams(dto, time.GetUtcNow());
    }

    public async Task<MarketSnapshot> GetMarketSnapshotAsync(OrderbookId id, CancellationToken ct)
    {
        MarketDataDto dto = await api.GetAsync(
            AvanzaRoutes.MarketData, AvanzaRoutes.MarketData.Path(id), AvanzaTierAContext.Default.MarketDataDto, MarketDataDto.Version, ct).ConfigureAwait(false);
        return AvanzaMapper.ToMarketSnapshot(id, dto, time.GetUtcNow());
    }

    public async Task<IReadOnlyList<Bar>> GetPriceHistoryAsync(OrderbookId id, ChartPeriod period, ChartResolution? resolution, CancellationToken ct)
    {
        string query = "?timePeriod=" + WireName(period.ToString()) + (resolution is { } r ? "&resolution=" + WireName(r.ToString()) : string.Empty);
        PriceChartDto dto = await api.GetAsync(
            AvanzaRoutes.PriceChart, AvanzaRoutes.PriceChart.Path(id) + query, AvanzaTierBContext.Default.PriceChartDto, PriceChartDto.Version, ct).ConfigureAwait(false);
        return AvanzaMapper.ToBars(dto);
    }

    /// <summary>Raw GET for routes that are recorded but not modelled yet (deals). Used by the probe only.</summary>
    internal Task<byte[]> GetRawAsync(AvanzaRoute route, CancellationToken ct) => api.SendAsync(route, route.Path(), null, ct);

    /// <summary>"OneMonth" → "one_month", "FiveMinutes" → "five_minutes" (lower snake case, as the clients send).</summary>
    internal static string WireName(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 4);
        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];
            if (char.IsUpper(c) && i > 0)
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
