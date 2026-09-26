# 07 — Phase 7: Confirm mode (real orders, each one typed by you)

- **Status:** planned 2026-09-26 at the owner's request ("start on phase 7"). **Step 1 done 2026-09-26** (see Step
  notes). Phase 6 is complete; the usability work that comes with this plan (`qa status`, `qa paper strategy`,
  automatic history refresh) is done. Steps 7–8 wait on the owner's capture (O4, O5).
- **Scope:** master plan §4 Phase 7; ADR 0003 §2 (BrokerPreflight), §3 (startup HMAC check), §5 (Confirm UX), §6
  (live reconciliation); ADR 0002 (fail-safe gateway); CLAUDE.md "Absolute safety rules".
- **Gate:**
  - all tests green
  - the **Confirm spy**: over the fake Avanza server,
    - a session in which nothing is confirmed sends **zero** requests to an order route
    - each typed `<TICKER> JA` sends exactly **one** place request
    - an expired, wrong or re-check-failed confirmation sends none
  - the Paper spy is still green
  - Claude is blocked twice over: by the hook, and by the binary itself (below)
  - **I never run it.** You place the first minimal-size orders.
- **Not in Phase 7:**
  - Auto (Phase 8)
  - the local HTTP API (`/health`, `/status`, `POST /kill`) moves to Phase 8 with the alert channel. In Confirm you are at the terminal, and `qa kill` plus `./KILL` already work.
  - order modify (a day order that doesn't fill is cancelled at the close and re-decided the next morning)
  - non-SEK instruments

## Remaining work: exactly what is left until you can use it

### Paper (usable now)

Nothing is left for Claude to build. Your part is the one-time setup in `docs/guide.md` §2; `qa status` lists
whichever steps are still open:
- `qa history import` + `qa universe add` for 1–5 names
- a backtest
- `qa paper strategy …`
- then `qa paper run` every trading morning before 09:10

Recommended, not required for Paper:
- **O10:** the stream recording from plan 04's stop point, to replace the hand-written SSE fixture with a real one.

### Confirm (real orders)

Owner's part (**O**) and Claude's part (**C**). Confirm is usable when every line is done.

| # | Who | What | Depends on | How you know it's done |
|---|---|---|---|---|
| O1 | you | **10 clean Paper days in a row** with at least one order sent; earliest Friday 2026-10-09 if you start Monday 2026-09-28 | Paper setup | `qa report gate` says MET; `qa status` shows it |
| O2 | you | **Verify the trading calendar** 2026 and 2027 against Nasdaq Stockholm's page; set `verified_on` in `config/market-calendar.XSTO.<year>.json` (R20) | – | `qa status`: Calendar `ok` |
| O3 | you | **Verify the courtage file** of your class (`config/costs.avanza-start.json`, `verified_on`) against Avanza's price list (R20). Also answer: is the minimum courtage charged once per order when an order fills in parts? | – | `qa status`: Paper account `ok` |
| O4 | you | **Capture one real web-app buy and one sell** (1 share is enough) with the browser's DevTools open: copy each order call's request payload and response body into text files (never cookies or headers). This finalises the order format, including the unresolved `profit` field on sells (Qluxzz #156). | – | files handed to C7 |
| O5 | you | **Record one real deal**: after the O4 buy has filled, run `qa probe` once and sanitize the recording (`qa recordings sanitize …`); commit the fixture | O4 | a `deals` fixture with one fill |
| O6 | you | **Name the one account that may trade** (R1): set the user environment variable `AVANZA__ALLOWEDACCOUNTIDS` to your ISK's account id | – | the Confirm startup check passes R1 |
| O7 | you | **Review the live limits** in `config/risk-limits.json` for 5,000 SEK: 500 SEK per order, 1,000 SEK per name, and a 100 SEK loss stop. Keep or lower them; raising them needs a note in ADR 0003's log | – | – |
| O8 | you | **Promote:** `qa promote --init-key` (once), then `qa promote --to Confirm` | O1 | `qa promote --verify` OK |
| C1 | Claude | Pre-trade calls: `validate` and `preliminaryfee` | – | step 1 green |
| C2 | Claude | Live account state for the limits, and R1 from `AVANZA__ALLOWEDACCOUNTIDS` | – | step 2 green |
| C3 | Claude | The order card and typed confirmation (30 s, re-check, skip) | C1, C2 | step 3 green |
| C4 | Claude | The Confirm startup gate, live authorisation, hook rule 7, and the `CLAUDECODE` refusal | C3 | step 4 green |
| C5 | Claude | `qa trade run --mode confirm`, plus `qa rebalance` with its `--execute` variant; the Confirm spy | C4 | step 5 green |
| C6 | Claude | The live end-of-day execution-quality report and the Auto gate's numbers | C5 | step 6 green |
| C7 | Claude | Final order DTOs and deals mapper from O4/O5; removes the "provisional" flag | O4, O5 | step 7 green |
| C8 | Claude | Docs, a gate run, and the handover checklist | C1–C7 | step 8 |
| O9 | you | **First real orders:** `qa trade run --mode confirm`, minimal size, watching the cards | everything above | your first live end-of-day report |

## Research re-check (before step 1, per CLAUDE.md)

Before any route or field is written, WebFetch the current source of both reference clients and pin their commits in
`docs/research/avanza-endpoints.md`:
- Qluxzz/avanza: `constants.py` and `avanza.py`
- avanza-sdk-go: the order, validation and preliminary-fee files

Fields to confirm:
- **validate:** `POST /_api/trading-critical/rest/order/validation/validate` → `{commissionWarning, orderValueLimitWarning, priceRampingWarning, largeInScaleWarning, …}`, each `{valid}`
- **preliminaryfee:** `POST /_api/trading/preliminary-fee/preliminaryfee`, with body `{accountId, orderbookId, price, volume, side}` as strings, → `{commission, marketFees, totalFees, totalSum, currencyExchangeFee{rate,sum}}`

Nothing is taken from memory.

## Decisions I took (tell me if you want any changed)

| Question | Decision | Why |
|---|---|---|
| **The commands** | `qa trade run --mode confirm` runs the daily session with a card per order. `qa rebalance` lists what the strategy would trade now and sends nothing. `qa rebalance --mode confirm --execute` runs the cards now instead of at 09:10. `qa paper run` stays as it is (it equals `qa trade run --mode paper`). | Every live start carries `--mode confirm`, which the hook's rule 1 already blocks for Claude. You get one familiar session command. |
| **The confirmation text** | The ticker plus `JA`: `ERIC-B JA`, `ERIC B JA` or `eric-b ja`. The hyphen/space and case are normalised as everywhere else in qa. `JA` alone, `y`, Enter or another ticker **skips** that order. | ADR 0003 §5. Typing the ticker proves you read which order it is. |
| **30 s expiry** | Measured from the card's render with the injected clock, and checked again after the input line arrives. A correct answer after 30 s is a skip. | ADR 0003 §5. The clock is testable. |
| **Re-check after JA** | All of R1–R21, including a **fresh `validate`**, on a fresh quote, before sending. Anything now failing ⇒ skip, with the failing checks on the card. | ADR 0003 §5; prices move while you read. |
| **A skip** | An audited `confirm-skip` event, **not** a reject: it doesn't count towards "3 rejects in a row" and doesn't spoil a clean day. The next card is re-planned. | Declining is the human doing their job. |
| **Fee on the card** | Avanza's `preliminaryfee` next to the model's courtage. A difference of more than 1 SEK is flagged on the card, and R9 uses Avanza's figure. | ADR 0003 R9/R21. The class need not be trusted blindly. |
| **Account (R1)** | Exactly one account id in `AVANZA__ALLOWEDACCOUNTIDS`, and it must be an ISK in your accounts overview. It is unset, more than one, not found or not an ISK ⇒ Confirm refuses to start. Masked `***123` everywhere. | ADR 0003 R1: one ISK expected. |
| **Live account state** | Cash is `availableForPurchase` from the trading accounts. Positions come from the positions read. Value is positions at composed quotes plus cash. It is refreshed at start, after every fill or reject, and with every 30 s reconciliation. | ADR 0003 R6–R9 use reconciled values. |
| **Startup gate** | Confirm starts only when every check passes, else it prints the whole failing list: <br>1. the promotion verifies with your key (HMAC, mode chain, evidence) and allows Confirm <br>2. R20: the calendar year and courtage class are verified <br>3. the kill switch is off, no other session is running, and the audit chain is intact <br>4. R1 as above <br>5. the order DTOs and deals mapper are final (C7) <br>6. **not started from Claude Code** (below) | ADR 0003 §1 and §3. The same checks back `qa status`. |
| **Claude can't start it (defence in depth)** | 1. The hook's rule 1 blocks `--mode confirm\|auto`. <br>2. A new rule 7 blocks `qa trade` and `rebalance … --execute` regardless of flags. <br>3. The binary refuses Confirm and Auto whenever the `CLAUDECODE` environment variable is set (Claude Code sets it in every shell it starts). | CLAUDE.md: "YOU NEVER run the app in Confirm or Auto mode." Three independent locks. |
| **Where the live path opens** | `OrderGateway` keeps refusing non-simulated channels unless it is given a `LiveAuthorization`. Only the startup gate can create one (internal constructor), and only after every check above passes. `PromotionState.HighestImplemented` becomes Confirm in step 4. | The compiler and an architecture test keep the path closed everywhere else. |
| **Paper** | Unchanged: no `validate` or `preliminaryfee` calls, and the Paper spy still asserts that only login POSTs happen. | R21 "Paper logs without blocking" is met by the model fee; Paper stays free of order-adjacent POSTs. |
| **Execution-quality report** | For each live fill: slippage against the decision price and against the arrival mid, in bps, signed so positive is a cost; and Avanza's fee against the model. Totals and the mean are compared with the backtest's cost assumption (half-spread + slippage from the cost file). | Master plan Phase 7 "Report"; it feeds the Auto gate (≥ 20 confirmed live orders, zero unreconciled Unknowns, slippage within the assumption). |

## Steps (each ends green, committed and pushed)

1. **Pre-trade calls:**
   - the research re-check above
   - routes in `AvanzaRoutes` (a separate `AvanzaPreflightRoutes` group, never in `AvanzaOrderRoutes`)
   - Tier A DTOs and mappers, with fixtures shaped from the pinned sources and marked provisional
   - an `IBrokerPreflight` port
   - tests: strict deserialization, drift ⇒ halt, and `valid:false` ⇒ R21 fails
2. **Live account state and R1:**
   - an `AvanzaAccountState : IAccountState` from the positions and trading-accounts reads
   - an R1 allowlist from the environment
   - tests on the Phase 3 fixtures: values, masking, and every refusal case
3. **Order card and confirmation:**
   - an `OrderCard` renderer (every field of ADR 0003 §5, all R-checks with observed against the limit)
   - a `ConfirmationPrompt` over a `TextReader` and a `TimeProvider`
   - the gateway runs the re-check on `JA`
   - unit tests: a correct answer sends; wrong, late or empty answers skip; a re-check failure skips; a skip is audited, not a reject
4. **Startup gate and locks:**
   - a `ConfirmStartup` that runs the checks and returns a `LiveAuthorization`, with `HighestImplemented = Confirm`
   - hook rule 7 plus its self-tests
   - the `CLAUDECODE` refusal
   - architecture test: nothing but `ConfirmStartup` creates a `LiveAuthorization`
5. **CLI:**
   - `qa trade run --mode confirm`, and `qa rebalance` with and without `--mode confirm --execute`
   - `qa status` learns the Confirm checks
   - **Confirm spy** over the fake server: the gate cases above, one place request per `JA`, every request audited
6. **Reports:**
   - the live execution-quality section in the end-of-day report
   - the Auto gate lines in `qa report gate`
   - tests on audit fixtures
7. **Owner's capture** (after O4/O5):
   - final order DTOs (new/delete, including `profit`)
   - the deals mapper for real fills
   - `AvanzaOrderRoutes` marked final
   - live reconciliation replayed on the recorded deal
8. **Docs and gate:**
   - `docs/cli.md`, `docs/guide.md` §8 made current, and this plan's Results
   - the full test run with output, and the handover checklist for O9

## Step notes

**Step 1: pre-trade calls (done 2026-09-26).**
- **Research:** both clients are unchanged since their pins. Qluxzz has neither route. The Go SDK's `trading/service.go` and `types.go` @ `43f3902` give both routes and every field (`docs/research/avanza-endpoints.md`). Neither client has a real recorded answer, so the DTOs are marked provisional.
- **Routes:** `AvanzaPreflightRoutes` in the routes file (`preflight.validate`, `preflight.fee`), with `RoutesVersion` 2026-09-26.2.
  - Internal, Tier A, POST.
  - Not in `AvanzaRoutes.All`, not order routes, and not matched by the forbidden-route pattern.
  - Only `AvanzaPreflight` uses them (IL-scanning architecture test).
- **DTOs:** strict Tier A. The validate body mirrors the Go struct with explicit nulls. The fee body is all strings, with the price written without trailing zeros (`"70.85"`).
  - Money strings accept a dot or a Swedish comma, e.g. `"3 426,16"`.
  - An unreadable or negative amount, or a currency that is not a 3-letter code, is drift.
- **Port:** `IBrokerPreflight` in Core, returning a `PreflightOutcome` (validation, fee, fault, problem) and never throwing for broker failures:
  - drift ⇒ `SchemaDrift`
  - 401/403 ⇒ `SessionExpired`
  - 404 ⇒ `EndpointGone`
  - other failures (after the read pipeline's retries) ⇒ no fault, and the part is missing
  - a validation fault skips the fee call
- **Trading:**
  - `BrokerPreflight.From(outcome)` feeds R21: a missing validation fails R21 live, and `valid:false` fails it and names the check.
  - `FeeComparison` gives R9 Avanza's fee (plus FX fee) when it is in SEK, else the model's, and flags differences above 1 SEK.
  - The gateway wiring comes with step 3, once a live mode can exist at all.
- **`qa probe --preflight [--account 123]`:** records the real answers for a hypothetical 1-share buy at the ask; nothing is placed. The recorded routes are then parsed strictly by `RecordedFixtureTests` like every other route. This lets you confirm the DTOs **before** O4.
- **Open question for you:** ADR 0003 makes any `valid:false` a hard reject. If Avanza's `commissionWarning` turns out to fire for ordinary small orders (the probe will show it), that rule needs your decision.
- **Tests:**
  - Avanza: 22 preflight, 3 architecture and 1 CLI.
  - Trading: 6 R21 and fee tests.

## Test map (planned)

| Area | Must show |
|---|---|
| Preflight | strict DTOs; `valid:false` ⇒ R21 fail; fee difference > 1 SEK flagged; 401/403 ⇒ session halt; 404 ⇒ endpoint gone |
| Account state | cash, positions and value from fixtures; R1 unset, several ids, an unknown id and a non-ISK are each refused; ids masked in output and audit |
| Confirmation | exact and normalised tickers accepted; `JA` alone, `y`, another ticker, empty and EOF skip; 30.0 s passes, 30.1 s skips; a re-check failure after `JA` skips; one send per `JA` |
| Startup gate | each check failing alone refuses the start and is listed; all passing yields a `LiveAuthorization`; `CLAUDECODE` set refuses |
| Architecture | only `ConfirmStartup` creates `LiveAuthorization`; only `OrderGateway` calls `IBrokerOrderChannel`; preflight routes are not order routes; still no transfer routes |
| Hook | rule 7 blocks `qa trade …` and `rebalance --execute` in every spelling; rules 1–6 unchanged (the self-test count grows) |
| Confirm spy | zero order requests without `JA`; exactly one per `JA`; none after a skip; the kill switch during a card cancels it and sends nothing |
