# ADR 0002 — Avanza gateway and fail-safe design

- **Status:** Accepted (2026-09-25). The owner approved the two-port split and the tiered strictness ("split the broker", "Apply the strict"); see `docs/plans/03-phase3-avanza-read.md` for how that answer was read.
- **Related:** CLAUDE.md "Avanza gateway rules" / "Absolute safety rules", `docs/research/avanza-endpoints.md`, ADR 0003

## Context

Avanza has **no official API**. We reuse the private web API as reverse-engineered by open-source clients. The research shows:
- ~11 breaking changes in 27 months, three of them in order entry; the latest was 2026-09-21.
- The CometD websocket was **discontinued** (Nov 2025) and replaced by **SSE**, which has a smaller channel set.
- There are two ways to find the security token (`X-SecurityToken` header or `AZACSRF` cookie).
- Repeated failed username logins lock the login method.
- The website terms prohibit automated tools without written consent (`avanza-terms.md`).

The program must therefore:
- **contain** Avanza behind one boundary
- **detect** breakage quickly
- **fail safe**: stop new orders and never guess

## Decision

### 1. Two ports, three adapters
```csharp
public interface IBrokerGateway        // reads + streams; used by everything
{
    Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct);
    Task<IReadOnlyList<Position>> GetPositionsAsync(AccountId id, CancellationToken ct);
    Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(CancellationToken ct);
    Task<IReadOnlyList<BrokerDeal>> GetDealsAsync(CancellationToken ct);
    Task<InstrumentTradingParams> GetTradingParamsAsync(OrderbookId id, CancellationToken ct); // tick table, lot, validity
    Task<MarketSnapshot> GetMarketSnapshotAsync(OrderbookId id, CancellationToken ct);
    IAsyncEnumerable<OrderDepthUpdate> StreamOrderDepthAsync(OrderbookId id, CancellationToken ct);
    IAsyncEnumerable<OwnOrderEvent> StreamOwnOrdersAsync(CancellationToken ct);
    Task<PreTradeFee> GetPreliminaryFeeAsync(OrderDraft d, CancellationToken ct);   // read-only helper
    Task<BrokerValidation> ValidateOrderAsync(OrderDraft d, CancellationToken ct);  // read-only helper
    // search, chart history, transactions, session health …
}

public interface IBrokerOrderChannel   // place / modify / cancel ONLY
{
    Task<OrderSubmitOutcome> PlaceLimitOrderAsync(ApprovedOrder o, CancellationToken ct);
    Task<OrderSubmitOutcome> ModifyOrderAsync(ApprovedModify m, CancellationToken ct);
    Task<OrderSubmitOutcome> CancelOrderAsync(ApprovedCancel c, CancellationToken ct);
}
```
- **Adapters:** `AvanzaGateway`/`AvanzaOrderChannel` (live), `PaperGateway` (live reads + `PaperOrderChannel` with simulated fills), and `BacktestGateway`/`BacktestOrderChannel`.
- **Access control:**
  - `ApprovedOrder`/`ApprovedModify`/`ApprovedCancel` have **internal constructors** in `QuantAnalyst.Trading`, so only `OrderGateway` can create them, after risk and mode checks.
  - An architecture test (NetArchTest or ArchUnitNET) asserts that **only `QuantAnalyst.Trading.OrderGateway` depends on `IBrokerOrderChannel`**.
  - A second test asserts that nothing outside `QuantAnalyst.Avanza` references Avanza DTOs.
- **Strategy isolation:** strategy code sees neither interface. It emits `OrderIntent`s.

### 2. `QuantAnalyst.Avanza` internals
- **`AvanzaRoutes`:**
  - the single file with every path
  - `public const string RoutesVersion = "2026-09-25";`
  - each route has an XML-doc `<see href>` to the client commit it came from
  - order routes are `internal`, visible only to `AvanzaOrderChannel`
