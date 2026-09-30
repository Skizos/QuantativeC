# ADR 0003 — Execution modes, promotion gates and pre-trade risk limits

- **Status:** Accepted by the owner (2026-09-26); proposed 2026-09-25; amended 2026-09-27 (the account cap, see Changes). Phase 6 implements it for Backtest and Paper. Where the implementation made a choice the text leaves open, `docs/plans/06-phase6-trading-core.md` records it; for example, Paper fills pay the configured courtage class (Start) rather than Small.
- **Related:** `docs/prompts/master-prompt.md` `<risk_limits>` and Part D; ADR 0002; `docs/research/market-rules.md`

## Context

The same strategy code must run in **Backtest → Paper → Confirm → Auto**, promoted strictly in that order. The risk is asymmetric: one bad live order costs real money and can look like market abuse. So mode selection, promotion, and every pre-trade limit must be explicit, persisted, testable and conservative by default.

Decisions from kickoff:
- **ISK** account: no shorting, no leverage
- **OMXS30** universe
- **Small** courtage
- **CLI only**
- **Windows** primary

## Decision

### 1. Modes

| Mode | Data | Order channel | Human | Allowed when |
|---|---|---|---|---|
| **Backtest** (default) | `BacktestGateway` (store) | `BacktestOrderChannel` | – | always |
| **Paper** | live reads (Avanza, public endpoints where possible) | `PaperOrderChannel`, **no HTTP client for order routes is registered in DI** | – | always |
| **Confirm** | live | `AvanzaOrderChannel` | typed confirmation per order | promotion record ≥ Confirm, all `verified_on` set, no `trading-disabled` flag, ADR 0004 recorded |
| **Auto** | live | `AvanzaOrderChannel` | none (limits + kill switch) | promotion record = Auto **and** all Phase 8 prerequisites |

- **Effective mode:** `min(requested, promotionState.MaxAllowed)`.
  - The requested mode comes from `--mode` or `TRADING__MODE`.
  - A request above the allowed level fails at **startup** with a clear message. It is **not** silently downgraded.
- **Downgrade only at runtime:** the mode can be lowered while running (Auto → Confirm → Paper) but never raised; raising needs a restart.
- **Paper isolation:**
  - Paper's DI container never registers `avanza-order` or `AvanzaOrderChannel`.
  - A **spy handler** on all Avanza HTTP clients asserts zero requests to any order route. The route list comes from `AvanzaRoutes`, so new order routes are covered automatically.
- **Claude Code:** blocked from starting Confirm/Auto by `.claude/hooks/block-live-trading.sh` (self-tested in `tests/hooks/`). Nothing that starts live modes is on the permission allowlist.

### 2. The order pipeline (every mode)

```
Strategy → OrderIntent{instrument, side, qty, limit?, reason, decisionPrice, decisionTime}
  → IntentNormalizer   (lot size from orderbook.volumeFactor/tradingUnit; qty integral)
  → TickRounder        (buy: round DOWN, sell: round UP, using Avanza orderbook tickSizeList;
                        RTS 11 table cross-check → warning only)
  → PreTradeRiskEngine (all checks below; each returns RiskCheckResult{id, pass, observed, limit, message})
  → BrokerPreflight    (live modes only: Avanza validate + preliminaryfee — read-only;
                        any validate.valid=false ⇒ reject; fee diff vs model > 1 SEK ⇒ warning)
  → ModeGate           (Paper: simulate; Confirm: order card + typed confirmation; Auto: pass)
  → OrderGateway       (creates ApprovedOrder; ONLY caller of IBrokerOrderChannel)
  → OMS                (state machine, reconciliation)
```
- Every step writes an **audit record**:
  - append-only JSONL under `audit/YYYY-MM-DD.jsonl`
  - each record carries the SHA-256 of the previous record, making the log tamper-evident
  - request and response bodies are sanitized
- **Risk-check order:** the engine evaluates **all** checks, not fail-fast, so the Confirm card shows the complete picture. The order proceeds only if all pass.

