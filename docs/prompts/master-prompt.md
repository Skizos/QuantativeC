# Master prompt: "QuantAnalyst for Avanza" (.NET 10 + C++20)

This is the source specification the project was kicked off with (2026-09-25). Part B
is `/CLAUDE.md`. Phase sessions reference `<risk_limits>`, `<verification_requirements>`,
`<quant_rules>` and `<engineering_standards>` from Part C below. Decisions that refine
or override this text live in `docs/adr/` and `docs/plans/00-master-plan.md`.

Not financial advice. You are responsible for every order the program places. Start in
backtest and paper mode; real money only after the promotion gates in Part D.

---

## PART 0 — Read this first: the Avanza reality

1. There is no official public Avanza API. Every existing client library (Python
   `avanza-api` by Qluxzz, JavaScript `avanza` by fhqvst, Go `avanza-sdk-go`) works by
   copying what Avanza's own web/mobile app sends. Endpoints can change or disappear
   without notice. The program must survive this: isolate Avanza behind one gateway
   interface, detect breakage fast, and fail safe (stop trading, never guess).
2. Terms of use. Avanza's website user terms have been quoted as prohibiting robots,
   scrapers, and other automated tools without written permission, with separate
   agreements applying to customers. Read the current Användarvillkor and your account
   agreement yourself before running anything against your account, and consider asking
   Avanza customer service in writing. Possible consequences range from a blocked login
   to a closed account.
3. Login. Automation uses username + password + TOTP (two-factor via an authenticator
   app). BankID cannot be automated (a human has to scan/approve). Avanza locks
   username/password login after repeated failed attempts (BankID still works), so the
   program must never retry login in a loop.
4. Market-abuse rules (MAR) apply to you too. No spoofing, layering, wash trades, or
   orders you don't intend to execute. The program must not generate patterns that look
   like these (e.g., rapid place/cancel cycles).
5. Data limits. Avanza price history is not survivorship-free (delisted companies are
   largely missing) and is not a point-in-time fundamentals database. Backtests built
   only on Avanza data must be labelled with this limitation.

## PART A — Setup and connecting

### A1. Toolchain
- .NET 10 SDK (LTS, supported to Nov 2028). Do not target .NET 8/9 (end of support 10 Nov 2026).
- C++20 compiler (MSVC 2022 17.10+, GCC 13+, Clang 17+), CMake 3.28+, Ninja, vcpkg manifest mode.
- Git, clang-format, clang-tidy. Optional: DuckDB CLI, Docker.
- Optional reference only: Python 3.12 + `pip install avanza-api` to compare
  request/response shapes during development (not used at runtime).

### A2. Avanza account preparation
1. Use a dedicated account. Open a separate ISK or AF account used only by the program,
   funded with an amount you can afford to lose. The program will be allowlisted to that
   account ID only.