- **`AvanzaAuthenticator`** (two methods since 2026-09-25):
  - **BankID** is the owner's choice for now and the default. It runs one QR transaction per trigger that a human approves in the BankID app, and never starts a second transaction. Details: `docs/research/avanza-endpoints.md` §1. BankID failures don't count toward the lock below.
  - **TOTP** stays fully implemented and is selected with `--login totp` / `QA_AVANZA_LOGIN=totp`. Unattended Auto mode (Phase 8) needs it. Everything below describes TOTP.
  - **States:** `NotAuthenticated → Authenticating → Valid → Expired | Failed | Locked`.
  - **Flow:** `usercredentials` → (if `twoFactorLogin.method == "TOTP"`) `totp`.
    - Any other 2FA method goes to `Failed` ("unsupported 2FA").
    - The token comes from the `X-SecurityToken` header, else the `AZACSRF` cookie, else `SchemaDriftException`.
  - **One attempt per trigger, no next-OTP retry.**
    - A 401 on either step goes to `Failed`.
    - A response indicating lockout, or a second consecutive failure across triggers within 24 h, goes to `Locked`. `Locked` is persisted in `state/auth.json`; a human clears it with `qa login --clear-lock` after checking via BankID.
  - **Health check:** `GET session/info/session` checks `loggedIn`.
  - **Timeout:** `maxInactiveMinutes` is configured (default 60, allowed 30–1440).
  - **TOTP:** our own RFC 6238 (HMAC-SHA1, 30 s, 6 digits). The code is computed immediately before the POST and **never logged**. If fewer than 3 s remain in the window, the authenticator waits for the next window **before** the single attempt. That is not a retry.
- **`AvanzaHttpClient`**, built with `IHttpClientFactory`, two named clients:
  - **`avanza-read`:**
    - handlers, in order: `RecordingHandler` (optional, sanitizing) → `RateLimitHandler` (global token bucket 2 rps, burst 5) → resilience pipeline → `AuthHeaderHandler`
    - resilience pipeline: `Microsoft.Extensions.Http.Resilience` retry ×2 with jittered backoff on 408/429/5xx/transport errors, circuit breaker (50 % failures over 30 s, 60 s break), timeout 15 s
    - cookie container shared with the authenticator
  - **`avanza-order`:**
    - **no retry handler, no hedging.**
    - a 10 s timeout; on timeout or transport error the outcome is `Unknown`, never re-sent
    - uses the same rate limiter and additionally the order-action limiter (ADR 0003)
  - **Status handling:**
    - 401/403 → `SessionExpiredException` → `HaltController.Halt(SessionExpired)` + one re-login attempt (per CLAUDE.md), after which the halt is lifted only if `session/info` confirms `loggedIn`
    - 404 on a Tier-A route → `EndpointGoneException` → halt
- **DTOs and strictness:** `System.Text.Json` source-generated contexts with `required` members and `RespectNullableAnnotations`/`RespectRequiredConstructorParameters`. There are two tiers:
  - **Tier A (trading-critical):**
    - covers accounts, trading accounts, positions, open orders, single order, deals, orderbook (tick table), marketdata, order responses, SSE `ORDER` and `ORDER_DEPTH`
    - `JsonUnmappedMemberHandling.Disallow` + required members
    - **any** unknown or missing field → `SchemaDriftException(route, dtoVersion, path)` → `HaltController.Halt(SchemaDrift)`
  - **Tier B (informational):**
    - covers search, stock details, chart, news, transactions
    - missing required fields → `SchemaDriftException`, which disables the feature but does **not** halt trading unless the DTO feeds a risk check (chart history does not; the tick table is Tier A)
    - unknown extras are logged once per day as `drift.warning`
  - **Why tiers:** this is a refinement of CLAUDE.md's rule, approved 2026-09-25; CLAUDE.md now states the tiers. The master plan §2 item 8 explains the reasoning.
- **Mappers:** DTOs map to Core types in one place.
  - Prices are parsed as `decimal`.
  - Volumes that arrive as `"0.00"` are parsed as `decimal`, then validated to be integral.
  - Timestamps: epoch ms → `DateTimeOffset` UTC.
- **Recording:**
  - `RecordingHandler` writes request/response pairs to `recordings/live/` (git-ignored; Claude is denied read access in `.claude/settings.json`).
  - `qa recordings sanitize` produces `recordings/fixtures/`. It drops `Cookie`/`Set-Cookie`/`X-SecurityToken`/`Authorization`, replaces `customerId`/`pushSubscriptionId`/`authenticationSession`, masks account ids to `***123`, and replaces names.
  - A test fails if any fixture contains a value from the local secret store's test doubles or matches token/cookie patterns.

