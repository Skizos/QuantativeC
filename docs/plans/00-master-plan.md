# 00 — Master plan: QuantAnalyst for Avanza

- **Status:** DRAFT, awaiting your approval. No implementation code exists yet.
- **Date:** 2026-09-25
- **Inputs:** `CLAUDE.md`, `docs/prompts/master-prompt.md` (full spec), `docs/research/*.md`, ADRs 0001–0003.

Every phase session starts with: *"Read CLAUDE.md and docs/plans/00-master-plan.md. We're doing Phase N."*

---

## 1. Decisions from the kickoff Q&A

| Topic | Decision | Consequences |
|---|---|---|
| Target OS | **Windows** primary; Linux in CI | See the list below. |
| Account type | **ISK**, a dedicated account, allowlisted by id | See the list below. |
| Universe | **OMXS30** | See the list below. |
| Courtage class | **Small** (UNVERIFIED: 39 SEK up to 26,000 SEK, then 0.15 %) | `config/courtage.json` with `source_url` + `verified_on`. Confirm/Auto show Avanza's own `preliminaryfee` figure. |
| Historical data | **Adapter only** | `IHistoricalDataProvider` + `AvanzaChartImporter`. A vendor slot is left empty. Every result is labelled "Avanza history — NOT survivorship-free". |
| UI | **CLI only** | `QuantAnalyst.Api` shrinks to localhost health, status and **kill** endpoints. It has **no order endpoints** in v1 (ADR 0003). Confirm-mode cards are CLI-only. |
| Streaming | **SSE + polling** | `ORDER_DEPTH` and `ORDER` SSE streams, polled `marketdata` for last trade, polled deals/positions (ADR 0002). |

**Target OS (Windows):**
- `build.ps1` is the primary build script; `build.sh` is kept for Linux CI and cloud sessions.
- vcpkg triplet `x64-windows`; the native library is `qe.dll` (`libqe.so` on Linux).
- RIDs `win-x64` and `linux-x64`.
- Secrets live in Windows Credential Manager, with DPAPI as fallback.
- Hooks are bash scripts; Claude Code on Windows runs them via Git Bash.

**Account type (ISK):**
- Reports say "net figures assume ISK (schablonbeskattning); no per-trade tax modelled; not tax advice".
- No short selling and no leverage, which matches the risk limits.

**Universe (OMXS30):**
- `config/universe.omxs30.json` maps orderbookId ↔ ISIN ↔ ticker, **with effective-dated membership**. Backtesting today's constituents over 10 years is survivorship bias, so a backtest uses historical membership or is labelled as biased.
- Continuous-trading names only, so the First North periodic-auction model is out of v1 scope.

## 2. What the research changed compared with the original spec (please read)

1. **CometD websocket is gone** (discontinued, removed by Qluxzz on 2025-11-26). Push is now **SSE**:
   - Available streams: `ORDER_DEPTH` per orderbook, own `ORDER`s, own `STOPLOSS`.
   - There is **no** quote/trade/deal/position stream, so those are **polled** (ADR 0002 §5). The spec's channel list is adjusted accordingly.
