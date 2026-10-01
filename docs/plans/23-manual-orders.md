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
- **When:** inside the market's trading window (09:05–17:20 Stockholm, R16). A request placed outside it waits for the
  next window and **expires at that window's close** (it is never carried into a second day). A request is cancelled
  with `qa paper orders --cancel <id>` while it waits.
- **Price:** the owner's `--limit`, or by default **marketable**: the ask for a buy, the bid for a sell (it fills at
  once up to the shown volume; the rest rests at that price until the close, like any day order). Without a fresh
  quote the request waits for one.
- **Order of work:** a due manual order goes before queued strategy orders, paced like them (13 s, R11).
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

## Not in this plan

- Live manual orders (Confirm): a later step through the existing order card and typed confirmation.
- Market orders, stop orders, orders for shares not on the list.