### 3. Streaming (SSE) and quotes
- **`AvanzaStreamClient`:**
  - one `HttpClient` (`avanza-stream`, infinite timeout) per stream
  - `Accept: text/event-stream`, `aza-do-not-touch-session: true`, page `Referer`, cookies + token
  - an SSE parser that handles `event`/`data`/`id`/`retry`, events up to 1 MB, and **`Last-Event-ID` on reconnect**
  - backoff `max(server retry, 3 s)·2^min(n,5)`, capped at 30 s, with jitter
  - a 4xx other than 408/429 → `SessionExpired`/`SchemaDrift` → halt (no reconnect loop)
- **Streams:**
  - `/_push/order-depth-web-push/{orderbookId}` for each instrument in the active universe (OMXS30 ⇒ ≤ 30 streams; this may need multiplexing. It is verified with recorded fixtures in Phase 4 before we commit to 30 concurrent SSE connections.)
  - `/_push/trading/orders/` (one stream)
- **Polling:**
  - `marketdata/{id}` every 5 s per active instrument during market hours (≤ 30 × 0.2 req/s = 6 req/s at 30 instruments, which is too much with a 2 rps budget). Therefore:
    - poll only instruments with **live intents or open orders** at 5 s
    - poll the rest at 60 s
  - `deals` + `orders` every 30 s and on every `ORDER` event; `positions` every 60 s
- **`QuoteComposer`:**
  - merges the best bid/ask from `ORDER_DEPTH` with last/volume from polls into `Quote{bid, ask, last, asOfDepth, asOfLast}`
  - **stale** if `now − max(asOfDepth, asOfLast) > 10 s` **or** the depth stream is disconnected
  - stale quotes block new orders in that instrument (ADR 0003)
- **Fan-out:** `Channel<T>` (bounded, `DropOldest` for depth, `Wait` for own-order events). Own-order events are **never dropped**; if the consumer lags, the stream halts.

### 4. Fail-safe halting
- **`HaltController`:**
  - a process-wide, thread-safe set of active halt reasons: `SchemaDrift`, `SessionExpired`, `EndpointGone`, `CircuitOpen`, `ReconciliationMismatch`, `StaleStream`, `Locked`, `KillSwitch`, `ManualHalt`
  - **any active reason ⇒ `OrderGateway` rejects new orders**; cancels are still allowed
  - reasons clear only through a positive signal (e.g. session info OK plus a successful reconciliation), never by timeout
- **Halt vs kill:** a halt blocks new orders. The **KillSwitch** (ADR 0003) additionally cancels all working orders and alerts.
- **Automatic kill:** drift on an **order-response** DTO or an `EndpointGone` on an order route escalates to KillSwitch. If we can't parse order responses, we can't trust the state of working orders.
- **Audit:** every halt and clear is audit-logged with its reason and evidence.

### 5. Drift detection
- **Online:** Tier-A strict deserialization, as above.
- **Canary (Phase 9):**
  - a scheduled read-only run at 08:30 Europe/Stockholm on trading days
  - checks session info, accounts, one orderbook (tick table), one marketdata call, a 5 s SSE depth handshake, and `validate` + `preliminaryfee` for a dummy draft (read-only)
  - any failure writes `state/trading-disabled.json` and sends an alert; Confirm/Auto refuse to start while that file exists
- **Research refresh:** before touching any route, re-read the clients (Qluxzz, avanza-sdk-go) at their HEAD, diff against `AvanzaRoutes`, update `avanza-endpoints.md` with the new commit URLs, and bump `RoutesVersion`.

### 6. What we deliberately do not do
- No BankID *approval* automation: a human approves every BankID login on the phone.
- No automatic login retry, and no retry of any order POST.
- No stop-loss orders in v1. The endpoint exists but the payload has a `orderBookId` casing quirk and is lightly tested; revisit after Phase 8.
- No reliance on `requestId` for server-side idempotency, since that behaviour is unknown.
- No scraping of HTML pages. BankID login GETs `/` and `/handla/order.html` once each, exactly as the Go SDK does, **only to receive cookies**. Their content is never parsed, and the recording keeps only the byte count.

