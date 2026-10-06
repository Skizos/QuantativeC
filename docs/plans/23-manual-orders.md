# 23 — Buying and selling by hand (Paper)

- **Status:** done 2026-10-01 (planned and built the same day), at the owner's request: "add an update that allows for manual buy and sale". The
  owner chose (2026-10-01): **Paper only** (live manual orders may come later, through Confirm's order card), and a
  share traded by hand is **left alone by the strategy** until the owner releases it.
- **Gate:** tests for every part; all managed tests green; nothing live (Paper never sends orders to Avanza).

## How it works

A manual order is a **request** the owner places with `qa paper manual buy|sell` or the app. The **Paper session** is
the only process that trades (it holds the session lock, the book and the live prices), so it picks the request up and
sends it through the same pipeline as the strategy's orders: `OrderGateway` (tick rounding, R1–R21, the OMS) and the
paper channel's fill model on live quotes. Nothing bypasses the risk checks: R2 (the list; an exiting share sells
only), R4 (no selling more than held), R6 (10 % of the account per order), R7, R8, R5's price collar, the trading
window, the daily loss stop and the kill switch all apply.

- **Requests** are files in `state/paper/manual/` (one per request, written atomically). A running session takes due
  ones within a second; without a session they wait for the next one. Each outcome is appended to
  `state/paper/manual/done.jsonl` and the audit (`manual-order` records).
- **When:** inside the market's trading window (09:05–17:20 Stockholm, R16). A request is for the **first window that
  ends after it is placed** (one placed after 17:20 is for the next trading day). Not sent by then, it **expires at that
  day's close** (in that day's report), or at the next session's start if none ran; it is never carried into a second
  day. A request is cancelled with `qa paper orders --cancel <id>` while it waits.
- **Price:** the owner's `--limit`, or by default **marketable**: the ask for a buy, the bid for a sell (it fills at
  once up to the shown volume; the rest rests at that price until the close, like any day order). Without a fresh
  quote the request waits for one.
- **Order of work:** a due manual order goes before queued strategy orders, paced like them (13 s, R11). After the
  cancels it causes it waits until R12 allows an action on the share again (5 s): the session asks the gateway
  (`NextActionAt`) before each order, so neither a manual nor a strategy order is sent only to be refused by R11/R12.
- **The strategy leaves the share alone:** when a manual order is sent, the share is marked **manual** in the book; the
  strategy's queued orders for it are dropped and its working ones cancelled. Every later decision holds the share
  ("X: yours (manual) …"). `qa paper release <TICKER>` (or the app's Release) gives it back: from the next decision the
  strategy trades it to its target again. A manual order the risk checks reject does not mark the share.
- **Reports:** the end-of-day report counts manual orders and lists them; the weekly summary says when a week had
  manual trades (Paper is then not the strategy alone, so the comparison with the backtest is weaker).

## Commands

- `qa paper manual buy|sell <TICKER> <QUANTITY> [--limit <PRICE>]` — places a request (offline; works while a session
  runs). The ticker must be on the list (a sell also takes an exiting share). Buy and sell are an argument, not commands
  of their own: the command tree keeps no verb named after a broker order (`CliAvanzaTests`).
- `qa paper orders [--cancel <ID>]` — the waiting requests and today's outcomes; `--cancel` removes a waiting request.
- `qa paper release <TICKER>` — gives a manual share back to the strategy (a request too, applied by the session).
- `qa paper status` shows the manual shares.

## In the app

The Trading page gains a **Buy or sell by hand** card: a share from the list, Buy/Sell, a quantity, an optional limit,
**Place**; the waiting requests and today's outcomes; and each manual share with **Release**. It writes the same
requests as the CLI, so it works whether or not a session is running.

## Done (2026-10-01)

| Part | Code | Tests |
|---|---|---|
| Requests | `ManualOrderInbox` (files in `state/paper/manual/`, taken by moving to `taken/`, outcomes in `done.jsonl`), `ManualOrderRequest`, `ManualOrderOutcome` | `ManualOrdersTests.TheInbox_…` |
| The session | `ManualOrderDesk` (due requests to intents at the ask/bid or the owner's limit; releases; expiry after a close; a request a stopped session took is reported, never resent), `PaperSession.Manual` (the share becomes manual, the strategy's queued orders dropped and working ones cancelled, the manual order first; a refused one leaves the share the strategy's) | `ManualOrdersTests` (goes first at the ask and the strategy's buys are dropped; cancels a working strategy order; R6 refusal; expired and released; outside the window and without a price it waits; interrupted) |
| The book and the decision | `PaperBook.ManualHolds` (persisted), `ManualIn`; the Paper decision holds manual shares ("yours (manual) …"); `qa paper status` lists them | `ManualOrdersTests.TheBook_KeepsItsManualShares`, `PaperSpyTests.AManualBuy_IsSentBeforeTheDecision_…` |
| Commands | `qa paper manual buy\|sell`, `qa paper orders [--cancel]`, `qa paper release` (`ManualTrading`) | `CliTradingTests.PaperOrder_…`, `CliAvanzaTests.CommandTree_HasNoOrderOrTransferVerbs` (still no verb named buy, sell, order or cancel) |
| Reports | `EodReport.ManualOrders`/`ManualOrdersSent` and the summary; the weekly summary's "Manual:" line | `ManualOrdersTests.AManualBuy_…` (the day's report), `WeeklyReportTests.AWeekWithManualOrders_…`, `PaperSpyTests.AManualBuy_…` |
| The app | The Trading page's "Buy or sell by hand" card (`SessionViewModel` manual properties and commands; `SessionView.xaml`) | `ManualOrderCardTests`, the XAML resource and binding checks, `AppSafetyTests` (the app still names no order plumbing) |