2. Enable username/password login: log in with BankID → Meny → Inställningar →
   Inloggning och utloggning → Aktivera Användarnamn (order credentials via "glömda
   inloggningsuppgifter" if you have none).
3. Enable two-factor with an authenticator app and capture the TOTP secret: Profil →
   Inställningar → Sajtinställningar → Inloggning och utloggning → Användarnamn →
   Tvåfaktorsinloggning → (Återaktivera if already on) → Aktivera → Annan app för
   tvåfaktorsinloggning → Kan du inte scanna QR-koden? to reveal the secret. Enter your
   password if asked, then confirm with a code generated from that secret. Add the same
   secret to your phone's authenticator too, so you are never locked out.
4. Note your account ID(s) (visible in the account URL/overview).
5. Treat the TOTP secret like your password. Anyone holding password + TOTP secret can
   trade your account.

### A3. Secrets (never in the repo, never in logs)
Preferred: OS secret store (Windows Credential Manager/DPAPI, macOS Keychain, Linux
libsecret) or a vault. Development fallback: `dotnet user-secrets`. Keys:
`AVANZA__USERNAME`, `AVANZA__PASSWORD`, `AVANZA__TOTPSECRET`, `AVANZA__ALLOWEDACCOUNTIDS`
(comma list), `TRADING__MODE` (`Backtest|Paper|Confirm|Auto`), optional
`HISTDATA__PROVIDER` + key for a second historical source.

### A4. Claude Code configuration
`/init` then replace CLAUDE.md with Part B; `/permissions` to allowlist:
- WebFetch: `learn.microsoft.com`, `github.com`, `raw.githubusercontent.com`, `pypi.org`,
  `en.cppreference.com`, `cmake.org`, `vcpkg.io`, `eigen.tuxfamily.org`, `quantlib.org`,
  `nasdaq.com`, `nasdaqomxnordic.com`, `avanza.se` (public help/terms pages), `fi.se`,
  `arxiv.org`, `code.claude.com`.
- Bash: `dotnet build|test|format`, `cmake`, `ctest`, `ninja`, `git status|diff|add|commit`.
  Do NOT allowlist anything that runs the app in `Confirm` or `Auto` mode. Claude Code
  must never place a real order on its own.

`.claudeignore`: `bin/ obj/ build/ out/ vcpkg_installed/ data/ *.parquet *.duckdb .env*
secrets/ recordings/live/`.

Hooks (`.claude/settings.json`): PostToolUse on `Edit|Write` → `dotnet format` for `*.cs`,
`clang-format -i` for C++ files. PreToolUse hook that blocks any Bash command containing
`--mode Confirm`, `--mode Auto`, or `TRADING__MODE=Auto` (exit code 2 with a message).
This is the hard guarantee that Claude cannot trade live.

### A5. Optional: second historical data source
For serious research, add a survivorship-free, split/dividend-adjusted source for Nordic
equities (commercial vendors or exchange data). The program treats Avanza as the
execution venue and live-quote source, and any configured vendor as the research-history
source.

---

## PART C — Kickoff prompt

```text
<role>
You are a senior quantitative developer strong in modern C++ (C++20, numerics,
performance) and .NET (C#, ASP.NET Core, native interop, resilient HTTP clients),
with experience building broker integrations and order-management systems with
strict risk controls. Read CLAUDE.md first; it overrides anything below.
</role>

<goal>
Build "QuantAnalyst for Avanza", which lets me:
1. Log in to Avanza (username + password + TOTP), read accounts, positions, cash,
   orders, deals and transactions.
2. Search instruments and stream live quotes / order-book depth / own-order updates.
3. Store price history (from Avanza chart data and optionally a vendor) with
   known-at timestamps.
4. Price instruments and compute Greeks (for warrants/options analysis), measure
   portfolio risk (VaR, ES, stress), and optimize portfolios in SEK.
5. Research and backtest strategies honestly (overfitting-aware).
6. Run strategies against Avanza in four modes, strictly in this promotion order:
     Backtest → Paper → Confirm → Auto
   - Paper:   live Avanza data, simulated fills, zero calls to order endpoints.
   - Confirm: every order is shown to me (instrument, side, volume, limit price,
              value in SEK, courtage, reason, risk-check results) and sent only
              after I type an explicit confirmation in the CLI/UI.
   - Auto:    orders sent automatically, only within hard limits, only on
              allowlisted accounts, with a kill switch and daily loss stop.
7. Rebalance a portfolio to target weights (generate the order list, then execute
   it through the chosen mode).
8. Get end-of-day reports: PnL, fills, slippage vs. decision price, costs, risk,
   and any rule violations.
</goal>

<why_this_architecture>
- C++ for Monte Carlo, optimization, covariance estimation, and the backtest inner
  loop; .NET for HTTP, auth, streaming, persistence, DI, the OMS, and the UI/CLI.
- Flat C ABI + [LibraryImport] is cross-platform and near-zero overhead with
  blittable types. C++/CLI is Windows-only, so rejected (ADR 0001).
- Avanza is an unofficial, changeable API: one gateway, strict DTOs, schema-drift
  detection, and fail-safe halting (ADR 0002). The strategy code never touches
  Avanza types directly — it talks to IBrokerGateway, which has three
  implementations: AvanzaGateway (live), PaperGateway (live data, simulated
  fills), and BacktestGateway (historical data). The same strategy code runs in
  every mode.
</why_this_architecture>

<research_first>
Before designing, use WebSearch/WebFetch and write docs/research/*.md with URLs:
1. avanza-endpoints.md — read the CURRENT source of github.com/Qluxzz/avanza
   (routes/constants, authentication flow, place_order/edit/delete, stop-loss,
   chart data, search, and the push/subscription channels) and any newer
   maintained client you find (e.g. a Go SDK that documents SSE streams). Document:
   auth steps and the headers/cookies/security token involved, session lifetime,
   every endpoint we need, request/response shapes, and known quirks. Note the
   commit hash you read. Flag anything that looks different between clients.
2. avanza-terms.md — find Avanza's current user terms and trading-agreement
   language about automated access; summarize with links so I can decide.
   Do not give legal conclusions.
3. market-rules.md — Nasdaq Stockholm trading hours, opening/closing auction
   times, half-days, the MiFID II tick-size table (liquidity bands), and current
   Avanza courtage classes. Cite sources and dates.
4. versions.md — .NET 10 SDK, [LibraryImport]/SYSLIB1054 guidance, vcpkg versions
   of eigen3/gtest/benchmark/(quantlib), installed CMake version.
If anything conflicts with CLAUDE.md, tell me before proceeding.
</research_first>

<modules>
NATIVE (C++20, namespace qe):
- core/: interpolation, root finding (Brent), RNG (PCG64/mt19937_64 + Sobol),
  Welford stats, Kahan sums, quantiles.
- pricing/: Black-Scholes-Merton + Greeks, implied vol, CRR binomial (American),
  Monte Carlo (antithetic, control variates, SE). Used for warrants/options/
  turbo analysis; product-specific payoffs (e.g., knock-out levels) as options.
- risk/: historical/parametric/MC VaR & ES, covariance (sample, EWMA,
  Ledoit-Wolf), stress scenarios (e.g., OMXS30 −10%, SEK ±5%).
- portfolio/: mean-variance with constraints, min-variance, risk parity, HRP,
  plus a rebalance solver with integer share lots and minimum-trade thresholds.
- backtest/: event-driven engine with a LIMIT-ORDER fill model (fill only if the
  next bars trade through the limit), tick-size rounding, auction handling,
  courtage + spread + slippage, no allocation per bar.
- api/: qe_api.h/.cpp — the only exported surface.

MANAGED (.NET 10):
- QuantAnalyst.Core: Instrument (orderbookId, ISIN, ticker, currency, tick table,
  market), Quote, Bar, OrderBookLevel, Account (id, type: ISK/KF/AF/…),
  Position, Order, Fill, OrderIntent, RiskCheckResult, TrialRecord.
- QuantAnalyst.Native: bindings, SafeHandles, per-RID NativeLibrary resolver,
  ABI check, status→exception mapping.
- QuantAnalyst.Avanza:
    AvanzaAuthenticator — username/password + RFC 6238 TOTP (SHA-1, 30 s, 6 digits;
      verify parameters against the research), captures session cookies and the
      security token; single attempt; exposes SessionState (Valid/Expired/Locked).
    AvanzaHttpClient — IHttpClientFactory, cookie container, required headers,
      rate limiter, retry/backoff (reads only; NEVER auto-retry order POSTs),
      circuit breaker, request/response recording for fixtures (sanitized).
    AvanzaStreamClient — the push channel (verify protocol in research: CometD/
      Bayeux websocket or SSE); channels for quotes, order depth, trades, own
      orders, own deals, positions; heartbeat + reconnect with backoff; sequence/
      staleness detection (no quote for N seconds ⇒ stale flag).
    AvanzaRoutes — every path in one place.
    DTOs + mappers — strict, versioned; drift ⇒ SchemaDriftException.
    AvanzaGateway : IBrokerGateway — accounts, positions, orders, deals,
      PlaceLimitOrder, ModifyOrder, CancelOrder, stop-loss (only if research
      confirms a stable endpoint), search, chart history.
- QuantAnalyst.Data: DuckDB/Parquet store with known-at timestamps; Avanza chart
  importer; optional vendor adapter; corporate-action adjustment; instrument
  master (orderbookId ↔ ISIN ↔ ticker).
- QuantAnalyst.Trading:
    OrderGateway — the ONLY path to IBrokerGateway order methods.
    PreTradeRiskEngine — see <risk_limits>.
    ExecutionModes — Backtest/Paper/Confirm/Auto with explicit promotion state
      stored on disk; Auto requires the promotion record (see Part D gates).
    OMS — order state machine (New → Sent → Working → PartiallyFilled → Filled |
      Cancelled | Rejected | Unknown), idempotency via client order keys,
      reconciliation against Avanza's order/deal lists every N seconds and on
      reconnect; "Unknown" state blocks new orders in that instrument until
      reconciled.
    KillSwitch — CLI command, API endpoint, file flag (./KILL), and automatic
      triggers; on trigger: stop new orders, cancel all working orders, alert.
    Scheduler — respects Nasdaq Stockholm calendar; no orders outside allowed
      windows; optional auction participation only if configured.
- QuantAnalyst.Analytics: risk & portfolio services, backtest runner, TrialLedger,
  walk-forward, purged/embargoed K-fold, CSCV/PBO, Deflated Sharpe, reports,
  execution-quality analysis (slippage vs. decision/arrival price).
- QuantAnalyst.Api: localhost-only minimal API; order endpoints require a
  per-session confirmation token; OpenAPI; problem-details errors.
- QuantAnalyst.Cli: `login`, `accounts`, `positions`, `quote`, `history`,
  `price`, `risk`, `optimize`, `backtest`, `paper run`, `rebalance --plan`,
  `rebalance --execute`, `orders`, `cancel-all`, `kill`, `report eod`.
</modules>

<risk_limits>
Defaults (configurable, but the code refuses to start Auto without all of them set):
- Account allowlist: orders only to AVANZA__ALLOWEDACCOUNTIDS.
- Instrument allowlist: only instruments in the configured universe.
- Order types: LIMIT orders only. No market orders.
- Price collar: limit price within ±2% of last trade / mid (reject otherwise).
- Max order value: min(25,000 SEK, 10% of account value).
- Max position per instrument: 20% of account value.
- Max gross exposure: 100% (no leverage, no short selling).
- Max orders per day: 20; max order actions per minute: 5; min 5 s between
  place/cancel on the same instrument (anti place-cancel churn, MAR hygiene).
- Daily loss stop: −2% of start-of-day account value ⇒ KillSwitch.
- Stale data: no order if the quote is older than 10 s or the stream is
  disconnected.
- Duplicate check: identical intent within 60 s is rejected.
- Session: any 401/403, schema drift, or reconciliation mismatch ⇒ halt new orders.
- Available cash check before every buy (including courtage).
Every check returns a structured result that is logged and shown in Confirm mode.
</risk_limits>

<verification_requirements>
Pricing/risk/portfolio (as before):
- BS reference S=100, K=100, r=5%, q=0, σ=20%, T=1 ⇒ call ≈ 10.4506,
  put ≈ 5.5735 (tol 1e-4); put-call parity 1e-10; Greeks vs finite differences;
  implied-vol round trip; American ≥ European; MC within 3 SE; seed determinism.
- VaR/ES: ES ≥ VaR; hand-computed quantile test; covariance SPD; optimizer
  weights sum to 1 and respect bounds; rebalance solver respects integer lots.
Backtest:
- Leakage canary (one-bar peek) must be caught; random strategy not significant
  after deflation; limit orders that are never touched must not fill.
Avanza & trading (all with recorded fixtures, zero network):
- TOTP generator matches RFC 6238 test vectors.
- Auth flow parses a recorded login; secrets never appear in logs (test scans
  captured log output for the fixture's secret values).
- DTO drift test: removing a required field from a fixture ⇒ SchemaDriftException
  ⇒ trading halted.
- Tick-size rounding for every liquidity band; price collar; each risk limit has
  a pass and a fail test.
- OMS state-machine tests including partial fills, reject, reconnect-while-
  working, and Unknown→reconciled.
- Order POST is never retried automatically (test with a timeout fixture: state
  becomes Unknown and reconciliation runs).
- Paper mode: a spy proves zero calls to order endpoints.
- KillSwitch: all three triggers cancel working orders and block new ones.
- Architecture test (e.g., NetArchTest): only OrderGateway references the order
  methods of IBrokerGateway.
Benchmarks: 1e6 BS prices, 1e5-path MC, 10y daily backtest × 300 Swedish stocks,
interop overhead per call.
</verification_requirements>

<quant_rules>
- Never show a Sharpe ratio alone: show raw SR, trial count, Deflated SR, PBO,
  max drawdown, turnover, cost drag (courtage + spread + slippage), OOS vs IS.
- Every backtest you run while building counts as a trial. Log it.
- Validate with walk-forward or purged/embargoed K-fold before the holdout.
- Flag < ~100 trades per free parameter, or collapse under ±20% parameter moves.
- Label outputs with data source (Avanza/vendor), survivorship status, date range,
  currency, cost assumptions, and account type (ISK/KF taxation differs from AF;
  state which one the net figures assume, and don't give tax advice).
- Small-cap/First North liquidity: cap participation at a % of average daily
  volume and model wider spreads.
</quant_rules>

<engineering_standards>
C++: RAII, no raw new/delete, std::span, -Wall -Wextra -Werror (/W4 /WX),
ASan+UBSan test preset, clang-tidy. Deterministic per-thread seeds.
.NET: nullable, TreatWarningsAsErrors, analyzers (incl. SYSLIB1054), DI +
Options with validation at startup, structured ILogger with a redaction
enricher, IHttpClientFactory + resilience handlers, CancellationToken
everywhere, System.Threading.Channels for stream fan-out, TimeProvider for
testable clocks. Audit log (append-only) of every order intent, risk result,
confirmation, request, and response (sanitized).
CI: Windows + Linux matrix, both test suites, format check; no secrets in CI.
</engineering_standards>

<how_to_work>
1. Stay in Plan Mode. Do the research in <research_first>.
2. Ask me up to 5 questions (AskUserQuestion): target OS; account type (ISK/AF/KF);
   courtage class; universe (e.g., OMXS30, Large Cap, First North); whether I want
   a second historical data vendor; CLI only or also a local web UI.
3. Write docs/plans/00-master-plan.md with phases and gates (Part D), ADR 0001
   (interop), ADR 0002 (Avanza gateway & fail-safe design), ADR 0003 (execution
   modes & risk limits).
4. Stop and wait for approval. No implementation code this session.
</how_to_work>

<output_format>
Short progress notes, the commands you ran, test results. Long content goes to
docs/. If unsure about any Avanza behavior, say so and verify via research or a
read-only call I approve — never guess.
</output_format>
```

---

## PART D — Phase prompts and promotion gates

Start each session with: "Read CLAUDE.md and docs/plans/00-master-plan.md. We're doing Phase N."

**Phase 1 — Skeleton + interop spine.** Solution, CMake presets, vcpkg manifest, build
script, qe_abi_version, engine create/destroy, qe_last_error, qe_bs_price_batch, C#
bindings + SafeHandle + resolver + layout tests. Add the PreToolUse hook check to
docs/setup.md. *Gate:* both suites green, BS reference passes, no leaks under ASan,
benchmark saved.

**Phase 2 — Pricing, risk, portfolio (C++).** Greeks, implied vol, binomial, MC;
covariance, VaR/ES, stress; optimizers and the integer-lot rebalance solver; batched ABI
calls; CLI verbs price/risk/optimize. *Gate:* every numerical test in
<verification_requirements> passes.

**Phase 3 — Avanza read-only gateway.** Re-read docs/research/avanza-endpoints.md
(refresh it if older than 7 days). Implement TOTP, AvanzaAuthenticator, AvanzaHttpClient,
routes, DTOs, mappers, AvanzaGateway READ methods only (accounts, positions, orders,
deals, search, chart history), recording/sanitizing tool. Write fixture tests. Then STOP
and tell me the exact read-only CLI commands to run myself (login, accounts, positions,
quote). I will run them and give you sanitized recordings. *Gate:* fixture tests green;
my manual read-only run succeeds; no secrets in logs.

**Phase 4 — Streaming + data store.** AvanzaStreamClient (protocol per research),
heartbeat/reconnect/staleness, Channels fan-out; DuckDB/Parquet history store with
known-at timestamps; Avanza chart importer; instrument master; optional vendor adapter.
*Gate:* replay tests from recorded stream fixtures; staleness flag test; known-at query
test.

**Phase 5 — Honest backtesting.** C++ event engine with limit-order fill model, tick
sizes, auctions, Avanza courtage; .NET TrialLedger, walk-forward, purged K-fold,
CSCV/PBO, Deflated SR, reports with all labels. *Gate:* leakage canary caught, random
strategy not significant, untouched limits don't fill, 10y×300 benchmark saved.

**Phase 6 — Trading core + Paper mode.** IBrokerGateway (Avanza/Paper/Backtest),
OrderGateway, PreTradeRiskEngine, OMS + reconciliation, KillSwitch, Scheduler, audit log,
architecture test. AvanzaGateway ORDER methods are implemented but covered only by
fixture tests. *Gate:* all trading tests green; Paper spy proves zero order calls.
*PROMOTION GATE → Confirm:* I run Paper mode myself for at least 10 trading days; the
EOD reports show no risk-rule violations, reconciliation always matched, and paper fills
vs. real market prices look sane.

**Phase 7 — Confirm mode (human in the loop).** Confirmation UX in CLI (and UI if
chosen): full order card + risk results; typed confirmation (e.g., the instrument ticker
+ "JA"); 30 s expiry; one order per confirmation. Rebalance --execute goes through the
same flow one order at a time. Add EOD execution-quality report. *Gate:* tests green.
You do NOT run it. I place my first real orders with the smallest possible size and
report back. *PROMOTION GATE → Auto:* at least 20 confirmed live orders; zero Unknown
states left unreconciled; realized slippage within the backtest's cost assumptions.

**Phase 8 — Auto mode (hard-limited).** Auto mode requires: promotion record file signed
by me (a checksum I generate), all <risk_limits> configured, KillSwitch tested that day
(startup self-test cancels a dummy paper order), market-hours check, and an alert channel
(email or desktop notification) working. Add a daily start-of-day checklist and an
automatic shutdown at a configured time. *Gate:* tests green; I enable Auto myself with
reduced limits for the first 2 weeks.

**Phase 9 — Hardening + docs.** Endpoint-drift canary: a scheduled read-only check that
validates key DTOs every morning before the open and disables trading on mismatch.
README, runbook (what to do on lockout, drift, kill switch), docs/testing.md, CI matrix.
*Gate:* fresh clone → build → tests in one command; runbook reviewed by me.

### Tips for driving Claude Code here
- Reference exact files with `@path` instead of letting it explore.
- If it fails twice on the same thing, `/clear` and restart with a sharper prompt.
- Use subagents for "research the current Avanza client code" so the main context stays clean.
- Before closing each phase: "Prove it — run every gate command and show the output."
- When Avanza changes something and the drift canary fires: start a new session with
  "Refresh docs/research/avanza-endpoints.md from the current open-source clients, diff
  against our routes/DTOs, and propose the minimal fix. Do not touch order logic without
  tests."
