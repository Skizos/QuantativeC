# Avanza web API: endpoint research

- **Researched:** 2026-09-25. Refresh this file if it is more than 7 days old before you change any gateway code (Phase 3 rule).
- **Method:** I cloned the maintained open-source clients and read their source. I made **no** calls to Avanza. The research container's egress proxy blocks `www.avanza.se`, so nothing here was checked against the live site.
- **Status:** Avanza has no official public API. Everything below was reverse-engineered by third parties from Avanza's own web app, and any of it can change without notice.

## Sources (pin these when you re-read)

| Client | Language | Commit read | Date | Notes |
|---|---|---|---|---|
| [Qluxzz/avanza](https://github.com/Qluxzz/avanza/tree/a6a18a948f88cb7e340051e480b203b2ee917eed) | Python | `a6a18a948f88cb7e340051e480b203b2ee917eed` | 2026-09-21 | Main reference. Username + password + TOTP login. Routes live in [`avanza/constants.py`](https://github.com/Qluxzz/avanza/blob/a6a18a948f88cb7e340051e480b203b2ee917eed/avanza/constants.py). Its live test suite covers **read** endpoints only. |
| [vmorsell/avanza-sdk-go](https://github.com/vmorsell/avanza-sdk-go/tree/43f39025751c05ff73a85e708dadee4bfa9da2ca) | Go | `43f39025751c05ff73a85e708dadee4bfa9da2ca` | 2026-07-05 | Only client that documents the **SSE** push streams. Also covers order validation, preliminary fees, tick-size tables, and a schema-drift test. **BankID-only login** (our BankID flow follows it). |
| [fhqvst/avanza](https://github.com/fhqvst/avanza/tree/858772175db425fe594c4110f98f8af991e7c9b3) | JS | `858772175db425fe594c4110f98f8af991e7c9b3` | 2023-08-26 | Stale. Its `_mobile/*` paths and CometD socket are obsolete. Historical reference only. |
| [AnteWall/avanza-mcp](https://github.com/AnteWall/avanza-mcp) | – | not read | updated 2026-09-25 | MCP server. Found but not reviewed. |

Base URL for everything: `https://www.avanza.se`.

**Re-checked 2026-09-28 (share search in the app, `docs/plans/15-share-search.md`):** the Qluxzz commit list still ends
at `a6a18a9` (2026-09-21). Its [`constants.py`](https://github.com/Qluxzz/avanza/blob/a6a18a948f88cb7e340051e480b203b2ee917eed/avanza/constants.py)
still has `INSTRUMENT_SEARCH_PATH = "/_api/search/filtered-search"`. The Go SDK's
[`market/types.go`](https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca/market/types.go)
`SearchHit` (`type`, `title`, `description`, `flagCode`, `orderBookId`, `urlSlugName`, `tradeable`, `sellable`,
`buyable`, `price`, `stockSectors`, `fundTags`, `marketPlaceName`, `subType`), `SearchHitPrice` (`last`, `currency`,
`todayChangePercent`, `todayChangeValue`, `todayChangeDirection`, `threeMonthsAgoChangePercent`,
`threeMonthsAgoChangeDirection`, `spread`; strings) and `StockSector` (`id`, `level`, `name`, `englishName`) match
our live recording of 2026-09-25 field for field. The app's search uses the same route and body as `qa history
import`; it only maps more of the answer: the ticker (in the title's last parentheses, e.g. `Ericsson B (ERIC B)`, as
recorded live), `flagCode`, `todayChangePercent` and the level-1 sector's `englishName`. No new endpoint.

**Re-checked 2026-09-26 (Phase 7 step 1, pre-trade checks):** still no newer commits (Qluxzz `a6a18a9`, avanza-sdk-go `43f3902`). Qluxzz `constants.py` has **no** validate or preliminary-fee route. The two routes and their shapes were re-read from the Go SDK: [`trading/service.go`](https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca/trading/service.go) (`ValidateOrder`, `GetPreliminaryFee`: POST, non-200 is an error) and [`trading/types.go`](https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca/trading/types.go):
- **Validate request** (`ValidateOrderRequest`): `isDividendReinvestment`, `requestId` (nullable), `orderRequestParameters`, `price` (number), `volume` (number), `openVolume`, `accountId`, `side` (`BUY`/`SELL`), `orderbookId`, `validUntil`, `metadata`, `condition` (`NORMAL`/`FILL_OR_KILL`), `isin`, `currency`, `marketPlace`.
- **Validate response** (`ValidateOrderResponse`): `commissionWarning`, `employeeValidation`, `largeInScaleWarning`, `orderValueLimitWarning`, `priceRampingWarning`, `canadaOddLotWarning`, each `{valid: bool}`.
- **Fee request** (`PreliminaryFeeRequest`): `accountId`, `orderbookId`, `price`, `volume` (all strings), `side`.
- **Fee response** (`PreliminaryFeeResponse`): `commission`, `marketFees`, `totalFees`, `totalSum`, `totalSumWithoutFees`, `orderbookCurrency` (strings in the orderbook currency), `transactionTax` and `campaign` (nullable strings), `currencyExchangeFee{rate, sum}`.
- **No real answer exists in either client:** the SDK's tests echo its own structs. Our DTOs are therefore provisional. `qa probe --preflight` records the real answers for a hypothetical 1-share buy, without placing anything.

**Re-checked 2026-09-26 (Phase 6):** the GitHub commit lists of both clients show no commits after the pins above (Qluxzz newest is still `a6a18a9`, 2026-09-21; avanza-sdk-go newest is still `43f3902`, 2026-07-05). §4 was re-read from those commits: `avanza/avanza.py` `place_order`/`edit_order`/`delete_order` and `constants.py`, and avanza-sdk-go `trading/types.go`.

## 1. Authentication (username + password + TOTP)

Source: Qluxzz `avanza/avanza.py` (`__authenticate`, `__validate_2fa`) and `avanza/credentials.py`.

1. `POST /_api/authentication/sessions/usercredentials`
   Body: `{"maxInactiveMinutes": <30..1440>, "username": "...", "password": "..."}`.
   - If the response has no `twoFactorLogin`, login is complete. The security token is in the `X-SecurityToken` **response header** and the session is in `body.successfulLogin`.
   - Otherwise `body.twoFactorLogin.method` must be `"TOTP"`. Any other method is unsupported, so we stop.
2. `POST /_api/authentication/sessions/totp`
   Body: `{"method": "TOTP", "totpCode": "123456"}`.
   Response: `X-SecurityToken` header plus a body with `authenticationSession`, `pushSubscriptionId`, `customerId`.
3. Every later request sends the **session cookies** set by these calls and the header `X-SecurityToken: <token>`.

TOTP parameters: the Python client uses `pyotp.TOTP(secret, digest=sha1)` with pyotp's defaults, which are **SHA-1, 30 s step, 6 digits, Base32 secret** (RFC 6238 defaults). The Phase 3 TOTP generator must pass the RFC 6238 Appendix B SHA-1 vectors.

Session lifetime: `maxInactiveMinutes` is set by the client and validated to **30–1440 minutes** (`MIN_INACTIVE_MINUTES`/`MAX_INACTIVE_MINUTES`). This is an **inactivity** timeout. No absolute maximum session age is documented.

**Quirks and warnings**
- The Python client **retries login once with the next OTP window after a 401**, enabled by default via `retry_with_next_otp=True`. **We must not copy this.** CLAUDE.md allows one login attempt per trigger. Repeated failures lock username/password login (Part 0 §3).
- There are two ways to find the security token:
  - Qluxzz reads the `X-SecurityToken` response header.
  - The Go SDK takes it from the **`AZACSRF` cookie** and sends that cookie's value as `X-SecurityToken` (`client/client.go`, `extractCookies`).
  - The authenticator should support both, preferring the header and falling back to the cookie, and record which one it used. If both are missing, raise `SchemaDriftException`.
- Session introspection: `GET /_api/authentication/session/info/session` returns `user.loggedIn`, `user.pushSubscriptionId`, `user.securityToken`, `user.id` (Go SDK `auth/auth.go`). This is a cheap read-only **session health check**, useful for the drift canary and for `SessionState`.
- **BankID login** (implemented 2026-09-25 as the default login; source: [Go SDK `auth/auth.go` @ `43f39025`](https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca/auth/auth.go), HEAD re-checked the same day). A human approves every login; we only draw the QR code.
  1. `GET /` for the initial cookies (`AZAPERSISTENCE` …).
  2. `POST /_api/authentication/v2/sessions/bankid` with `{"method":"QR_START","returnScheme":"NULL"}`. The response is 200/202 `{transactionId, expires, qrToken}`.
  3. Every second:
     - `POST …/bankid/collect` `{}`. The response is `{state: OUTSTANDING_TRANSACTION|COMPLETE|FAILED, hintCode, logins[], name, identificationNumber, …}`. `name` and `identificationNumber` are personal data: we never read them.
     - While the state is pending, `POST …/bankid/restart` `{}` returns a fresh `qrToken` (animated QR).
  4. On COMPLETE:
     - `GET logins[0].loginPath`, e.g. `/_api/authentication/v2/sessions/bankid/{tx}/{customerId}`. We validate it and follow same-origin redirects manually.
     - `GET /handla/order.html` for the remaining cookies.
     - `GET session/info/session` to verify.
  5. The security token is the `AZACSRF` cookie, sent as `X-SecurityToken`.
- Open questions with no source that answers them:
  - How many failed password/TOTP attempts trigger the lockout.
  - Whether the inactivity timeout is extended by every request or only some. The Go SDK sends `aza-do-not-touch-session: true` on SSE requests, which suggests streams deliberately do **not** extend the session.

## 2. Headers

From the Go SDK `client.setHeaders`:
- `Accept: application/json, text/plain, */*`
- `Content-Type: application/json;charset=UTF-8`
- `Origin: https://www.avanza.se`
- `Referer`: the Go SDK sends `https://www.avanza.se/logga-in.html` on REST calls and a page-specific referer on SSE calls.
- `User-Agent`: browser-like.
- `X-SecurityToken`
- `Cookie`

The Python client sends only `X-SecurityToken`, plus the cookies from its `requests.Session`. The minimal set evidently works for reads.

Rate limits: nothing is documented. The Go SDK default is **one request per 100 ms**, global and serialized. Our default should be more conservative: a token bucket of about 2 req/s with burst 5, and order endpoints limited separately by the risk engine.

## 3. Read endpoints needed for Phases 3–4

| Purpose | Method + path | Source | Notes |
|---|---|---|---|
| Accounts overview | `GET /_api/account-overview/overview/categorizedAccounts` | Py, Go | Accounts with ids, types, and scrambled `urlParameterId`. |
| Trading accounts | `GET /_api/trading-critical/rest/accounts` | Go | Includes available-for-purchase. Needed for the cash check. |
| Positions (all accounts) | `GET /_api/position-data/positions` | Py | |
| Positions (one account) | `GET /_api/position-data/positions/{urlParameterId}` | Go | Needs the scrambled id, not the account id. |
| Open orders | `GET /_api/trading/rest/orders` | Py, Go | Response `{orders[], fundOrders[], cancelledOrders[]}` (Go). |
| Single order | `GET /_api/trading-critical/rest/order/find?orderId={}&cAccountId={}` | Py, Go | Changed 2025-11 (Qluxzz PR #147). |
| Deals (fills) | `GET /_api/trading/rest/deals` | Py | Used for reconciliation. |
| Transactions | `GET /_api/transactions/list?...` | Py, Go | Path changed 2025-06 (Qluxzz #139). |
| Search | `POST /_api/search/filtered-search` | Py, Go | Body `{"query","searchFilter":{"types":["STOCK"]},"pagination":{"from","size"}}`, result `.hits`. Changed GET→POST in 2024-09. |
| Orderbook parameters | `GET /_api/trading-critical/rest/orderbook/{orderbookId}` | Py, Go | Returns **`tickSizeList.tickSizeEntries[{min,max,tick}]`**, `volumeFactor`, `tradingUnit`, `minValidUntil`/`maxValidUntil`, `orderbookStatus`, `marketPlace`, `isin`, `currency`, and `featureSupport` (stopLoss, fillAndOrKill, …). **This is the authoritative per-instrument tick table.** |
| Market data snapshot | `GET /_api/trading-critical/rest/marketdata/{orderbookId}` | Py, Go | `quote{buy,sell,last,highest,lowest,timeOfLast,updated,totalVolumeTraded,vwap}` + `orderDepth{receivedTime,levels[{buySide,sellSide}]}` + `trades`. Empty sides are formatted `"0.00"`, so volumes must decode as decimal. |
| Stock info | `GET /_api/market-guide/stock/{id}` (+ `/details`, `/quote`, `/orderdepth`, `/marketplace`) | Py, Go | The Go SDK marks these **public** (no session needed). |
| Price chart | `GET /_api/price-chart/stock/{id}?timePeriod=...&resolution=...` | Py, Go | OHLC `{timestamp(ms),open,high,low,close,totalVolumeTraded}`. Periods `today…infinity`, resolutions `minute…quarter`. **Public.** |
| Price chart, intraday (2026-09-29, plan 17) | same route | Py, Go | Resolutions `minute`, `two_minutes`, `five_minutes`, `ten_minutes`, `thirty_minutes`, `hour`, `day`, `week`, `month`, `quarter`, sent lower-case (Qluxzz [`a6a18a94` `constants.py`](https://github.com/Qluxzz/avanza/blob/a6a18a948f88cb7e340051e480b203b2ee917eed/avanza/constants.py) `Resolution`; `get_chart_data` lower-cases both parameters). The server picks what it allows per period and says so in `metadata.resolution.{chartResolution, availableResolutions}` (Go SDK [`43f39025` `market/types.go`](https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca/market/types.go)). Our recording of `one_month` allows only `hour`, `day`, `week`. **The owner's probe (`qa intraday probe ERIC-B`, 2026-09-29 23:24 Stockholm, public, no login)** answered which periods give minute bars (see the table below): **only `today`**. |
| Off-hours price | `GET /_push/market-offhours-price/latest/{id}` | Go | Public. |
| Order validation (pre-flight) | `POST /_api/trading-critical/rest/order/validation/validate` | Go | Returns `commissionWarning`, `orderValueLimitWarning`, `priceRampingWarning`, `largeInScaleWarning`… each `{valid: bool}`. Read-only pre-trade check; see ADR 0003. |
| Preliminary fee | `POST /_api/trading/preliminary-fee/preliminaryfee` | Go | Body `{accountId, orderbookId, price, volume, side}` as strings. Returns `commission`, `marketFees`, `totalFees`, `totalSum`, `currencyExchangeFee{rate,sum}`. **Gets the real courtage for an order, so the class need not be hard-coded.** |
| Session info | `GET /_api/authentication/session/info/session` | Go | Health check (§1). |

**Intraday probe, 2026-09-29** (owner, ERIC B, orderbook 5240; asked after the close, at 23:24 Stockholm):

| period | asked | answered | bars | days | offers (`availableResolutions`) |
|---|---|---|---|---|---|
| `today` | (its own) | `minute` | 475 | 1 (09:00–17:29) | minute, two_minutes, five_minutes, ten_minutes, thirty_minutes, hour, day |
| `today` | `five_minutes` | `five_minutes` | 102 | 1 (09:00–17:25) | the same |
| `one_week` | (its own) | `ten_minutes` | 306 | 6 | ten_minutes, thirty_minutes, hour, day |
| `one_month` | (its own) | `hour` | 198 | 22 | hour, day, week |
| `three_months` | (its own) | `day` | 67 | 67 | day, week, month |

- 1- and 5-minute bars come **only for the current day**: a day not collected by its evening is lost at those
  resolutions (a week back gives 10-minute bars at best).
- Asked late in the evening, `today` still returned the whole day, so the evening import works.
- A minute with no trade has no bar (475 of 510 minutes for ERIC B); every 5-minute slot was there (102, the last
  one the closing auction's 17:25).
- **Used by the catch-up (plan 17 A2b, 2026-09-29):** `GET /_api/price-chart/stock/{id}?timePeriod=one_week&resolution=ten_minutes`.
  It is the same route, and `one_week` and `ten_minutes` are both in Qluxzz
  [`a6a18a94` `constants.py`](https://github.com/Qluxzz/avanza/blob/a6a18a948f88cb7e340051e480b203b2ee917eed/avanza/constants.py)
  (`TimePeriod.ONE_WEEK`, `Resolution.TEN_MINUTES`, `CHARTDATA_PATH = "/_api/price-chart/stock/{}"`; fetched again
  2026-09-29 before building the catch-up). The probe saw the server offer it. Called once per share only
  when a trading day of the last week has no 5- or 10-minute bars. The answer must be `ten_minutes`, as for the
  other resolutions.

**No login needed:** the Go SDK README says search, stock/certificate/warrant info, quote, order depth, market place, price chart, off-hours price, news and forum work without a session. That lets the chart importer and much of Paper-mode data run **without** credentials. The ToS question in `avanza-terms.md` still applies.

## 3a. Observed live (owner's first read-only probe, 2026-09-25, BankID login)

This is the first contact with the real API. The list below has **field names only**; the sanitized recordings will pin down their types.

| Route | Result | Differences from the reference clients |
|---|---|---|
| BankID login, session info | OK | none |
| `account-overview/…/categorizedAccounts` | drift | new per-account fields: `interestRates`, `creditAccountClearingAccountNumber`, `autoDistribution` |
| `trading-critical/rest/accounts` | drift | new field: `isDiscretionaryAccount` |
| `position-data/positions`, `trading/rest/orders`, `transactions/list` | OK | none in Tier A. Transactions is Tier B, and its warnings weren't captured. |
| `trading/rest/deals` | recorded | 27 bytes (no fills yet) |
| `trading-critical/rest/orderbook/{id}` | drift | `orderbookStatus` is **absent** (it was required in the Go model) |
| marketdata, price chart | not reached | the probe used to stop at an orderbook failure; it now continues with the search hit |

**Second run and sanitized recording** (`recordings/fixtures/avanza/2026-09-25/`, parsed strictly on every build by `RecordedFixtureTests`):
- **Account fields:** `autoDistribution` and `isDiscretionaryAccount` are booleans. `interestRates` is `{currency: {deposit, loan}}` of value objects. `creditAccountClearingAccountNumber` was null everywhere.
- **Account names:** `name.defaultName` is the account number, and the sanitizer replaces it.
- **Timestamps:** marketdata `quote.timeOfLast`/`updated` are ISO **without offset**, in **Europe/Stockholm local time**. Proof: `timeOfLast` "17:29:40" equals `orderDepth.receivedTime` and `trades[].dealTime` (epoch ms) of 15:29:40Z. Transaction `date` is `yyyy-MM-ddT00:00:00`.
- **Search prices** are Swedish-formatted strings (`"94,96"`). The search response also echoes `searchFilter`.
- **Deals:** `{"deals": [], "fundDeals": []}`. The element fields are still unknown until the first fill.
- **ERIC B orderbook:** 17 tick bands (0.02 at 50–99.98 SEK).
- **BankID:**
  - `GET /` answers **302** plus `AZAPERSISTENCE`.
  - Start returns 202 plus an `AZABANKIDTRANSID` cookie.
  - Pending collect carries `hint` (not `hintCode`), `rfa` and `state`.
  - **The login path sets `AZACSRF`** (plus `csid`, `cstoken`) and `X-SecurityToken`. The trading page sets no cookie, although the reference client visits it; we keep that visit.

## 4. Order endpoints (Phase 6 fixtures only; Claude never calls them)

| Purpose | Method + path | Source / date | Body |
|---|---|---|---|
| Place | `POST /_api/trading/order-entry/order/new` | Qluxzz PR #164, **2026-09-21** (the old path returned 404, issue #163) | Qluxzz: `{accountId, orderbookId, side, condition, price, validUntil, volume}`. Go SDK adds `requestId` (client UUID), `isDividendReinvestment`, `orderRequestParameters`, `openVolume`, `metadata{orderEntryMode, hasTouchedPrice}`. |
| Delete | `POST /_api/trading/order-entry/order/delete` | Qluxzz PR #164, 2026-09-21 | `{accountId, orderId}` |
| Modify | `POST /_api/trading-critical/rest/order/modify` | Qluxzz (2025-02 fix #131), Go | `{orderId, accountId, price, volume, openVolume, validUntil, metadata{orderEntryMode:"STANDARD"}}`. **May have moved with place/delete.** Unverified. |
| Response (all three) | – | – | `{orderRequestStatus: "SUCCESS"|"ERROR", message, parameters[], orderId}` |
| Stop-loss new/modify/delete/list | `POST /_api/trading/stoploss/new`, `POST …/stoploss/modify`, `DELETE …/stoploss/{accountId}/{id}`, `GET …/stoploss` | Py, Go | Uses `orderBookId` with a capital B, unlike the other endpoints. |

**Conflicts to resolve before Phase 6**
1. **Place/delete path:** the Go SDK (2026-07) still uses `/_api/trading-critical/rest/order/{new,delete}`. Qluxzz (2026-09-21) moved to `/_api/trading/order-entry/order/{new,delete}` after a 404. Trust the newer one. The routes file needs a version tag, and the drift canary must probe this path. A read-only probe is not possible for order endpoints, so the canary can only check that the paths used by `validate`/`preliminaryfee` still answer.
2. **`requestId`:** the Go SDK sends a fresh UUID per order. It is **unknown** whether Avanza de-duplicates on it. **Do not rely on it for idempotency.** OMS idempotency stays client-side: a client order key plus reconciliation, and never an automatic POST retry.
3. **`profit` field:** Qluxzz issue #156 (2026-05-28) reports Avanza "complains about the missing param `profit`" on **sell** orders. It is unresolved and the format is unknown. This must be captured from a real web-app sell before Phase 7.
4. **Price type:** both clients send `price` as a JSON float. We will serialize a `decimal`, rounded to tick, with invariant culture, and never a binary float string.

**Implemented 2026-09-26 (Phase 6 step 5, fixture-tested only).** Re-read before writing the code:
- the commit pages of both clients: HEADs are still `a6a18a9` (Qluxzz, 2026-09-21) and `43f3902` (Go SDK, 2026-07-05)
- the raw sources at those commits:
  - [Qluxzz `avanza/constants.py`](https://github.com/Qluxzz/avanza/blob/a6a18a948f88cb7e340051e480b203b2ee917eed/avanza/constants.py): `ORDER_PLACE_PATH`, `ORDER_DELETE_PATH`, `ORDER_EDIT_PATH`
  - [Qluxzz `avanza/avanza.py`](https://github.com/Qluxzz/avanza/blob/a6a18a948f88cb7e340051e480b203b2ee917eed/avanza/avanza.py): `place_order`, `delete_order`, `edit_order` bodies
  - [Go `trading/types.go`](https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca/trading/types.go): the `Place/Delete/ModifyOrderResponse` shape

Decisions:
- **Routes:** the three routes live in the internal `AvanzaOrderRoutes`, in the same routes file (routes version `2026-09-26.1`).
- **Request bodies:** we send **Qluxzz's** bodies, because they are the ones confirmed against the new place/delete path. We send no `requestId` (conflict 2).
  - The Go SDK's extra fields stay out until a captured web-app order shows which of them the new path expects.
- **Responses:** Tier A, strict. An unknown field or an unknown `orderRequestStatus` counts as drift: the result is Unknown and the gateway halts.
- **Status codes:**

  | Answer | Outcome |
  |---|---|
  | 404 | Unknown, and the endpoint is flagged as gone |
  | 401 / 403 | Unknown, and the session is flagged as expired |
  | Timeout, transport error, 408, 5xx, redirect | Unknown |
  | Any other 4xx | Rejected |

- **One attempt only:** every request is sent once, through a pipeline with no retry handler and no recorder.

## 5. Streaming (push)

**The old CometD/Bayeux websocket (`wss://www.avanza.se/_push/cometd`) is discontinued.** Qluxzz removed it in [PR #151, commit `75c4f62`](https://github.com/Qluxzz/avanza/commit/75c4f6207d74b488a67df3c48ad1989dcd77496b) on 2025-11-26 ("Remove discontinued web socket support"). It had channels `quotes`, `orderdepths`, `trades`, `brokertradesummary`, `positions`, `orders`, `deals`, `accounts`.

Its replacement is **Server-Sent Events** (Go SDK, `internal/sse/subscription.go`):
- `GET` with `Accept: text/event-stream`, the session cookies, `X-SecurityToken`, `aza-do-not-touch-session: true`, and a page `Referer`. No HTTP timeout.
- Standard SSE framing (`event:`, `data:`, `id:`, `retry:`). On reconnect it sends **`Last-Event-ID`** and honours the server's `retry` interval. Backoff is `base·2^min(n,5)`, capped at 30 s. On 4xx except 408/429 it stops: unrecoverable, re-auth needed. Single events can exceed 64 KB, and the SDK allows 1 MB lines.

| Stream | Path | Event name | Payload |
|---|---|---|---|
| Order depth (per orderbook) | `/_push/order-depth-web-push/{orderbookId}` (Referer `https://www.avanza.se/handla/order.html/kop/{orderbookId}`; needs cookies `csid`, `cstoken`, `AZACSRF`) | `ORDER_DEPTH` | `{orderbookId (string), levels[{buyPrice, buyVolume, sellPrice, sellVolume}], marketMakerLevelInAsk, marketMakerLevelInBid}`, a **full snapshot** per event ([`market/types.go`](https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca/market/types.go), [`market/service.go`](https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca/market/service.go)). Corrected 2026-09-25: the nested `{buySide, sellSide}` shape listed here before is the **marketdata** REST shape. |
| Own orders | `/_push/trading/orders/` | `ORDER` | `{id, accountId, orderbook, currentVolume, originalVolume, price, type(side), state{…}, action(NEW/DELETED/…), sum, orderDateTime, eventTimeStamp, uniqueId, detailedCancelStatus}` |
| Own stop-losses | `/_push/trading/stoploss/` | `STOPLOSS` | stop-loss state |

**Gaps compared with the kickoff prompt:**
- No known SSE channel exists for **last-trade quotes, public trades, own deals, or positions**.
- **Quote stream:** combine the best bid/ask from `ORDER_DEPTH` with polling `marketdata/{id}` for `last`/`timeOfLast`/volume, at a conservative interval of about 5 s per instrument during market hours. The staleness rule measures age from the newest of `ORDER_DEPTH` and the poll's `updated`.
- **Own deals and positions:** poll `deals` and `positions` (every 30–60 s and on every `ORDER` event that implies a fill) for reconciliation.

- **Other events:** the Go SDK tests show `event: info` with plain-text data (`connected`, `heartbeat`) on the same stream ([`order_depth_test.go`](https://github.com/vmorsell/avanza-sdk-go/blob/43f39025751c05ff73a85e708dadee4bfa9da2ca/order_depth_test.go)).
- **Re-checked 2026-09-25 (Phase 4):** both client HEADs are unchanged (Qluxzz `a6a18a94`, Go SDK `43f39025`).

**First live use, 2026-09-30 (the owner's Paper session, 10:19): every order-depth connection was refused with HTTP 429**,
from the first attempt and on every retry, for all three shares (connections already about a second apart), while the
REST market-data polls worked. The stream has never been captured live; the only fixture is hand-written. Not yet known:
- whether it is a rate or concurrency limit (another stream open, e.g. Avanza in a browser)
- or a refusal of non-browser clients. The Go SDK (`internal/sse/subscription.go` at `43f39025`, fetched again
  2026-09-30) sends a full browser header set on SSE requests: `Accept-Language`, `Pragma`, `Priority`, `Sec-Ch-Ua*`,
  `Sec-Fetch-Dest/Mode/Site`, a browser `User-Agent`, and oddly `Content-Type: application/json`. We send our own
  `User-Agent` (`QuantAnalyst/0.3 …`) and none of those.

The owner's `qa stream ERIC-B` (one stream, no browser tab open, 10:38) was refused the same way at once. So it is not our
own connections adding up. **Decision (owner, 2026-09-30): Paper runs on the polls alone** (ADR 0002 §3 amendment);
the imitation was declined. Confirm and Auto still need the stream, so this must be solved before Phase 7's first live
day (asking Avanza, `avanza-terms.md` §3, is the clean way).

Since then a refusal's log line names the answering `Server` and any `Retry-After`, and `qa stream` (recording on by
default) keeps the first three refused answers: status, header names and the body. That tells a block page from a
rate limit. Imitating a browser would be the owner's decision: Avanza's user terms bar automated tools without written
consent (`avanza-terms.md`).

Open issue: [Qluxzz #140 "Event-stream (SSE)"](https://github.com/Qluxzz/avanza/issues/140) (2025-09-23) has no maintainer answer. The SSE protocol is known from the Go SDK only, so we need our own recorded fixtures (Phase 4) before relying on it.

## 6. Drift history (evidence for ADR 0002)

Breaking changes recorded in Qluxzz `constants.py` / `avanza.py` history (`git log -- avanza/constants.py`):

| Date | Change |
|---|---|
| 2024-06-25 | deals-and-orders 404, path changed (#105) |
| 2024-09-01 | search moved GET→POST (#114); watchlist method/path (#116) |
| 2024-12-10 | new stop-loss endpoints (#119) |
| 2025-01-20 | watchlist path (#124) |
| 2025-02-13 | edit order: new path, payload, HTTP method (#131) |
| 2025-06-04 | transactions path (#139) |
| 2025-11-26 | get_order changed (#147); **websocket discontinued** (#151) |
| 2026-03-28 | insights GET→POST (#154) |
| 2026-05-28 | `profit` param on sells reported (#156, unresolved) |
| 2026-09-21 | **order place/delete path changed** (#163/#164) |

That is roughly one breaking change every 2–3 months, including **three that touched order entry**. Schema-drift detection and fail-safe halting are required.

## 7. Implications for our design (summary; decisions go in ADRs)

- Keep one `AvanzaRoutes` file with a `RoutesVersion` constant and a URL to the source commit for every route.
- Authenticator: one attempt, no next-OTP retry, token from header or `AZACSRF` cookie, and `session/info` as the health probe.
- The **tick table comes from `orderbook/{id}`**, cached per instrument with a known-at timestamp. The RTS 11 table is only a cross-check (see `market-rules.md`).
- Courtage: use `preliminaryfee` in Confirm/Auto for the exact fee on the order card, and a local courtage model (`market-rules.md`) for backtests and Paper. Log any difference between the two as a warning.
- Pre-trade: call Avanza's own `validate` endpoint as an extra, non-authoritative check after our risk engine passes. Any `valid:false` is a hard reject.
- Streaming is SSE, not CometD. Quotes are order depth plus polling.
- The research container cannot reach avanza.se. **Phase 3 needs you to record sanitized fixtures** with the read-only CLI commands.