The WPF card itself can't be opened in the Linux CI: look at it on Windows.

## Review (2026-10-05)

A review of plans 21–23 found these holes; each is fixed with a test.

| Hole | Fix | Test |
|---|---|---|
| The cancels a manual order causes count for R12 (5 s per share), and the manual order could go in the same second: R12 refused it after the strategy's orders were already cancelled | `OrderGateway.NextActionAt` (when R11 and R12 would pass); the session sends the first queued order that is ready | `ManualOrdersTests.AManualOrderRightAfterTheCancelsItCaused_…` (fails without the fix), `OrderGatewayTests.NextActionAt_…` |
| A request placed between the window's end (17:20) and the close (17:30) expired the next morning without a chance | Expiry compares with the previous **window** end (`ManualWindow.PreviousWindowCloseUtc`) | `ManualOrdersTests.ARequestPlacedAfterTheWindowClosed_…` |
| A request never sent (no live price) waited silently until the next session | `ManualOrderDesk.ExpireAtClose` at each market's close, before the day's report | `ManualOrdersTests.ARequestStillWaitingAtTheClose_…` |
| A busy or locked `done.jsonl` (a CLI cancel at the same moment, on Windows) would have thrown inside the session | Retries, then a note in the session's output; the audit keeps the outcome | `ManualOrdersTests.AnOutcomeThatCannotBeWritten_…` |
| `qa status` said nothing about waiting requests or manual shares | A **Manual** line for each | `CliTradingTests.PaperOrder_…` |
| `qa status` crashed when `paper.json` named a cost file that is missing (found while testing the line above) | `BacktestConfigException` is a FAIL line like other broken files | `CliUsabilityTests.Status_ReportsABrokenFile_…` |
| `qa status` still advised "Add names (up to 5)" after plan 22 raised the list to 10 | Says `Allowlist.MaxNames` | — |

**Still open:** `ForeignSharesAppTests.TheInstrumentsList_SaysAUsSharesCurrencyAndMarket` fails now and then in a full
solution run (its US share's line read "(store busy)": the Instruments page could not open the test's own history
store). A first guess, a race with the Status page's first refresh, was wrong: the shell starts no refresh when it is
built, and each test has its own store, so that change was taken back. The test failed once in 9 full runs after it
and never alone; a diagnostic build that put the exception into the text ran 6 full runs without a failure, so the
cause is not known yet.

Second look (2026-10-06), still no failure to learn from:
- It has never failed on CI (Linux and Windows).
- 600 parallel cycles of the test's pattern passed, writing a store, closing it, then opening it on another thread
  and reading. So did 300 parallel runs of the test itself.
- With exception logging on, 6 runs of the Desktop tests and 6 full solution runs passed too.
- DuckDB.NET shares one database per file within a process and reference-counts it, and nothing else in the test
  touches its store. A lock race inside the test is therefore unlikely.

The test now asserts `InstrumentsViewModel.StoreError` first, which holds why the last refresh could not read the
store. The next failure prints the exception instead of "(store busy)".

## Not in this plan

- Live manual orders (Confirm): a later step through the existing order card and typed confirmation.
- Market orders, stop orders, orders for shares not on the list.
