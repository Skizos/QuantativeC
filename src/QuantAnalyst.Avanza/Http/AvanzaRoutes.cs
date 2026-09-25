using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza.Http;

/// <summary>One Avanza endpoint. <see cref="Name"/> is the logical name used in logs, recordings and errors.</summary>
public sealed record AvanzaRoute(string Name, string Method, string PathTemplate, DtoTier? Tier, bool IsAuthentication, string Source)
{
    /// <summary>Builds the path. Arguments must be plain ids ([A-Za-z0-9_-]); anything else is rejected.</summary>
    public string Path(params string[] args)
    {
        foreach (string a in args)
        {
            if (string.IsNullOrEmpty(a) || !a.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                throw new ArgumentException($"Route '{Name}': invalid path argument.", nameof(args));
            }
        }

        return string.Format(CultureInfo.InvariantCulture, PathTemplate, args);
    }

    public string Path(OrderbookId id) => Path(id.Value);
}

/// <summary>
/// The single file with every Avanza path (CLAUDE.md "Avanza gateway rules"). Phase 3 is read-only: there are
/// <b>no</b> order, stop-loss or money-transfer routes here, and architecture tests keep it that way (order routes
/// arrive in Phase 6 as internal members visible only to the order channel; transfers never, ADR 0004).
/// Before changing anything: re-read the clients at HEAD, update docs/research/avanza-endpoints.md with the new
/// commit URLs, and bump <see cref="RoutesVersion"/>.
/// </summary>
public static class AvanzaRoutes
{
    public const string RoutesVersion = "2026-09-25";

    public static readonly Uri DefaultBaseAddress = new("https://www.avanza.se");

    private const string Qluxzz = "https://github.com/Qluxzz/avanza/blob/a6a18a948f88cb7e340051e480b203b2ee917eed/avanza/constants.py";
    private const string GoSdk = "https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca";

    // ---- Authentication (username + password + TOTP). Never retried (ADR 0002). ----

    /// <summary>Step 1 of login. Body: {maxInactiveMinutes, username, password}.</summary>
    public static readonly AvanzaRoute UserCredentials = new(
        "auth.usercredentials", "POST", "/_api/authentication/sessions/usercredentials", null, true,
        "https://github.com/Qluxzz/avanza/blob/a6a18a948f88cb7e340051e480b203b2ee917eed/avanza/avanza.py");

    /// <summary>Step 2 of login. Body: {method: "TOTP", totpCode}.</summary>
    public static readonly AvanzaRoute Totp = new(
        "auth.totp", "POST", "/_api/authentication/sessions/totp", null, true,
        "https://github.com/Qluxzz/avanza/blob/a6a18a948f88cb7e340051e480b203b2ee917eed/avanza/avanza.py");

    /// <summary>Session health check; the payload contains the security token, so it is treated as authentication data.</summary>
    public static readonly AvanzaRoute SessionInfo = new(
        "session-info", "GET", "/_api/authentication/session/info/session", DtoTier.A, true, GoSdk + "/auth/auth.go");

    // ---- Account data (Tier A) ----

    public static readonly AvanzaRoute AccountsOverview = new(
        "accounts-overview", "GET", "/_api/account-overview/overview/categorizedAccounts", DtoTier.A, false, Qluxzz);

    public static readonly AvanzaRoute TradingAccounts = new(
        "trading-accounts", "GET", "/_api/trading-critical/rest/accounts", DtoTier.A, false, GoSdk + "/accounts/service.go");

    public static readonly AvanzaRoute Positions = new(
        "positions", "GET", "/_api/position-data/positions", DtoTier.A, false, Qluxzz);

    public static readonly AvanzaRoute Orders = new(
        "orders", "GET", "/_api/trading/rest/orders", DtoTier.A, false, Qluxzz);

    /// <summary>Fills. The response is not modelled by any client yet: recorded by <c>qa probe</c> only.</summary>
    public static readonly AvanzaRoute Deals = new(
        "deals", "GET", "/_api/trading/rest/deals", DtoTier.A, false, Qluxzz);

    // ---- Instrument and market data ----

    /// <summary>Orderbook parameters incl. the per-instrument tick table.</summary>
    public static readonly AvanzaRoute Orderbook = new(
        "orderbook", "GET", "/_api/trading-critical/rest/orderbook/{0}", DtoTier.A, false, Qluxzz);

    public static readonly AvanzaRoute MarketData = new(
        "marketdata", "GET", "/_api/trading-critical/rest/marketdata/{0}", DtoTier.A, false, Qluxzz);

    /// <summary>Instrument search; a read-only POST. Body: {query, searchFilter: {types}, pagination: {from, size}}.</summary>
    public static readonly AvanzaRoute Search = new(
        "search", "POST", "/_api/search/filtered-search", DtoTier.B, false, Qluxzz);

    /// <summary>OHLC history; query <c>timePeriod</c> and optional <c>resolution</c>, lower-case.</summary>
    public static readonly AvanzaRoute PriceChart = new(
        "price-chart", "GET", "/_api/price-chart/stock/{0}", DtoTier.B, false, Qluxzz);

    /// <summary>Transactions; query <c>from</c>, <c>to</c> (yyyy-MM-dd), <c>includeResult</c>.</summary>
    public static readonly AvanzaRoute Transactions = new(
        "transactions", "GET", "/_api/transactions/list", DtoTier.B, false, GoSdk + "/accounts/service.go");

    /// <summary>Every route, for architecture tests and the probe.</summary>
    public static IReadOnlyList<AvanzaRoute> All { get; } =
    [
        UserCredentials, Totp, SessionInfo, AccountsOverview, TradingAccounts, Positions, Orders, Deals,
        Orderbook, MarketData, Search, PriceChart, Transactions,
    ];
}