### 3. Promotion state
- **Location:** `promotion/state.json`, local and git-ignored. `promotion/state.template.json` is committed with `maxAllowed: "Paper"`.
  ```json
  { "maxAllowed": "Paper", "records": [ { "to": "Confirm", "at": "…", "evidence": ["reports/eod/2026-…json", …],
      "evidenceSha256": "…", "operator": "…", "hmac": "…" } ] }
  ```
- **Promotion command:** `qa promote --to Confirm|Auto`. It is run **by you**, and Claude is blocked by the hook pattern `--mode`/`TRADING__MODE` plus a deny rule added for `qa promote` in Phase 6. The command:
  1. checks the gate criteria automatically from EOD reports and the OMS store:
     - **Confirm:** ≥ 10 Paper trading days, zero violations, reconciliation always matched
     - **Auto:** ≥ 20 confirmed live orders, zero unreconciled Unknowns, realized slippage ≤ backtest cost assumption
  2. prints them and asks you to type the target mode name
  3. writes a record whose `hmac` is HMAC-SHA256 over the canonical record, keyed by a **promotion key in Windows Credential Manager** that only you create (`qa promote --init-key`). This is "the checksum I generate" from Part D.
- **Startup:** Confirm/Auto verify the HMAC. A missing key, bad HMAC or unmet gate means the process will not start in that mode.

### 4. Pre-trade risk limits (defaults)

All limits are in `config/risk-limits.json`, validated at startup. **Auto refuses to start unless every limit is explicitly set** (no implicit defaults in Auto).

| # | Check | Default | Notes |
|---|---|---|---|
| R1 | Account allowlist | `AVANZA__ALLOWEDACCOUNTIDS` | Exactly one ISK expected; masked in logs (`***123`) |
| R2 | Instrument allowlist | OMXS30 universe file | Resolved by orderbookId, not ticker. An `exiting` share (taken off the list while held) may be sold only (2026-09-30, see Changes) |
| R3 | Order type | LIMIT only | `condition = NORMAL`; no FoK/FaK in v1; no market orders (none exist in the API anyway) |
| R4 | Side vs account | no short selling | Sell qty ≤ settled + pending position |
| R5 | Price collar | limit within **±2 %** of reference | Reference = last trade if fresh, else mid; evaluated **on the rounded price** |
| R6 | Max order value | **min(25,000 SEK, 10 % of account value)** | Account value from the latest reconciled positions + cash |
| R7 | Max position per instrument | **20 %** of account value | Post-trade, including working orders |
| R8 | Max gross exposure | **100 %** | No leverage |
| R9 | Available cash (buys) | value + **Avanza preliminary fee** (live) / model courtage (Paper/Backtest) + FX fee if non-SEK | Uses trading-accounts `availableForPurchase` |
| R10 | Max orders per day | **20** | Counts placements; Europe/Stockholm trading day |
| R11 | Max order actions per minute | **5** | Place + modify + cancel |
| R12 | Min interval same instrument | **5 s** between any place/modify/cancel | MAR hygiene (anti-churn) |
| R13 | No opposite working order | reject a buy if a sell is working in the same instrument (and vice versa) | Avoids self-cross / wash-trade patterns |
| R14 | Duplicate intent | identical (instrument, side, qty, limit) within **60 s** ⇒ reject | Independent of client order key |
| R15 | Stale data | quote older than **10 s** or depth stream disconnected ⇒ reject | ADR 0002 §3 |
| R16 | Trading window | continuous session only, **09:05–17:20** (half days **09:05–12:50**) | Default avoids auctions and the first minutes. Auction participation is opt-in per strategy. Calendar must be `verified_on`. |
| R17 | Halt state | any active `HaltController` reason ⇒ reject | Session, drift, reconciliation, circuit, stale, kill |
| R18 | Unknown orders | any `Unknown` order in the instrument ⇒ reject | Until reconciled |
| R19 | Daily loss stop | **−2 %** of start-of-day account value ⇒ **KillSwitch** | Mark-to-market from composed quotes, checked every 10 s and after every fill |
| R20 | Verified constants | courtage, calendar and tick-table sources have `verified_on` | Confirm/Auto only |
| R21 | Broker preflight | Avanza `validate` all `valid:true` | Confirm/Auto only; Paper logs it without blocking |

