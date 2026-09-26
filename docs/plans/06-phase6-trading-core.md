# 06 — Phase 6: Trading core + Paper mode

- **Status:** steps 1–6 implemented and gated 2026-09-26 (see Results); step 7 (EOD report, promotion) next. Started 2026-09-26 at the owner's request.
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
| ADR 0003 status | **Accepted by the owner on 2026-09-26.** Phase 6 implements it for **Backtest and Paper**. | Nothing in Phase 6 can place a real order, so building to the proposal is safe and makes it concrete. |
| Who can create an order for a broker | `IBrokerOrderChannel`, `ApprovedOrder` and `OrderSubmitResult` live in Core. `ApprovedOrder` has an **internal constructor** visible only to `QuantAnalyst.Trading`, and inside Trading only `OrderGateway` creates one. An IL-scanning test checks that no other method calls the channel. | ADR 0002/0003: "only `OrderGateway` may reach order methods", enforced by the compiler and by a test. |
| Projects | New `src/QuantAnalyst.Trading` (depends on Core, Data, Analytics) and `tests/QuantAnalyst.Trading.Tests`. `AvanzaOrderChannel` lives in `QuantAnalyst.Avanza`. | The master plan's layout. |
| Paper account | A simulated book: starting cash and courtage class from `config/paper.json` (default **avanza-start, 5,000 SEK**, the owner's starting capital, the same as the backtest defaults). Positions, cash and fills persist in `state/paper/` (git-ignored). The account id is `PAPER`. R1 (account allowlist) applies to real accounts only. | Paper must not touch the real account; the book is the source of truth for Paper reconciliation. |
| Instrument allowlist (R2) | `config/universe.json`, keyed by **orderbook id**. It starts **empty**, so every order is rejected until you add names with `qa universe add <TICKER>` (from the instrument master, offline). | There is no OMXS30 membership file yet. An empty allowlist fails safe. |
| Risk limits | `config/risk-limits.json` with ADR 0003's defaults, validated at startup. With 5,000 SEK, R6 (min(25,000 SEK, 10 % of account value)) allows **500 SEK per order** and R7 **1,000 SEK per instrument**. | ADR 0003 §4, unchanged. The small cap is intended while testing; tune it deliberately later. |
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

**Step 6 (reconciler, CLI, Paper session):**
- **Reconciler.** One implementation serves both sources: the paper channel every day (resting orders plus the session's deals) and the read gateway for Phase 7 (`GatewayBrokerState`).
  - Live reconciliation **fails closed on the first live fill**, because the deals mapper refuses non-empty lists until a deal has been recorded. Recording one is a Phase 7 prerequisite.
  - An order at the broker that the OMS did not place counts as a mismatch. **Don't place manual orders in the app on the algorithm's account.**
- **Fill-model fix found while testing.** Several resting paper orders in one instrument each took 10 % of the same printed volume. They now share it, oldest first.
- **Reconciliation fills** are booked by total value (`ApplyFillValue`), so the order's filled value stays exact instead of going through a rounded average price.
- **`qa paper run` checks everything it can before the one login:** the promotion state, limits, paper config, allowlist, courtage class and calendar. It refuses to start while the kill switch is active.
- **The session loop** reconciles before it ticks the kill switch. So an Unknown order reaching 2 minutes is reconciled (and possibly resolved as "not placed") before the kill switch's > 2 min trigger looks at it.
- **Session lock.** `state/session.lock` stops a second session and an offline `qa kill --reset` under a running one. A lock left by a dead process is stale and gets taken over.
- **Promotion guard.** Hook rule 6 plus settings deny rules block the promotion command in any form, and any write to the local promotion state, for Claude.
  - The hook also stopped two of my own commit messages that quoted them, which is intended. Messages now go through a file.
- **Spy test race, fixed in the test.** The driver moved the fake clock while the CLI was still logging in. Under full-suite load, the kill file then existed before the kill switch was built, so the CLI took its correct refuse-to-start path instead of the in-session path the test expects.
  - The driver now holds the clock until the session streams.
  - The refuse-to-start path has its own test.

## Results

**Gate (2026-09-26): met in code and CI; the Paper days themselves are yours.**

| Gate item | Evidence |
|---|---|
| All trading tests green, one pass and one fail test per R1–R21 | `RiskEngineTests`: fail-alone and at-the-boundary theories for all 21 checks. Trading tests **210**, Avanza tests (incl. CLI, channel, architecture, spy) **257**; managed total **707**, 0 failed, two full runs. |
| Paper spy green | `PaperSpyTests`: a full `qa paper run` against the fake Avanza server decides at 09:10, places a paper buy of 63 ERIC B (R6-clipped to 4,500 SEK), fills it at the ask, reconciles clean. There are **zero requests to any order route**, and the only POSTs are login steps. There are also tests for a kill during the session, an active kill at startup, and the checks made before login. |
| Only `OrderGateway` calls `IBrokerOrderChannel`; only `AvanzaOrderChannel` uses order routes | `OrderArchitectureTests` (IL scan of all 7 production assemblies, positive controls, a mutation check), and the source-literal scan in `ArchitectureTests`. |
| Confirm/Auto cannot start | The gateway refuses them and any channel that is not simulated. `PromotionState.Effective` refuses modes above the record or not yet built. Both are tested, including against the real Avanza channel. |

**Not done in Phase 6 (by design or waiting on you):**
- **Step 7:** EOD report (fills vs VWAP, rule violations, reconciliation) and the owner's promotion command with an HMAC record. Both are needed before your 10 Paper days can be assessed.
- **A real web-app order capture** (buy and sell, sanitized) before Phase 7. It finalizes the order DTOs, including the `profit` question, and the deals format.
- **Answered by the owner (2026-09-26):**
  - starting capital **about 5,000 SEK**: `config/paper.json` and `config/backtest-defaults.json` use 5,000
  - the FX fee does not apply in every class: Start has none while under its limit (`costs.avanza-start.json` has 0; the other classes keep 0.25 %)
  - **ADR 0003 accepted**
- **Still open:** whether Avanza charges the minimum courtage once per order when it fills in parts. It doesn't matter on Start (no courtage), only on the other classes.
- **Consequence of 5,000 SEK under ADR 0003's limits:** 500 SEK per order (R6), 1,000 SEK per instrument (R7), 100 SEK daily loss stop (R19). A share priced above 500 SEK can't be bought at all, and a target is reached over at least two days. Changing that means changing `config/risk-limits.json`, which is your call.