2. **The order endpoints moved 4 days ago:** `/_api/trading/order-entry/order/{new,delete}` (2026-09-21).
   - The Go SDK still uses the old paths, and `modify` may have moved as well.
   - Unresolved: a `profit` field that sell orders reportedly need (Qluxzz #156).
   - So the order DTOs are **provisional** until you capture a real web-app order request in Phase 6/7.
3. **Avanza exposes pre-trade helpers we should use:**
   - `orderbook/{id}` returns the **per-instrument tick-size table**, which becomes our authoritative tick source.
   - `preliminaryfee` returns **exact courtage/fees** for an order.
   - `order/validation/validate` runs Avanza's own order checks.
4. **Security token:** the `X-SecurityToken` response header (Qluxzz) *or* the `AZACSRF` cookie (Go SDK). Support both.
5. **Login retry trap:** Qluxzz retries a 401 login with the next OTP **by default**. We explicitly do **not** (one attempt per trigger).
6. **Public endpoints** (search, quotes, order depth, price charts) work **without logging in**, per the Go SDK. Research and chart import can run without credentials.
7. **Drift rate:** about 11 breaking changes in 27 months, three of them in order entry. This backs ADR 0002's fail-safe design.
8. **Strict deserialization refinement (approved 2026-09-25, ADR 0002 accepted):**
   - CLAUDE.md: unknown or missing fields ⇒ `SchemaDriftException` ⇒ halt.
   - ADR 0002 applies **unknown-field rejection** only to **trading-critical DTOs** (orders, deals, positions, accounts, order responses, orderbook, marketdata, SSE `ORDER`/`ORDER_DEPTH`).
   - Informational DTOs (stock details, news) still reject **missing required** fields, but only *log* unknown extras.
   - Reason: Avanza adds fields to informational payloads constantly, and halting trading on a new ESG field would make the kill switch meaningless.
9. **Egress-blocked sources:** this cloud container can't reach avanza.se, nasdaq.com, eur-lex, fi.se or learn.microsoft.com.
   - Courtage, calendar and the RTS 11 tick table are therefore **UNVERIFIED** and carry `verified_on` fields.
   - Confirm/Auto refuse to start while any is empty (ADR 0003).
10. **Terms of use:** the website terms (search extract) forbid automated tools **without written consent**. Phase 3's first live call is gated on your decision. **Recorded 2026-09-25 in ADR 0004** (accepted; no written consent on record).

Nothing in the research contradicts CLAUDE.md's rules. Item 8 is a *refinement* of one rule, and I need your yes/no on it.

## 3. Architecture at a glance

```
            ┌──────────────── QuantAnalyst.Cli (System.CommandLine) ─────────────────┐
            │ login accounts positions quote history price risk optimize backtest    │
            │ paper run  rebalance --plan|--execute  orders cancel-all kill report   │
            └───────┬──────────────────────────────┬────────────────────────────────┘
                    │                              │
     QuantAnalyst.Analytics                QuantAnalyst.Trading
     (risk, portfolio, backtest runner,    ┌───────────────────────────────────────┐
      TrialLedger, CV/PBO/DSR, reports)    │ Strategy → OrderIntent                │
                    │                      │   → TickRounder → PreTradeRiskEngine  │
                    │                      │   → ModeGate (Backtest/Paper/Confirm/ │
                    │                      │       Auto + PromotionState)          │
                    │                      │   → OrderGateway  (ONLY caller of     │
                    │                      │       IBrokerOrderChannel)            │
                    │                      │ OMS + Reconciler, KillSwitch,         │
                    │                      │ HaltController, Scheduler, AuditLog   │
                    │                      └──────────────┬────────────────────────┘
                    │                                     │ IBrokerGateway (reads)
                    │                                     │ IBrokerOrderChannel (orders)
       QuantAnalyst.Native ── qe.dll (C ABI) ── native/   ├─ AvanzaGateway   (QuantAnalyst.Avanza)
       [LibraryImport], SafeHandles             qe::*     ├─ PaperGateway    (live data, simulated fills)
                                                          └─ BacktestGateway (historical data)
       QuantAnalyst.Data: DuckDB store (known-at), instrument master, chart importer, vendor slot
       QuantAnalyst.Core: domain types, no I/O
```

Two interfaces instead of one:
- **`IBrokerGateway`** covers reads and streams.
- **`IBrokerOrderChannel`** covers place, modify and cancel.

That makes "only `OrderGateway` may reach order methods" a one-line architecture test (ADR 0002/0003).

## 4. Phases and gates

Every gate is proven by running the listed commands and pasting the output in the session. After every green step: commit and push on the working branch.

### Phase 0 — Kickoff ✅ (2026-09-25)
Research docs, CLAUDE.md, guardrail hooks plus their self-test, this plan, and ADRs 0001–0003.

*Gate:*
- `bash tests/hooks/block-live-trading.test.sh` passes (23/23, run 2026-09-25).
- You approve this plan.

### Phase 1 — Skeleton + interop spine ✅ (2026-09-25; results in `docs/plans/01-phase1-skeleton.md`)
- **Repo scaffolding:**
  - `QuantAnalyst.sln` (see note), `global.json` (10.0.100 + `latestFeature`)
  - `Directory.Build.props` (net10.0, nullable, `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, `AllowUnsafeBlocks` in Native only)
  - `.editorconfig` (SYSLIB1054 = error)
  - `Directory.Packages.props` (central package versions from `versions.md`)
- **Native:**
  - `CMakePresets.json` presets: `dev` (Ninja, Debug), `release`, `asan` (clang, ASan+UBSan, Linux), `msvc-dev`, `msvc-release`
  - `vcpkg.json` with `builtin-baseline 10541e31…`, eigen3/gtest/benchmark, and QuantLib as an optional feature
- **ABI:**
  - `qe_abi_version`, `qe_engine_create/destroy`, `qe_last_error`, `qe_bs_price_batch`
  - `QuantAnalyst.Native` with `QeEngineHandle : SafeHandle`, a per-RID `NativeLibrary` resolver, and a startup ABI check
  - Layout tests on both sides
- `build.ps1` / `build.sh`.
- `docs/setup.md`:
  - toolchain install on Windows
  - secrets setup
  - **how to verify the PreToolUse hook** (run the self-test, then ask Claude to run a blocked command and see it refused)
- `.claude/hooks/session-start.sh` for cloud sessions (apt `dotnet-sdk-10.0`, vcpkg bootstrap).
- CI: GitHub Actions matrix `windows-latest` + `ubuntu-latest` running native tests, managed tests, format check (`dotnet format --verify-no-changes`, `clang-format --dry-run -Werror`) and the hook self-test.
- Benchmarks: `bench/` for 1e6 BS prices (Google Benchmark) and interop overhead per call (BenchmarkDotNet). Results go to `bench/results/phase1-<machine>.json`.

*Gate:*
- `cmake --preset dev && cmake --build --preset dev && ctest --preset dev --output-on-failure` is green.
- `ctest --preset asan` is green, with no leaks.
- `dotnet build QuantAnalyst.sln && dotnet test --solution QuantAnalyst.sln` is green.
- The BS reference (call 10.4506 / put 5.5735, tol 1e-4) passes **through the C# binding**.
- Benchmark saved.

*Note:* CLAUDE.md names `QuantAnalyst.sln`. The .NET 10 SDK also supports `.slnx`, the newer XML solution format; Phase 1 will check which one `dotnet new sln` produces. I'll use `QuantAnalyst.sln` unless you prefer `.slnx`, and update CLAUDE.md if you do.

### Phase 2 — Pricing, risk, portfolio (C++) ✅ (2026-09-25; results in `docs/plans/02-phase2-numerics.md`)
- **Pricing (`qe::pricing`):** BSM + Greeks, implied vol (Brent), CRR American, MC (antithetic + control variate, SE, Sobol option).
- **Risk (`qe::risk`):** historical/parametric/MC VaR & ES; covariance (sample, EWMA, Ledoit-Wolf); stress (OMXS30 −10 %, SEK ±5 %).
- **Portfolio (`qe::portfolio`):** MV with bounds, min-var, risk parity, HRP, integer-lot rebalance solver (lot = `volumeFactor`/`tradingUnit` from Avanza orderbook).
- **ABI and CLI:** batched ABI calls; CLI verbs `price`, `risk`, `optimize`.

*Gate:* every numerical item in `<verification_requirements>` passes, with seeds recorded.

### Phase 3 — Avanza read-only gateway ✅ (2026-09-25; results in `docs/plans/03-phase3-avanza-read.md`)
- First, refresh `avanza-endpoints.md` if it is older than 7 days.
- Implement RFC 6238 TOTP (Appendix B vectors), `AvanzaAuthenticator` (single attempt, lock persistence), `AvanzaHttpClient` (handler pipeline, ADR 0002), `AvanzaRoutes` (versioned), strict DTOs + mappers, and `AvanzaGateway` reads: accounts, trading accounts, positions, orders, deals, transactions, search, orderbook (tick table), marketdata, chart, session info.
- Recording + sanitizing tool (`qa recordings sanitize`), a secret-scanning log test, and the drift test.
- Windows Credential Manager secret store.

*Stop point:* I hand you the exact read-only commands (`qa login`, `qa accounts`, `qa positions`, `qa quote ERIC-B`). You run them and send sanitized recordings.

*Gate:*
- Fixture tests are green.
- Your manual read-only run succeeds.
- The log scan finds no secrets.
- **ADR 0004 (authorization to automate) is recorded before your first live run.**

### Phase 4 — Streaming + data store (in progress; plan in `docs/plans/04-phase4-streaming-store.md`)
- **Streaming:** `AvanzaStreamClient` (SSE, `Last-Event-ID`, server `retry`, backoff, 1 MB events) plus `QuoteComposer` (ORDER_DEPTH + polled marketdata ⇒ `Quote` with `asOf` and a stale flag). Fan-out over `Channel<T>`.
- **Store:** DuckDB with **known-at** timestamps (`valid_from`, `known_at`, `source`, `source_version`), the instrument master, `AvanzaChartImporter`, and `IHistoricalDataProvider` (vendor slot).
- **Calendar:** `config/market-calendar.XSTO.{2026,2027}.json` (XSTO is the MIC Avanza reports; an earlier draft said "XNSA" by mistake), filled and verified by you from Nasdaq's official calendar.

*Gate:*
- Replay tests on recorded SSE fixtures pass.
- Staleness test: no depth or poll update for 10 s sets the flag.
- Known-at query test: a restatement is not visible before its `known_at`.
- The calendar test classifies every weekday.

### Phase 5 — Honest backtesting
- **C++ event engine:**
  - limit-order fill model: fill at t+1 or later, only when the market trades through the limit
  - tick rounding from the RTS 11 table, transcribed from EUR-Lex or supplied by you
  - opening/closing auction handling
  - costs: Small courtage, spread, slippage, ADV participation cap
  - no allocation per bar
- **.NET analytics:** TrialLedger (append-only; every run, including mine), walk-forward, purged/embargoed K-fold, CSCV/PBO, Deflated SR.
- **Reports** carry all labels: source, survivorship, dates, SEK, cost assumptions, ISK.

*Gate:*
- The leakage canary is caught.
- A random strategy is not significant after deflation.
- Untouched limits do not fill.
- The 10y × 300 synthetic benchmark is saved.

### Phase 6 — Trading core + Paper mode
- **Gateways:** `IBrokerGateway` / `IBrokerOrderChannel` with Avanza, Paper and Backtest implementations. The Avanza order channel is **implemented but fixture-tested only**.
- **Pipeline:** `OrderGateway`, `PreTradeRiskEngine` (ADR 0003), OMS + Reconciler, `HaltController`, `KillSwitch` (CLI / API / `./KILL` file / automatic), `Scheduler`, hash-chained audit log.
- **Tests:** architecture tests plus a Paper spy (zero order-route requests at the HTTP handler level).

*Gate:* all trading tests green; the Paper spy is green.

**PROMOTION → Confirm** (you, not me):
- at least 10 trading days of Paper mode
- EOD reports with zero rule violations
- reconciliation always matched
- sane paper fills
- `qa promote --to Confirm` writes the signed promotion record (ADR 0003 §3)

### Phase 7 — Confirm mode
- **Pre-card calls:** before the card, `validate` + `preliminaryfee` run as read-only POSTs. They are pre-trade helpers, never order endpoints.
- **Order card:** instrument, side, volume, limit, SEK value, **Avanza fee vs model fee**, reason, and every risk-check result.
- **Confirmation:** typed `<TICKER> JA`, 30 s expiry, one order per confirmation. `rebalance --execute` confirms one order at a time.
- **Report:** EOD execution-quality report (slippage vs decision and arrival price).
- **Required first:** capture one real web-app buy and sell request (DevTools, sanitized) to finalize the order DTOs, including the `profit` question.

*Gate:* tests green. **I never run it.** You place the first minimal-size orders.

**PROMOTION → Auto** (you):
- at least 20 confirmed live orders
- zero unreconciled Unknown states
- realized slippage within the backtest cost assumptions

### Phase 8 — Auto mode (hard-limited)
Auto requires all of the following:
- **TOTP login enabled:** BankID needs a human at every login, so it cannot run unattended.
- a signed promotion record
- every `<risk_limits>` value set
- a KillSwitch self-test that day (a dummy **paper** order is cancelled)
- a market-hours check
- a working alert channel (Windows toast + email)

Also in this phase: the start-of-day checklist and auto-shutdown at a configured time.

*Gate:* tests green. You enable Auto yourself with **reduced limits** for 2 weeks.

### Phase 9 — Hardening + docs
- **Drift canary:** a scheduled read-only run before the open. It validates Tier-A DTOs, the session info, the orderbook tick table and the SSE handshake, and on mismatch it disables trading.
- **Docs:** README, runbook (lockout, drift, kill switch, Unknown orders), `docs/testing.md`.
- **CI:** matrix finalized.

*Gate:* a fresh clone reaches build and tests with one command; you review the runbook.

## 5. Cross-cutting conventions (all phases)

- **Config:**
  - `appsettings.json` + `config/*.json` (universe, courtage, calendar, risk limits), validated at startup with `IValidateOptions`.
  - Any value with `verified_on: null` blocks Confirm/Auto.
- **Secrets:** `ISecretStore` → Windows Credential Manager (primary), `dotnet user-secrets` (dev), env vars (never in CI). Secret values are wrapped in a `Secret` type whose `ToString()` returns `***`.
- **Logging:** `ILogger` with structured templates. A redaction processor masks:
  - known secret values
  - `Cookie`/`Set-Cookie`/`X-SecurityToken` headers
  - account ids (keep last 3 digits)
  - `customerId`/`pushSubscriptionId`

  A test scans captured logs for fixture secret values.
- **Time:** `TimeProvider` everywhere. UTC in storage; Europe/Stockholm for display and calendar logic.
- **Money:** `decimal` for prices sent to Avanza, fees and cash; `double` inside models. Conversion happens in exactly one place (`PriceConversion`).
- **Tests:** no network. Fixtures live in `recordings/fixtures/` (sanitized, committed); raw live recordings go to `recordings/live/` (git-ignored, Claude read-denied).
- **Trials:** every backtest, including the ones I run while building, goes to the TrialLedger.

## 6. Actions for you before or around Phase 1

1. **Approve or amend** this plan and ADRs 0001–0003. In particular:
   - §2 item 8 (tiered DTO strictness)
   - the two-interface split (§3)
   - no order endpoints in the local API (ADR 0003)
2. **Terms decision:** recorded in ADR 0004 (2026-09-25). Writing to Avanza is still recommended; add the answer to the ADR's log.
3. **Network (optional):** if you want me to verify the courtage, calendar and RTS 11 figures myself in cloud sessions, add these to the environment's allowed domains:
   - `www.avanza.se` (public pages only)
   - `www.nasdaq.com`
   - `eur-lex.europa.eu`
   - `learn.microsoft.com`
   - `builds.dotnet.microsoft.com`

   Otherwise you verify them and I record `verified_on`.
4. **Account prep (Part A2):** a dedicated ISK, username + TOTP enabled, and the TOTP secret stored in Windows Credential Manager. Do **not** paste it anywhere Claude can read.

## 7. Risk register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Avanza changes an order endpoint (happened 2026-09-21) | High | Orders fail or are misrouted | One routes file; strict Tier-A DTOs; drift canary; 404 on order POST ⇒ Unknown + halt, never retry |
| Account blocked for ToS | Medium | Loss of access | Written consent; low request rate; one account; ADR 0004 gate |
| Username login locked by retries | Low (by design) | Manual BankID recovery | One attempt per trigger; persisted `Locked` state; no auto re-login in a loop |
| Duplicate order after timeout | Medium | Double position | Never auto-retry POST; Unknown blocks the instrument until reconciled; duplicate-intent check |
| Stale quotes → bad limit price | Medium | Poor fills | 10 s staleness; ±2 % collar on the rounded price; SSE and poll freshness both required |
| Backtest overfitting | High | False confidence | TrialLedger, DSR, PBO, locked holdout, ±20 % parameter stability |
| Survivorship bias (Avanza history, current OMXS30 list) | High | Inflated results | Effective-dated membership; labels on every report; vendor slot |
| Unverified cost and calendar constants | Medium | Wrong costs/hours | `verified_on` gate; `preliminaryfee` cross-check in live modes |
| Claude runs something live | Low | Real orders | PreToolUse hook + self-test; no allowlist for live modes; order channel reachable only via `OrderGateway` |