**The account cap (`max_account_value_sek`, added 2026-09-27, see Changes).** R6, R7, R8 and R19, and the plan's
investable equity, are sized on min(the account value, the cap). The committed file sets it to 5,000 SEK. Money added to
the account therefore never raises a limit:
- R6 is at most 10 % of the cap.
- R7 is at most 20 % of the cap.
- R8 is at most 100 % of the cap, counting every holding in the account.
- R19's loss limit is at most 2 % of the cap in SEK.

Each check has one **pass** and one **fail** unit test (Phase 6). Reduced-limit profiles (`config/risk-limits.auto-first-2-weeks.json`) are required for the first two weeks of Auto (Part D, Phase 8).

### 5. Confirm mode UX (CLI)
- **Order card:** account (masked), instrument (name, ticker, orderbookId, ISIN), side, volume, limit (rounded, showing rounding direction), SEK value, **Avanza fee** vs model fee, reason, decision price/time, current bid/ask/last with age, and **every** R-check with observed vs limit.
- **Confirmation:** type exactly `<TICKER> JA`, e.g. `ERIC-B JA`.
  - Anything else cancels.
  - **30 s expiry**, measured from card render, re-checked after input.
  - One order per confirmation.
  - Before sending, all R-checks are **re-evaluated**; if anything changed to fail, the order is cancelled.
- **Rebalance:** `rebalance --execute` runs the same flow one order at a time, re-planning after each fill or reject.
- **Local API:** v1 has **no order endpoints** (CLI-only decision). It exposes only `GET /health`, `GET /status` and `POST /kill`, bound to `127.0.0.1` and protected by a per-session random token printed at startup.

### 6. OMS and reconciliation
- **States:** `New → Sent → Working → PartiallyFilled → Filled | Cancelled | Rejected | Unknown`.
  - Transitions are table-driven; illegal transitions throw and trigger a halt.
- **Client order key:** a UUIDv7 persisted **before** the POST.
  - Sent as `requestId` if the captured web request confirms the field, but **not relied on**.
- **Submit outcomes:**
  - A POST timeout, transport error, 5xx or unparsable response ⇒ `Unknown`, **never retried**.
  - Avanza's `orderRequestStatus: "ERROR"` ⇒ `Rejected`, with `message` audited.
- **Reconciliation:**
  - **When:** every 30 s, on every SSE `ORDER` event, on stream reconnect, and on startup.
  - **Sources:** open orders (`trading/rest/orders`), deals, positions.
  - **Matching:** by Avanza `orderId` when known. For `Unknown`, by (account, orderbookId, side, price, volume, created ≥ sent−5 s), accepting only a unique match; ambiguity ⇒ `ReconciliationMismatch` halt.
  - **Unknown resolution:** an `Unknown` older than 2 min without a match is resolved as *not placed* only if two consecutive reconciliations agree and the deals list shows no fill. Otherwise it escalates to KillSwitch and an alert.

### 7. KillSwitch
- **Triggers:**
  1. `qa kill` (CLI)
  2. `POST /kill` on the local API
  3. **file flag `./KILL`** (FileSystemWatcher + 1 s poll fallback)
  4. automatic: daily loss stop (R19); drift or endpoint-gone on order routes; 3 consecutive rejects; `Unknown` unresolved > 2 min; reconciliation mismatch persisting > 60 s
- **On trigger:**
  1. set halt `KillSwitch`, blocking new orders
  2. **cancel all working orders** via `OrderGateway`, each cancel audited; cancel failures raise an alert and repeat at most every 10 s until Avanza reports no working orders
  3. alert (Windows toast + email in Phase 8; console + audit before then)
  4. persist `state/killed.json`
- **Reset:** `qa kill --reset`, run by you, only when:
  - no working orders exist
  - reconciliation is clean
  - it is outside or before the next session's start-of-day checklist
