# 03 — Phase 3: Avanza read-only gateway

- **Status:** in progress (2026-09-25)
- **Scope:** master plan §4 Phase 3; ADR 0002 (accepted 2026-09-25); ADR 0004.
- **Gate:**
  - fixture tests are green
  - your manual read-only run succeeds
  - the log scan finds no secrets
  - ADR 0004 is recorded before your first live run

## Decisions taken at the start of this phase (2026-09-25)

| Question | Your answer | What I implement |
|---|---|---|
| Broker split (master plan §3) | "split the broker" | `IBrokerGateway` (reads) now. `IBrokerOrderChannel` (orders) arrives in Phase 6. **Phase 3 has no order code at all.** |
| DTO strictness (master plan §2 item 8) | "Apply the strict" | Read as approval of the **tiered** proposal in ADR 0002. See the note below. |
| Account | "a dedicated ISK account" | Orders (Phase 6+) are allowlisted to one ISK. Reads show every account; account ids are always masked to the last 3 digits. |
| Terms of use | "I agree to avanzas terms of use" | ADR 0004 records your decision and its limits. |

**How "Apply the strict" is read.** The question was whether to accept the tiered refinement in master plan §2 item 8:
- **Tier A (trading-critical) is strict:** unknown **or** missing fields ⇒ `SchemaDriftException` ⇒ halt.
- **Tier B (informational)** still rejects missing required fields, but only *logs* unknown fields.

If you meant "strict everywhere, no tiers", say so. It is a one-line change per Tier B DTO.

## Research refresh (Phase 3 rule)

`docs/research/avanza-endpoints.md` is dated 2026-09-25, which is less than 7 days old. Both clients were re-checked at the start of this phase and their HEADs are unchanged:
- Qluxzz: `a6a18a94…`
- avanza-sdk-go: `43f39025…`

Every route in `AvanzaRoutes` links to the commit it came from.

## Projects

