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

    /// <summary>Transactions; query <c>from</c>, <c>to</c> (yyyy-MM-dd), <c>includeResult</c>.</summary>
    public static readonly AvanzaRoute Transactions = new(
        "transactions", "GET", "/_api/transactions/list", DtoTier.B, false, GoSdk + "/accounts/service.go");

    /// <summary>Every route, for architecture tests and the probe.</summary>
    public static IReadOnlyList<AvanzaRoute> All { get; } =
    [
        UserCredentials, Totp, SessionInfo, StartPage, BankIdStart, BankIdRestart, BankIdCollect, BankIdLogin, TradingPage,
        LoginRedirect, AccountsOverview, TradingAccounts, Positions, Orders, Deals,
        Orderbook, MarketData, Search, PriceChart, Transactions,
    ];
}
