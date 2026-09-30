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
/// The single file with every Avanza path (CLAUDE.md "Avanza gateway rules"). <see cref="AvanzaRoutes"/> is read-only.
/// The three order-entry routes live apart in the internal <see cref="AvanzaOrderRoutes"/>, which only
/// <c>AvanzaOrderChannel</c> may reference (an IL-scanning architecture test checks it). There are no stop-loss, fund
/// order or money-transfer routes, and there never will be for transfers (ADR 0004).
/// Before changing anything: re-read the clients at HEAD, update docs/research/avanza-endpoints.md with the new
/// commit URLs, and bump <see cref="RoutesVersion"/>.
/// </summary>
public static class AvanzaRoutes
{
    // .1: order routes in AvanzaOrderRoutes (Phase 6); .2: AvanzaPreflightRoutes (Phase 7 step 1); 2026-09-30.1: StockDetails (plan 21)
    public const string RoutesVersion = "2026-09-30.1";

    public static readonly Uri DefaultBaseAddress = new("https://www.avanza.se");

    private const string Qluxzz = "https://github.com/Qluxzz/avanza/blob/a6a18a948f88cb7e340051e480b203b2ee917eed/avanza/constants.py";
    private const string GoSdk = "https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca";

    // ---- Authentication (username + password + TOTP; optional, see AvanzaLoginMethod). Never retried (ADR 0002). ----

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

    // ---- Authentication (BankID, a human approves on the phone). One transaction per trigger. ----
    // The two HTML GETs (start page, trading page) only collect session cookies, exactly as the reference
    // client does; their content is never parsed or recorded (ADR 0002 §6).

    /// <summary>Start page, fetched once for the initial cookies (AZAPERSISTENCE …) before a BankID login.</summary>
    public static readonly AvanzaRoute StartPage = new(
        "auth.start-page", "GET", "/", null, true, GoSdk + "/auth/auth.go");

    /// <summary>Starts a BankID transaction. Body: {method: "QR_START", returnScheme: "NULL"} ⇒ {transactionId, expires, qrToken}.</summary>
    public static readonly AvanzaRoute BankIdStart = new(
        "auth.bankid.start", "POST", "/_api/authentication/v2/sessions/bankid", null, true, GoSdk + "/auth/auth.go");

    /// <summary>Fresh QR token for the same transaction (animated QR). Body: {}.</summary>
    public static readonly AvanzaRoute BankIdRestart = new(
        "auth.bankid.restart", "POST", "/_api/authentication/v2/sessions/bankid/restart", null, true, GoSdk + "/auth/auth.go");

    /// <summary>Transaction state: OUTSTANDING_TRANSACTION | COMPLETE (with logins[].loginPath) | FAILED. Body: {}.</summary>
    public static readonly AvanzaRoute BankIdCollect = new(
        "auth.bankid.collect", "POST", "/_api/authentication/v2/sessions/bankid/collect", null, true, GoSdk + "/auth/auth.go");

    /// <summary>Selects the customer after a completed BankID transaction; the suffix comes from <c>logins[0].loginPath</c>.</summary>
    public static readonly AvanzaRoute BankIdLogin = new(
        "auth.bankid.login", "GET", "/_api/authentication/v2/sessions/bankid/{0}", null, true, GoSdk + "/auth/auth.go");

    /// <summary>Trading page, fetched once after BankID for the remaining session cookies (AZACSRF).</summary>
    public static readonly AvanzaRoute TradingPage = new(
        "auth.trading-page", "GET", "/handla/order.html", null, true, GoSdk + "/auth/auth.go");

    /// <summary>A same-origin redirect followed manually during login (so every hop's cookies are kept).</summary>
    public static readonly AvanzaRoute LoginRedirect = new(
        "auth.redirect", "GET", "(same-origin redirect during login)", null, true, GoSdk + "/client/client.go");

