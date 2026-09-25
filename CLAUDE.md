# QuantAnalyst for Avanza

Quant research, backtesting, portfolio/risk analytics, and gated trading through
Avanza Bank (unofficial web API). Swedish market focus (Nasdaq Stockholm, First North),
base currency SEK.

## Stack
- .NET 10 (LTS), C# latest, nullable on, warnings as errors.
- C++20, CMake + Ninja + vcpkg. Eigen, GoogleTest, Google Benchmark, optional QuantLib 1.42.x.
- Interop: flat C ABI in native/include/qe_api.h via [LibraryImport]. No C++/CLI.

## Layout
- native/                        C++ QuantEngine + C ABI shim
- src/QuantAnalyst.Core          domain types (no I/O)
- src/QuantAnalyst.Native        P/Invoke bindings, SafeHandles
- src/QuantAnalyst.Avanza        Avanza gateway: auth, REST, push stream, DTO mapping
- src/QuantAnalyst.Data          history store (DuckDB/Parquet), vendor adapters
- src/QuantAnalyst.Trading       OMS, pre-trade risk, execution modes, kill switch
- src/QuantAnalyst.Analytics     risk, portfolio, backtest runner, TrialLedger
- src/QuantAnalyst.Api           ASP.NET Core minimal API (localhost only)
- src/QuantAnalyst.Cli           CLI incl. order-confirmation prompts
- tests/  bench/  docs/adr/  docs/plans/  recordings/fixtures/

## Commands
- Native:  cmake --preset dev && cmake --build --preset dev && ctest --preset dev --output-on-failure
- Managed: dotnet build QuantAnalyst.sln && dotnet test --solution QuantAnalyst.sln   (Microsoft.Testing.Platform, see global.json)
- All:     ./build.ps1 | ./build.sh

## Absolute safety rules
- YOU (Claude Code) NEVER run the app in Confirm or Auto mode and never call a live
  order endpoint. Live Avanza calls during development are READ-ONLY and only when I
  explicitly ask in this session.
- Tests use recorded/sanitized fixtures only. No network in CI.
- Order code paths are reachable only through Trading.OrderGateway, which enforces
  mode, account allowlist, and pre-trade limits. No other class may call the order endpoints.
- Never log or print passwords, TOTP secrets, TOTP codes, session cookies, security
  tokens, or full account IDs (mask to last 3 digits).
- Login is attempted at most once per trigger; on failure → stop and alert. Never loop.
- No money movement: never implement transfer, withdrawal, deposit or payment endpoints (ADR 0004).

## Avanza gateway rules
- Avanza has no official API. Before implementing or changing any endpoint, WebFetch the
  current source of a maintained open-source client (e.g. github.com/Qluxzz/avanza,
  constants/routes file) and note the commit URL in docs/research/avanza-endpoints.md.
  Never invent endpoints or fields from memory.
- All endpoints/paths live in ONE routes file. All JSON goes through versioned DTOs with
  strict deserialization, tiered per ADR 0002: Tier A (trading-critical) unknown or missing
  required fields ⇒ SchemaDriftException ⇒ halt trading; Tier B (informational) missing
  required ⇒ SchemaDriftException (feature disabled), unknown fields logged.
- Rate-limit outbound calls (token bucket, conservative defaults), jittered backoff on
  429/5xx, circuit breaker. 401/403 ⇒ session expired ⇒ halt order flow, single re-login attempt.
- Instruments are identified by Avanza orderbookId, mapped to ISIN + ticker in the store.

## Interop rules (C ABI)
- Only blittable types cross; int32 flags instead of bool; fixed-layout structs.
- Every export noexcept, returns qe_status, results via out-pointers; errors via qe_last_error.
- Opaque handles wrapped in SafeHandle; no finalizers. Batch APIs, not per-scalar calls.
- Callbacks via delegate* unmanaged + [UnmanagedCallersOnly]. UTF-8 strings only.
- qe_abi_version() checked at startup; struct size/offset tests on both sides.

## Numerical & money rules
- Models use double. Money, prices sent to Avanza, fees, cash use C# decimal.
- Order prices are rounded to the instrument's valid tick size BEFORE risk checks.
- All randomness seeded; seed stored with every result. MC results carry SE + path count.
- Time: store UTC, display Europe/Stockholm. Trading calendar = Nasdaq Stockholm.

## Backtesting rules
- Point-in-time data; flag non-survivorship-free sources (Avanza history is one).
- Signal at bar t uses data known at ≤ t; fills at t+1 or later, limit-order fill model.
- Costs always on: Avanza courtage for the configured class, spread, slippage, FX fee
  for non-SEK instruments. Fetch current courtage table; don't hard-code from memory.
- TrialLedger logs EVERY evaluation, including yours. Report raw SR, Deflated SR, PBO.
- Final holdout window locked unless I unlock it.

## Workflow
- Plan first for >2 files (docs/plans/). Verify every change with tests you ran; show output.
- Two failed fixes ⇒ stop, summarize, propose a new approach.
- Look up versions/APIs via WebSearch/WebFetch before use; cite URLs in ADRs.
- Use subagents for broad exploration. Commit after each green step.
