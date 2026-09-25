# Avanza web API: endpoint research

- **Researched:** 2026-09-25. Refresh this file if it is more than 7 days old before you change any gateway code (Phase 3 rule).
- **Method:** I cloned the maintained open-source clients and read their source. I made **no** calls to Avanza. The research container's egress proxy blocks `www.avanza.se`, so nothing here was checked against the live site.
- **Status:** Avanza has no official public API. Everything below was reverse-engineered by third parties from Avanza's own web app, and any of it can change without notice.

## Sources (pin these when you re-read)

| Client | Language | Commit read | Date | Notes |
|---|---|---|---|---|
| [Qluxzz/avanza](https://github.com/Qluxzz/avanza/tree/a6a18a948f88cb7e340051e480b203b2ee917eed) | Python | `a6a18a948f88cb7e340051e480b203b2ee917eed` | 2026-09-21 | Main reference. Username + password + TOTP login. Routes live in [`avanza/constants.py`](https://github.com/Qluxzz/avanza/blob/a6a18a948f88cb7e340051e480b203b2ee917eed/avanza/constants.py). Its live test suite covers **read** endpoints only. |
| [vmorsell/avanza-sdk-go](https://github.com/vmorsell/avanza-sdk-go/tree/43f39025751c05ff73a85e708dadee4bfa9da2ca) | Go | `43f39025751c05ff73a85e708dadee4bfa9da2ca` | 2026-07-05 | Only client that documents the **SSE** push streams. Also covers order validation, preliminary fees, tick-size tables, and a schema-drift test. **BankID-only login.** |
| [fhqvst/avanza](https://github.com/fhqvst/avanza/tree/858772175db425fe594c4110f98f8af991e7c9b3) | JS | `858772175db425fe594c4110f98f8af991e7c9b3` | 2023-08-26 | Stale. Its `_mobile/*` paths and CometD socket are obsolete. Historical reference only. |
| [AnteWall/avanza-mcp](https://github.com/AnteWall/avanza-mcp) | – | not read | updated 2026-09-25 | MCP server. Found but not reviewed. |

Base URL for everything: `https://www.avanza.se`.

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
- BankID uses `/_api/authentication/v2/sessions/bankid[/collect|/restart]` (Go SDK). It needs a human, so we only document it and do not automate it.
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
| Off-hours price | `GET /_push/market-offhours-price/latest/{id}` | Go | Public. |
| Order validation (pre-flight) | `POST /_api/trading-critical/rest/order/validation/validate` | Go | Returns `commissionWarning`, `orderValueLimitWarning`, `priceRampingWarning`, `largeInScaleWarning`… each `{valid: bool}`. Read-only pre-trade check; see ADR 0003. |
| Preliminary fee | `POST /_api/trading/preliminary-fee/preliminaryfee` | Go | Body `{accountId, orderbookId, price, volume, side}` as strings. Returns `commission`, `marketFees`, `totalFees`, `totalSum`, `currencyExchangeFee{rate,sum}`. **Gets the real courtage for an order, so the class need not be hard-coded.** |
| Session info | `GET /_api/authentication/session/info/session` | Go | Health check (§1). |

**No login needed:** the Go SDK README says search, stock/certificate/warrant info, quote, order depth, market place, price chart, off-hours price, news and forum work without a session. That lets the chart importer and much of Paper-mode data run **without** credentials. The ToS question in `avanza-terms.md` still applies.

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

## 5. Streaming (push)

**The old CometD/Bayeux websocket (`wss://www.avanza.se/_push/cometd`) is discontinued.** Qluxzz removed it in [PR #151, commit `75c4f62`](https://github.com/Qluxzz/avanza/commit/75c4f6207d74b488a67df3c48ad1989dcd77496b) on 2025-11-26 ("Remove discontinued web socket support"). It had channels `quotes`, `orderdepths`, `trades`, `brokertradesummary`, `positions`, `orders`, `deals`, `accounts`.

Its replacement is **Server-Sent Events** (Go SDK, `internal/sse/subscription.go`):
- `GET` with `Accept: text/event-stream`, the session cookies, `X-SecurityToken`, `aza-do-not-touch-session: true`, and a page `Referer`. No HTTP timeout.
- Standard SSE framing (`event:`, `data:`, `id:`, `retry:`). On reconnect it sends **`Last-Event-ID`** and honours the server's `retry` interval. Backoff is `base·2^min(n,5)`, capped at 30 s. On 4xx except 408/429 it stops: unrecoverable, re-auth needed. Single events can exceed 64 KB, and the SDK allows 1 MB lines.

| Stream | Path | Event name | Payload |
|---|---|---|---|
| Order depth (per orderbook) | `/_push/order-depth-web-push/{orderbookId}` | `ORDER_DEPTH` | `{orderbookId, levels[{buySide,sellSide}{price,volume,priceString}], marketMakerLevelInBid/Ask}` |
| Own orders | `/_push/trading/orders/` | `ORDER` | `{id, accountId, orderbook, currentVolume, originalVolume, price, type(side), state{…}, action(NEW/DELETED/…), sum, orderDateTime, eventTimeStamp, uniqueId, detailedCancelStatus}` |
| Own stop-losses | `/_push/trading/stoploss/` | `STOPLOSS` | stop-loss state |

**Gaps compared with the kickoff prompt:**
- No known SSE channel exists for **last-trade quotes, public trades, own deals, or positions**.
- **Quote stream:** combine the best bid/ask from `ORDER_DEPTH` with polling `marketdata/{id}` for `last`/`timeOfLast`/volume, at a conservative interval of about 5 s per instrument during market hours. The staleness rule measures age from the newest of `ORDER_DEPTH` and the poll's `updated`.
- **Own deals and positions:** poll `deals` and `positions` (every 30–60 s and on every `ORDER` event that implies a fill) for reconciliation.

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