    /// <summary>
    /// Validates a server-supplied <c>loginPath</c>: it must be a relative path under the BankID route with 1–3 plain
    /// segments. Anything else (absolute URL, another route, dot segments, query) is schema drift, never followed.
    /// </summary>
    public static bool IsValidBankIdLoginPath(string? loginPath)
    {
        const string prefix = "/_api/authentication/v2/sessions/bankid/";
        if (loginPath is null || !loginPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] segments = loginPath[prefix.Length..].Split('/');
        return segments.Length is >= 1 and <= 3
               && segments.All(s => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
               && segments[0] is not ("collect" or "restart");
    }

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

    /// <summary>
    /// A stock's extended data; we read only its dividends (past and announced) and share count (plan 21). Public: the Go
    /// SDK's <c>GetStockDetails</c> "does not require an authenticated session".
    /// </summary>
    public static readonly AvanzaRoute StockDetails = new(
        "stock-details", "GET", "/_api/market-guide/stock/{0}/details", DtoTier.B, false, GoSdk + "/market/service.go");

    /// <summary>Transactions; query <c>from</c>, <c>to</c> (yyyy-MM-dd), <c>includeResult</c>.</summary>
    public static readonly AvanzaRoute Transactions = new(
        "transactions", "GET", "/_api/transactions/list", DtoTier.B, false, GoSdk + "/accounts/service.go");

    // ---- Push streams (server-sent events; ADR 0002 §3). Needs the login cookies csid, cstoken and AZACSRF. ----

    /// <summary>
    /// Order depth of one orderbook: <c>ORDER_DEPTH</c> events, each a full snapshot, plus <c>info</c> keep-alives.
    /// The Go SDK sends the orderbook's trading page as <c>Referer</c> (<see cref="OrderDepthRefererPath"/>).
    /// </summary>
    public static readonly AvanzaRoute OrderDepthStream = new(
        "order-depth-stream", "GET", "/_push/order-depth-web-push/{0}", DtoTier.A, false, GoSdk + "/market/service.go");

    /// <summary>Page <c>Referer</c> path for <see cref="OrderDepthStream"/> (Go SDK <c>SubscribeToOrderDepth</c>).</summary>
    public static string OrderDepthRefererPath(OrderbookId id) =>
        OrderDepthStream.Path(id).Length > 0 ? "/handla/order.html/kop/" + id.Value : throw new ArgumentException("Invalid orderbook id.", nameof(id));

    /// <summary>Every route, for architecture tests and the probe.</summary>
    public static IReadOnlyList<AvanzaRoute> All { get; } =
    [
        UserCredentials, Totp, SessionInfo, StartPage, BankIdStart, BankIdRestart, BankIdCollect, BankIdLogin, TradingPage,
        LoginRedirect, AccountsOverview, TradingAccounts, Positions, Orders, Deals,
        Orderbook, MarketData, Search, PriceChart, StockDetails, Transactions, OrderDepthStream,
    ];
}

/// <summary>
/// Order entry (ADR 0003, Phase 6). <b>Internal</b>, and referenced only by <c>AvanzaOrderChannel</c> (an IL-scanning
/// architecture test checks both). In Phase 6 nothing sends to these routes: the channel is tested against fixtures and
/// the gateway accepts only simulated channels. The bodies are <b>provisional</b> until the owner captures a real
/// web-app order, including the unresolved sell-side <c>profit</c> field (Qluxzz issue #156), before Phase 7.
/// Sources re-read 2026-09-26: Qluxzz <c>constants.py</c> and <c>avanza.py</c> (place/delete moved on 2026-09-21,
/// PR #164; modify from the 2025-02 fix, #131) and the Go SDK <c>trading/types.go</c> (same response shape).
/// </summary>
internal static class AvanzaOrderRoutes
{
    private const string Qluxzz = "https://github.com/Qluxzz/avanza/blob/a6a18a948f88cb7e340051e480b203b2ee917eed/avanza/constants.py";

    /// <summary>Body (Qluxzz <c>place_order</c>): {accountId, orderbookId, side, condition, price, validUntil, volume}.</summary>
    public static readonly AvanzaRoute Place = new(
        "order.place", "POST", "/_api/trading/order-entry/order/new", DtoTier.A, false, Qluxzz);

    /// <summary>Body (Qluxzz <c>delete_order</c>): {accountId, orderId}.</summary>
    public static readonly AvanzaRoute Delete = new(
        "order.delete", "POST", "/_api/trading/order-entry/order/delete", DtoTier.A, false, Qluxzz);

    /// <summary>
    /// Body (Qluxzz <c>edit_order</c>): {accountId, metadata: {orderEntryMode: "STANDARD"}, openVolume: null, orderId,
    /// price, validUntil, volume}. It may have moved with place/delete; unverified.
    /// </summary>
    public static readonly AvanzaRoute Modify = new(
        "order.modify", "POST", "/_api/trading-critical/rest/order/modify", DtoTier.A, false, Qluxzz);

    public static IReadOnlyList<AvanzaRoute> All { get; } = [Place, Delete, Modify];
}

/// <summary>
/// Avanza's pre-trade checks (ADR 0003 §2 <c>BrokerPreflight</c>; Phase 7 step 1): order validation and the preliminary
/// fee. Read-only POSTs that place nothing; the web app calls them while you fill in an order. <b>Internal</b>, and
/// referenced only by <c>AvanzaPreflight</c> (an IL-scanning architecture test checks it). They are not order routes and
/// are not in <see cref="AvanzaRoutes.All"/>. Source: the Go SDK <c>trading/service.go</c> <c>ValidateOrder</c> and
/// <c>GetPreliminaryFee</c> @ 43f39025 (re-checked 2026-09-26: no newer commit; Qluxzz has neither route). The DTOs are
/// <b>provisional</b> until a real answer is recorded (<c>qa probe --preflight</c> or the owner's web-app capture).
/// </summary>
internal static class AvanzaPreflightRoutes
{
    private const string GoSdk = "https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca/trading/service.go";

    /// <summary>Body: the order (Go <c>ValidateOrderRequest</c>) ⇒ one <c>{valid}</c> per named check.</summary>
    public static readonly AvanzaRoute Validate = new(
        "preflight.validate", "POST", "/_api/trading-critical/rest/order/validation/validate", DtoTier.A, false, GoSdk);

    /// <summary>Body: {accountId, orderbookId, price, volume, side}, all strings ⇒ commission, fees and totals as strings.</summary>
    public static readonly AvanzaRoute PreliminaryFee = new(
        "preflight.fee", "POST", "/_api/trading/preliminary-fee/preliminaryfee", DtoTier.A, false, GoSdk);

    public static IReadOnlyList<AvanzaRoute> All { get; } = [Validate, PreliminaryFee];
}
