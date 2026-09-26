# 06 — Phase 6: Trading core + Paper mode

- **Status:** in progress (started 2026-09-26 at the owner's request, while Phase 4's live stream recording waits for Monday).
- **Scope:** master plan §4 Phase 6; ADR 0002 (fail-safe gateway, two ports); ADR 0003 (modes, pipeline, R1–R21, OMS, kill switch, paper fills); CLAUDE.md "Absolute safety rules".
- **Gate:**
  - all trading tests green, including one pass and one fail test per risk check R1–R21
  - the **Paper spy** is green: a Paper run over the fake Avanza server makes **zero** requests to any order route
  - architecture tests: only `OrderGateway` calls `IBrokerOrderChannel`; only `AvanzaOrderChannel` uses order routes
- **Not in Phase 6:**
  - **Confirm and Auto cannot start.** The mode gate refuses them outright, whatever the promotion record says. They arrive in Phases 7 and 8.
  - **No order is ever sent to Avanza.** `AvanzaOrderChannel` exists and is tested against fixtures only. Claude never runs anything live.
  - **Local API** (`/health`, `/status`, `POST /kill`): deferred to Phase 7. The kill switch works through `qa kill` and the `./KILL` file.

## Research re-check (2026-09-26)

Both reference clients are unchanged since the pins in `docs/research/avanza-endpoints.md`:
- Qluxzz `a6a18a9` (2026-09-21): place and delete at `/_api/trading/order-entry/order/{new,delete}`, modify at `/_api/trading-critical/rest/order/modify`.
- avanza-sdk-go `43f3902` (2026-07-05): `requestId`, `metadata`, `orderRequestStatus: SUCCESS|ERROR`, `validate`, `preliminaryfee`.