## Implementation notes (Phase 3)

- **Resilience and rate limiting are our own code,** not `Microsoft.Extensions.Http.Resilience` / `System.Threading.RateLimiting`:
  - `ReadResilienceHandler`: retry, circuit breaker, attempt timeout
  - `RateLimitHandler`: token bucket

  Both are small, run on `TimeProvider` so their tests are deterministic, and avoid a Polly dependency in the trading path. The behaviour is the one specified above.
- **Unknown fields are found by a scanner** that walks the JSON alongside the source-generated metadata. A Tier A drift report therefore lists **every** unknown path, not only the first.
- **No automatic re-login in Phase 3.** The CLI is a one-shot trigger; the "one re-login attempt" on 401/403 belongs to the long-running `HaltController` (Phase 6).
- **Deals:** no reference client models the current response, so the DTO waits for your recording (`EndpointNotModelledException` until then).

## Implementation notes (Phase 4)

- **Stream pipeline:** RateLimit → SecurityToken → Cookies → primary.
  - It has no recording handler, because that handler buffers whole bodies. The stream client records events itself (`qa-stream-recording/1`).
  - It has no retry handler: the stream loop is the retry.
- **Timeouts:** the response headers must arrive within the normal attempt timeout (15 s). After that, an **idle watchdog** (60 s without a byte) replaces the HTTP timeout, so a half-open connection is dropped and reconnected. The quote is stale long before that.
- **Terminal statuses:** besides 401/403, a **404 is `EndpointGone`**, and any other 4xx or a non-`text/event-stream` 200 is **schema drift**. None of them reconnects.
- **Events:** `info` events are heartbeats and are never parsed. Any other event name except `ORDER_DEPTH` is drift, because the stream is Tier A.
- **The `ORDER_DEPTH` DTO is provisional** until your recording (the Go SDK's flat `{buyPrice, buyVolume, sellPrice, sellVolume}` levels).
- **Staleness is measured on our clock:** the receipt time of the last depth event and of the last *successful* poll. Avanza's `quote.updated` isn't used, because it is the last server-side change and stays old in a quiet market.
- **Bid/ask:** the composer takes them from whichever source is newer, because the poll also carries a full depth. The stale check runs every 250 ms.
- **Own-order stream (`/_push/trading/orders/`):** moved to Phase 6, where the OMS consumes it. Until an order exists there is nothing to record.

## Alternatives considered

| Option | Why not |
|---|---|
| Use the Python `avanza-api` in-process (pythonnet) or as a sidecar | It auto-retries login, can't be made strict, adds a runtime, and doesn't do SSE. We read it as a *reference* only. |
| One `IBrokerGateway` with order methods | Works, but "only OrderGateway calls order methods" then needs a method-level architecture rule. Two interfaces make the rule structural. |
| Lenient DTOs everywhere | Silent misreads of orders or positions are the worst failure mode for a trading program. |
| Strict DTOs everywhere (no tiers) | Halts on harmless additions to informational payloads, which trains the operator to ignore halts. |
| Keep CometD | Discontinued (Qluxzz #151). |

## Consequences

- Any Avanza change to a Tier-A payload stops trading until a human refreshes the research and fixtures. That is intended.
- Polling budgets are tight at 2 rps. The universe is kept to OMXS30, and fast polling is limited to instruments with active intents.
- Adding an endpoint means a route, DTOs (tier chosen explicitly), a mapper, a recorded fixture, a drift test and a research-doc entry.

## Open items (resolved in the named phase)

1. Can ~30 concurrent SSE depth streams run on one session, or is there a multiplexed variant? **Phase 4**, from recordings. `qa stream` runs up to 5 streams on one session. Your two-instrument recording (plan 04, stop point) is the first data point; 30 streams stays unproven until a Paper-mode run in Phase 6.
2. The current path for `modify`. **Phase 6/7**, from a captured web-app request.
3. The `profit` field on sells (Qluxzz #156). **Phase 7**, from a captured web-app sell.
4. How lockout is signalled (status and message). **Phase 3**, only if it happens; never provoked.