- **Self-test:** the Phase 8 daily startup self-test places and cancels a dummy **Paper** order through the kill path.

### 8. Paper fill model
- **Marketable limit** at entry (buy limit ≥ best ask): fills at the ask up to the displayed volume, and the remainder rests.
- **Resting limit:** fills only when a **subsequent trade prints through the limit**, i.e. last < buy limit or last > sell limit.
  - Touching the limit is not enough; that is conservative about queue position.
  - Fill quantity is capped at 10 % of the printed volume increment.
- **Costs:** model courtage (Small) is applied, plus the FX fee for non-SEK.
- **EOD comparison:** paper fills are compared against actual prints (VWAP over the fill window) as a sanity metric for the Confirm promotion gate.

## Alternatives considered

| Option | Why not |
|---|---|
| Mode set only by config | Too easy to flip accidentally. We need the promotion record + HMAC + startup gate. |
| Fail-fast risk engine | Faster, but the Confirm card would show only the first failure. |
| Allow market orders with a collar | The API exposes limit orders; the spec says limit-only. |
| Order endpoints in the local API with a token | CLI-only was chosen. Dropping them removes a whole attack surface; `POST /kill` is kept because it only reduces risk. |
| Trust `requestId` for idempotency | The server behaviour is unknown (ADR 0002). |

## Consequences
- **Promotion takes calendar time:** at least 10 paper days plus 20 confirmed orders before Auto. That is intended.
- **Some legitimate orders will be rejected** (stale quote during quiet periods, the 5 s same-instrument spacing). Rejections are logged and visible in EOD reports so limits can be tuned deliberately.
- **Every limit is testable in isolation** with a pass and a fail test, and the Confirm card doubles as a human-readable test of the risk engine.

## Changes

Every change to a limit is logged here. Lowering a limit in `config/risk-limits.json` needs no entry. Raising one above
the value in this ADR or its latest entry needs an entry first, written by the owner.

| Date | Change | Why | Where |
|---|---|---|---|
| 2026-09-30 | **R2's list: up to 10 names in Paper, and First North shares that trade continuously.** The list may hold 10 shares (Confirm, which streams each, still 5). A share outside Nasdaq Stockholm's main market is measured on its last week of 10-minute bars and joins only when it trades between Nasdaq's First North auction times; it pays First North's courtage (`marketplace_courtage`). A listed share later measured as auction-only is left out of the Paper decision (not traded), instead of stopping it. **No limit changes:** R6 (10 % per order), R7 (20 % per share), R8 and the 5,000 SEK account cap stay, so more names share the same capital. | The owner (2026-09-30): the app refused AIRA (First North), "why can't I add more than 5 instruments". The 5 was the stream budget, gone for Paper since it polls; First North was refused as a group although only its auction-model shares trade in auctions. | `docs/plans/22-first-north-and-ten-names.md` |
| 2026-09-30 | **R2: a share taken off the list while held may be sold, never bought.** `qa universe remove` (and the app's Remove) moves a share the Paper book still holds to the `exiting` list of `config/universe.json`; R2 passes a **sell** of an exiting share and fails a buy; R4 still caps the sell at the position, so it can only go to zero. The sessions quote exiting shares and target zero for them; they never count towards the five names. Once it is sold, the same command drops it. | The owner's request (2026-09-30, "build 3, 1 and 2 first", item 3 of the improvement review): a removed share was stranded, since R2 refused its sell and the session stopped quoting it, so it sat in the book at its last fill price. | `docs/plans/21-exits-dividends-splits.md` §A |
| 2026-09-27 | **Added the account cap** `max_account_value_sek`, committed at **5,000 SEK**. R6–R8, R19 and the plan are sized on min(account value, cap). R19's loss limit becomes 2 % of min(start-of-day value, cap) in SEK. The plan also clips buys to R8's room. | The owner asked for it after the handover checklist found that the limits grew with the whole ISK: a deposit would have raised the order, position and loss limits without anyone deciding it. | `docs/plans/07-phase7-confirm.md` "Addition: the account cap" |