The order DTOs stay **provisional** until the owner captures a real web-app order (including the unresolved sell-side `profit` field, Qluxzz #156) before Phase 7.

## Decisions I took (tell me if you want any changed)

| Question | Decision | Why |
|---|---|---|
| ADR 0003 status | It is still "Proposed". Phase 6 implements it as written for **Backtest and Paper**. Please accept or amend it before Phase 7, since Phase 7 sends real orders. | Nothing in Phase 6 can place a real order, so building to the proposal is safe and makes it concrete. |
| Who can create an order for a broker | `IBrokerOrderChannel`, `ApprovedOrder` and `OrderSubmitResult` live in Core. `ApprovedOrder` has an **internal constructor** visible only to `QuantAnalyst.Trading`, and inside Trading only `OrderGateway` creates one. An IL-scanning test checks that no other method calls the channel. | ADR 0002/0003: "only `OrderGateway` may reach order methods", enforced by the compiler and by a test. |
| Projects | New `src/QuantAnalyst.Trading` (depends on Core, Data, Analytics) and `tests/QuantAnalyst.Trading.Tests`. `AvanzaOrderChannel` lives in `QuantAnalyst.Avanza`. | The master plan's layout. |
| Paper account | A simulated book: starting cash and courtage class from `config/paper.json` (default **avanza-start, 45,000 SEK**, the same as the backtest defaults). Positions, cash and fills persist in `state/paper/` (git-ignored). The account id is `PAPER`. R1 (account allowlist) applies to real accounts only. | Paper must not touch the real account; the book is the source of truth for Paper reconciliation. |
| Instrument allowlist (R2) | `config/universe.json`, keyed by **orderbook id**. It starts **empty**, so every order is rejected until you add names with `qa universe add <TICKER>` (from the instrument master, offline). | There is no OMXS30 membership file yet. An empty allowlist fails safe. |
| Risk limits | `config/risk-limits.json` with ADR 0003's defaults, validated at startup. With 45,000 SEK, R6 (min(25,000 SEK, 10 % of account value)) allows **4,500 SEK per order**. | ADR 0003 §4, unchanged. The small cap is intended while testing; tune it deliberately later. |
| When Paper decides | Phase 5's daily strategies decide once per trading day, **after the open** (default 09:10 Stockholm), on daily bars through **yesterday's close**. Their orders are day limits placed right after. | The same timing as the backtest: decide on bar t, trade on t+1. No intraday bar has to be invented. |
| Limit price in Paper | The live reference price (last trade if fresh, else mid) ± the offset, rounded passively to the tick. | ADR 0003 R5 collars limits at ±2 % of the live reference, so anchoring on yesterday's close would be rejected after most gaps. The backtest anchors on the decision close; this difference is stated in the Paper report. |
| Paper fills | ADR 0003 §8: a marketable limit fills at the ask (buys) or bid (sells), up to the displayed volume, and the rest rests. A resting limit fills only when a later trade **prints through** it, capped at 10 % of the printed volume increment. Courtage and FX fee come from the courtage class. | Conservative about queue position, and it matches the backtest's "touch is not a fill". |
| Audit log | `audit/YYYY-MM-DD.jsonl` (git-ignored), one record per pipeline step. Each record carries the SHA-256 of the previous one, continuing across days. `qa audit verify` checks it. Account ids are masked; there are no secrets in it. | ADR 0003 §2. |
| Kill switch | Triggers: `qa kill` (writes `./KILL`), the `./KILL` file itself (watched, with a 1 s poll fallback), and the automatic triggers of ADR 0003 §7. On trigger: halt, then cancel every working order **through `OrderGateway`**, persist `state/killed.json`, and alert on the console and in the audit log. `qa kill --reset` only clears it when there are no working orders. | ADR 0003 §7; one kill path for Paper and live. |
| Trading window (R16) | 09:05–17:20, half days 09:05–12:50, from the XSTO calendar. In Paper an **unverified** calendar is allowed with a warning; R20 blocks it in live modes. | Paper should run before you verify the calendar; live must not. |
| Reconciliation | The live reconciler (open orders, deals, positions; unique-match rule; mismatch ⇒ halt) is built and **fixture-tested** now. In Paper it reconciles the OMS against the paper book on the same schedule, so the code runs every day. | ADR 0003 §6. |
| Promotion | `promotion/state.json` is read at startup; if it is missing, the committed template applies (`maxAllowed: Paper`). `qa promote` (HMAC record, gate checks from EOD reports) comes at the end of this phase, before your 10 Paper days are over. It gets a **settings deny rule and a hook pattern** so Claude cannot run it. | ADR 0003 §3. |

## Pipeline (every mode)

```
Strategy targets → OrderIntent
  → IntentNormalizer   whole lots, positive quantity
  → TickRounder        buy DOWN, sell UP (instrument tick table)
  → PreTradeRiskEngine R1–R21, all evaluated (not fail-fast), each with observed vs limit
  → ModeGate           Backtest/Paper: pass to the simulated channel; Confirm/Auto: refused in Phase 6
  → OrderGateway       the ONLY creator of ApprovedOrder and the ONLY caller of IBrokerOrderChannel
  → OMS                New → Sent → Working → PartiallyFilled → Filled | Cancelled | Rejected | Unknown
```

Every step writes an audit record. A submit timeout, transport error, 5xx or unreadable response becomes **Unknown**; it is **never retried**, and it blocks that instrument (R18) until reconciled.

## Steps (each ends green, committed and pushed)

1. Plan (this file) and the research re-check note.
2. Trading core: order types in Core, audit log, `HaltController`, risk-limit config, `PreTradeRiskEngine` with R1–R21.
3. Pipeline: normalizer, tick rounder, mode gate with promotion state, `OrderGateway`, OMS state machine.
4. Paper channel with the fill model, kill switch, scheduler.
5. `AvanzaOrderChannel` (fixture-tested), the Paper spy and the architecture tests.
6. Reconciler, CLI (`qa paper run`, `qa kill`, `qa audit verify`, `qa universe add|list`, `qa risk-limits`), docs, gate.
7. EOD report and `qa promote` (the owner's command), before the Paper promotion window closes.

## Test map

| Area | Tests |
|---|---|
| Risk engine | one pass and one fail test per check R1–R21; all checks are reported even when an early one fails; limit-file validation |
| Pipeline | lots and tick rounding (buy down, sell up); Confirm/Auto refused; a requested mode above the promotion state fails at startup, never silently downgraded |
| OMS | every legal transition; illegal transitions throw and halt; Unknown on timeout/5xx/unparsable, never retried; R18 blocks the instrument |
| Paper fills | marketable limit fills at the ask up to displayed volume; a touch is not a fill; trade-through fills capped at 10 % of the volume increment; costs from the courtage class |
| Kill switch | each trigger halts and cancels all working orders through `OrderGateway`; reset refused while orders are working |
| **Paper spy** | a full Paper run against the fake Avanza server: zero requests to any order route (the list comes from `AvanzaRoutes`) |
| **Architecture** | IL scan: only `OrderGateway` calls `IBrokerOrderChannel`; only `AvanzaOrderChannel` references order routes; Paper DI registers no Avanza order channel |
| Avanza channel | provisional request bodies; SUCCESS → Working with the order id; ERROR → Rejected with the message; timeout/5xx/404/garbage → Unknown, no retry |
| Audit | hash chain verifies across days; an edited or deleted line is detected; account ids masked |

## Step notes

**Step 3 (pipeline, OMS, gateway):**
- A fill can reach the OMS before the channel's submit reply (a marketable paper order). The fill proves the order exists, so the order moves Sent → Working → fill. If the reply is then lost, the order keeps its fills; it is not made Unknown.
- Transitions that race with fills use a compare-and-set (`TryTransition`), so a late reply or a cancel can never trip the OMS invariant halt.

**Step 4 (Paper channel, book, kill switch, schedule):**
- **Trade-through fills are priced at the limit.** Only the last print is known, so the model takes the worst price the order allows.
- **The marketable test runs once, at entry,** as ADR 0003 §8 says. A resting order whose limit the market later crosses fills only on trade-throughs.
- **Courtage is charged per order, not per partial fill.** Each fill pays courtage(cumulative value) minus what the order has already paid, so the minimum fee is paid once per order. **Owner to confirm:** does Avanza charge the minimum once per order (per day) when an order fills in several parts? Per-fill charging would make many small paper fills look much more expensive.
- **Kill-switch cancels run on the 1 s tick, not inside `Trigger`.** Automatic triggers fire inside a gateway call (for example, the third broker reject), and cancelling there would wait on that same call. The halt itself is immediate.
- `state/killed.json` makes a kill survive restarts. An unreadable file still counts as killed.
- `Reset` refuses while any order is open or Unknown, or while a reconciliation halt is active.
- The daily loss stop fires at **−2 % or worse**, the same boundary at which R19 starts rejecting.
- **The paper book** lives in `state/paper/book.json`, rewritten atomically after every fill, with the fills appended to `fills.jsonl`.
  - An existing book wins over `config/paper.json`, and a note says so. A damaged book is refused, never replaced.
  - The first snapshot of each Stockholm day fixes the start-of-day value used by R19.

**Step 5 (Avanza order channel, architecture tests):**
- **`AvanzaOrderChannel` can't be reached in Phase 6.** It has an internal constructor, and it can only be created through `AvanzaConnection.CreateOrderChannel()`, which is also internal and has no production caller. The gateway refuses it in every mode, and a test checks that too.
- **Its own pipeline: one attempt, no retries, no recording.** Every request is sent once, through rate limit, security token and cookies only. Order requests are never recorded, because their bodies carry the full account id.
- **How answers are classified** (the table is in `docs/research/avanza-endpoints.md` §4). A drift, a gone endpoint or an expired session is returned as a `BrokerFault`, and the gateway turns it into the matching halt (the first two also fire the kill switch).
- **IL architecture tests** (`OrderArchitectureTests`) read every production assembly and check that:
  - only `OrderGateway` calls an order channel or creates `Approved*`
  - only `AvanzaOrderChannel` touches `AvanzaOrderRoutes`
  - only `AvanzaConnection` builds the channel

  Each rule has a positive control. A mutation check (a rogue async `CancelAsync` caller added to Trading) made the test fail, as it should.
- **The source scan** now allows the three order-entry literals only inside `AvanzaOrderRoutes`. Stop-loss, fund-order and money-movement paths stay forbidden everywhere.
- **The Paper spy moves to step 6.** It needs `qa paper run` to exist, so it can check that a whole Paper session against the fake server sends zero order-route requests.

## Results

(Filled in at the gate.)