| Project | Contents | Dependencies |
|---|---|---|
| `src/QuantAnalyst.Core` | Domain types (ids, accounts, positions, orders, instruments, tick table, market snapshot, bars), `IBrokerGateway`, broker exceptions, `Secret`. **No I/O.** | none |
| `src/QuantAnalyst.Avanza` | TOTP, routes, HTTP pipeline, authenticator, tiered DTOs, mappers, `AvanzaGateway`, secret stores, redacting logger, recording and sanitizer | Core, `Microsoft.Extensions.Logging.Abstractions` 10.0.12 |
| `tests/QuantAnalyst.Core.Tests` | Tick rounding, id masking, `Secret` | Core |
| `tests/QuantAnalyst.Avanza.Tests` | TOTP vectors, drift, auth flow, pipeline, gateway, sanitizer, log scan, architecture scan | Avanza, `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 |

Package versions come from nuget.org's flat-container index (2026-09-25). `System.Threading.RateLimiting` and Polly are **not** used: the token bucket and the read-retry/circuit-breaker handler are ~150 lines of our own code, driven by `TimeProvider` so the tests are deterministic. This deviates from ADR 0002 §2, which names `Microsoft.Extensions.Http.Resilience`; the ADR now carries an implementation note saying so.

## Design

### Authentication (`AvanzaAuthenticator`)
1. **Before any HTTP call:**
   - load `state/auth.json`; if `Locked`, stop
   - read the credentials from the secret store; if any is missing, stop
2. `POST usercredentials`. Then:
   - 401/403 → failure recorded → `LoginFailedException`
   - 423/429 → `Locked` immediately. This is conservative: the lockout signal is unknown (ADR 0002 open item 4).
   - 5xx/transport → `BrokerUnavailableException`, which is not counted toward the lock
3. `twoFactorLogin.method` must be `TOTP`; anything else → `LoginFailedException("unsupported 2FA")`.
4. If fewer than 3 s remain in the current 30 s window, wait for the next window. That is a wait, not a retry.
5. `POST totp` once. A 401/403 → failure recorded → `LoginFailedException`.
6. Token: the `X-SecurityToken` header, else the `AZACSRF` cookie, else `SchemaDriftException`. The source used is reported (never the value).
7. **Lock rule:** a second recorded failure within 24 h ⇒ `Locked`, persisted. `qa login --clear-lock` clears it; do that only after you have checked with BankID on avanza.se that login works.
8. **No code path calls login twice** in one trigger. A test asserts exactly one request per step even on failure.

### HTTP pipeline
- **`avanza-auth`** (login only): `RecordingHandler` → `RateLimitHandler` → primary handler. No retries.
- **`avanza-read`:** `RecordingHandler` → `ReadResilienceHandler` → `RateLimitHandler` → `SecurityTokenHandler` → primary handler.
  - **Resilience:**
    - retry ×2 on 408/429/5xx/transport errors/attempt timeout (15 s)
    - exponential backoff with full jitter; `Retry-After` is honoured up to 30 s
    - circuit breaker: ≥ 5 calls and ≥ 50 % failures within 30 s ⇒ open 60 s, then one half-open probe
  - Retries apply only to requests marked as idempotent reads, which is every Phase 3 route.
- **`RateLimitHandler`:** one token bucket shared by both clients: 2 tokens/s, burst 5.
- **Status mapping** (in `AvanzaApiClient`, after the pipeline):
  - 401/403 → `SessionExpiredException`
  - 404 on a Tier A route → `EndpointGoneException`
  - other non-2xx → `BrokerUnavailableException`
- There is **no automatic re-login in Phase 3**. The CLI is a one-shot trigger; the re-login-once rule arrives with the long-running `HaltController` in Phase 6.

### DTOs and drift
- Two source-generated `JsonSerializerContext`s:
  - **Tier A:** `UnmappedMemberHandling.Disallow`
  - **Tier B:** `Skip`
  - both: `RespectNullableAnnotations`, `RespectRequiredConstructorParameters`, strict number handling
- Before deserializing, an **unknown-field scanner** walks the JSON alongside the source-generated metadata and returns **every** unknown path (e.g. `$.accounts[0].foo`).
  - **Tier A:** any unknown path ⇒ `SchemaDriftException` listing all of them. One probe run therefore reports the complete fix list.
  - **Tier B:** unknown paths are logged once per route per process as `drift.warning`.
- Required members are only the fields we map. Known-but-unused fields are declared optional, typed as `JsonElement` where their shape doesn't matter, so a *renamed* unused field is still caught as unknown.
- Where the reference clients disagree on a type, the DTO uses `JsonElement` and the mapper accepts exactly the documented shapes; anything else ⇒ drift. Example: `timeOfLast` is epoch ms in Qluxzz and the Go testdata, but an ISO string in Qluxzz's `MarketData` validator.
- **Money and prices are `decimal`** straight from the JSON number text, never through `double`. Volumes are decimal and validated integral in the mapper.
- Every DTO has a `DtoVersion` string; `SchemaDriftException` carries route, DTO version, tier and paths.

| Tier | Route | DTO source |
|---|---|---|
| A | session info, accounts overview, trading accounts, positions, open orders, orderbook (tick table), marketdata | Qluxzz + Go models |
| B | search, price chart, transactions | Qluxzz + Go models |
| – | deals | **No client models the current response.** Recorded by `qa probe`, and the DTO is written from your recording. Until then `GetDealsAsync` throws `EndpointNotModelledException`. |

### Recording and sanitizing
- **`RecordingHandler`** (opt-in) writes one JSON file per exchange to `recordings/live/<utc-stamp>/NNN-<route>.json`.
  - Contents: method, path, status, content type, header **names**, cookie **names**, request and response body.
  - Never written, even to the git-ignored folder:
    - header or cookie **values**
    - request bodies of authentication routes (these contain your password and TOTP code)
    - string/number values of authentication responses. Session info contains the security token, so only its structure is kept.
- **`qa recordings sanitize --in recordings/live/<stamp> --out recordings/fixtures/avanza/<date>`** (you run it; Claude cannot read `recordings/live`):
  - Account ids → fake ids that keep the last 3 digits. Also applied as a global replace across every string.
  - `urlParameterId` → `url-N`.
  - User-defined names → `Account N`.
  - `customerId`, `pushSubscriptionId`, `authenticationSession`, `securityToken`, `greetingName`, `noteId`, `verificationNumber` → `<redacted>`.
  - On personal routes (accounts, positions, orders, deals, transactions), every number is replaced by a deterministic value of the same shape (integer stays integer, decimals keep their count). `--keep-amounts` opts out.
  - The output is then scanned for:
    - your stored secrets (compared in memory, never printed)
    - JWT/long-token patterns
    - any leftover original account id

    Any hit fails the command and nothing is written.

### Logging
`RedactingLogger` is an `ILogger` that writes one line per event to a `TextWriter` (stderr in the CLI). Before writing, it:
- replaces every registered secret value with `***`: username, password, TOTP secret, security token, cookie values
- masks every registered account id to `***123`
- scrubs the `X-SecurityToken`, `Cookie`, `Set-Cookie` and `AZACSRF=` patterns

`AccountId.ToString()` is already masked, so a raw id only leaks through deliberate `.Value` access. A test runs the full fake login and every read at `Trace` level, then scans the logs and recordings for the fixture secrets and the unmasked account id.

### Secrets
- `ISecretStore` → `AvanzaCredentials(Secret Username, Secret Password, Secret TotpSecret)`.
- **Windows Credential Manager** (default on Windows), with generic credentials:
  - `QuantAnalyst:Avanza`: user name = your Avanza username, password = your password
  - `QuantAnalyst:Avanza:TOTP`: password = the Base32 TOTP secret

  `qa secrets set` prompts without echo and writes both; `qa secrets check` says which exist, never their values.
- **Environment variables** (`QA_AVANZA_USERNAME`, `QA_AVANZA_PASSWORD`, `QA_AVANZA_TOTP_SECRET`) are a dev fallback, selected explicitly with `--secret-store env`. They are never used in CI.
- `dotnet user-secrets` is dropped: it stores plaintext JSON under the user profile, which is no better than env vars and adds a dependency. This is a deviation from master plan §5.

### Guardrails added in this phase
- **Hook:** `block-live-trading.sh` also blocks Avanza **money-transfer** paths (`transfer`, `withdraw`, `deposit`, `payment`, `uttag`, `overforing`, `insattning` under `/_api/`). The self-test covers them.
- **Architecture tests** (source scan of `src/`):
  - `/_api/` and `/_push/` literals appear only in `AvanzaRoutes.cs`
  - no order-entry or transfer route exists anywhere in `src/`
  - `AvanzaRoutes` exposes no mutating route; the only POST is search
  - nothing outside `QuantAnalyst.Avanza` references `QuantAnalyst.Avanza.Dto`

### CLI verbs (each invocation = one trigger = at most one login)
| Verb | Login | What it does |
|---|---|---|
| `qa secrets set` / `qa secrets check` | no | Windows Credential Manager setup |
| `qa login [--clear-lock]` | yes | one login + session health; prints the token source, never the token |
| `qa probe [--ticker ERIC-B]` | yes | one login, then every Phase 3 read with recording on; a per-endpoint OK/DRIFT/HTTP table. It continues after drift (read-only diagnostics) and stops on 401/403. |
| `qa accounts` | yes | accounts + buying power, ids masked |
| `qa positions [--account <last3>]` | yes | positions and cash |
| `qa orders` | yes | open orders |
| `qa quote <TICKER> \| --id <orderbookId>` | yes | bid/ask/last, tick size at the price, lot size |
| `qa recordings sanitize` | no | see above |

## Test plan (all offline, fake `HttpMessageHandler`)
- **TOTP:** RFC 6238 Appendix B SHA-1 vectors (8 digits) and the 6-digit truncation; Base32 round trip and invalid input.
- **Drift:**
  - each fixture parses
  - an injected unknown field (Tier A) ⇒ drift listing the path
  - a removed required field ⇒ drift
  - a wrong type ⇒ drift
  - Tier B unknown ⇒ a logged warning, no exception
- **Auth:**
  - success via the header, and via the cookie
  - no token ⇒ drift
  - 401 on either step ⇒ exactly one request per step, failure recorded
  - a second failure ⇒ `Locked` persisted
  - `Locked` ⇒ zero HTTP calls
  - unsupported 2FA
  - the < 3 s window wait (`FakeTimeProvider`)
- **Pipeline:**
  - 401 → `SessionExpired`
  - 404 → `EndpointGone`
  - retry on 503 then success
  - no retry on 400
  - circuit opens and half-opens
  - token bucket timing
  - `Retry-After` honoured
- **Gateway mapping:**
  - decimals are exact
  - `"0.00"` volumes
  - epoch-ms and ISO timestamps
  - unknown side ⇒ drift
  - tick table from `orderbook`
- **Sanitizer:** ids, names and amounts replaced; leftover-secret detection fails closed.
- **Log scan** and **architecture scan**, as above.
- **CLI:** `qa accounts` / `qa probe` against the fake server, checking the masked output and the exit codes.

### Fixtures
`recordings/fixtures/avanza/provisional/` holds **hand-built** payloads that follow the client models. Their `README.md` records the sources; the Go SDK testdata is MIT, attributed. They are labelled **provisional**, and your sanitized recordings replace them.

## Stop point
After the gate commands pass, I stop and give you the exact read-only commands to run on your Windows machine. You then commit the sanitized fixture folder (or send it), and I update the DTOs until every Tier A route parses strictly.
