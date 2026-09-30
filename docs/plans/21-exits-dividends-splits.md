# 21 — Shares off the list, dividends and splits

- **Status:** planned 2026-09-30, at the owner's request: "build 3, 1 and 2 first" (from the improvement review:
  3 = a share taken off the list is stranded, 1 = dividends are not handled, 2 = splits are not handled).
- **Gate:** tests for every part; all managed tests green; no live call (the new endpoint is tested on a fixture built
  from the Go SDK's recorded sample); the report and the gate still rebuild from the audit alone.

## A. A share taken off the list is sold, not stranded (item 3)

Today `qa universe remove X` deletes X from the allowlist. If Paper holds X, R2 then refuses every order for it, the
session stops quoting it, and the position stays in the book at its last fill price for good.

- `config/universe.json` gains an optional `exiting` list. `qa universe remove X` (and the app's Remove) moves a
  **held** share there; a share that is not held is deleted as before. Removing an exiting share that is still held
  is refused with the reason; once it is sold, the same command drops it.
- **R2** (ADR 0003 amendment): an exiting share passes R2 for **sells only**. R4 still caps a sell at the position, so
  an exiting share can only go down to zero. It never counts towards the five names.
- The Paper session quotes exiting shares, and the decision targets **zero** for them (the strategy's panel is the
  list only). R6's per-order cap still applies, so a large position is sold over several days.
- `qa status` shows each exiting share: still held (the next session sells it) or sold (the command that drops it).
- "Held" is the Paper book's position. Confirm reads the live account at run time; an exiting share is sold there
  the same way.

**A done 2026-09-30:** `Universe.Exiting`/`IsExiting`/`Exit` (the file's `exiting` list, written only when there is
one), R2 in `PreTradeRiskEngine`, `Allowlist.Remove` (held → exiting; exiting and held → refused; sold → dropped) with
`PaperBook.HeldIn`, `qa universe remove --state-dir`, `qa universe list`, the app's Remove (both ways), `qa status`
("Exiting" lines and the drop step), the Paper run (exiting shares quoted, target zero, the strategy's panel the list
only; a list of only exiting shares runs) and Confirm (`TargetsWithExits`). ADR 0003 Changes has the R2 entry. Tests:
`TradingConfigTests.AShareTakenOffWhileHeld…`, `RiskEngineTests.R2_LetsAnExitingShareBeSold…`,
`CliTradingTests.Universe_RemovingAHeldShare…` and `PaperSpyTests.AShareTakenOffTheListWhileHeld_IsSoldByTheNextSession`
(a real session sells the 7 held ERIC B at the bid, and `qa status` then says to drop it).

## B. Dividends (item 1)

Research (2026-09-30, both clients unchanged at `a6a18a9` / `43f3902`): `GET /_api/market-guide/stock/{id}/details`
is public (Go SDK `GetStockDetails`: "does not require an authenticated session") and returns
`dividends: { events[], pastEvents[] }`, each `{ exDate, paymentDate?, amount, currencyCode, dividendType }`, and
`stock.numberOfShares`. The Go SDK's recorded sample (Nvidia) shows past amounts **adjusted for the 2024 10:1 split**
(0.004 before, 0.01 after), so amounts are per current share.

- **Route and DTO:** `AvanzaRoutes.StockDetails`, Tier B (informational: a missing required field disables the
  feature for the day, it never halts trading). Only `dividends` and `stock` are typed; the other sections are
  accepted as they come. The fixture is the Go SDK's sample, pinned by commit, until the owner records a Stockholm
  share with `qa probe`.
- **Store:** `dividend_events` and `share_counts` tables (known-at versioned, like the bars). `qa history import` and
  the Paper session's history refresh fetch them for every listed, exiting and held share.
- **Paper:** at session start, **before the day's first valuation** (which fixes R19's start value), each held
  position is credited `quantity × amount` (SEK at the day's rate for a foreign share) for every ex-date after the
  last session and up to today. The book records what it credited (so a second session the same day credits
  nothing), and the audit gets a `dividend` record. The price drop on the ex-date is then matched by the cash, so the
  daily loss stop no longer sees a loss that is not there.
  - Cash is credited on the ex-date, not on the payment date (value tracking; real cash comes later).
  - Foreign withholding tax is not modelled (the owner has no foreign shares); the audit line says gross.
- **Report:** the end-of-day report lists the day's dividends; the summary shows the total.
- **Backtest:** `qa history dividends <TICKER>` shows the stored events and checks the stored price history on the
  ex-dates: a price-only series drops by about the dividend on the ex-date, a total-return series does not. That
  answers whether the backtest (which uses the same history) already includes dividends.

## C. Splits (item 2)

Neither client has split data. Two signals, both from public data:

1. **Share count:** `stock.numberOfShares` from the same details answer, stored each day. A split k:1 multiplies it
   by exactly k (a buyback or issue moves it by far less). At session start, a held share whose count changed by a
   clean k (k = 2…20, or 1/k for a reverse split, within 0.5 %) since the last stored count is split in the book:
   quantity × k, last price ÷ k, cost unchanged; audited as `split`.
2. **Price guard:** at the decision, a held share whose live price is a clean ratio away from its last close mark
   (about ½, ⅓, … or 2×, 3×, …, within 10 %) with no split applied is **held back**: no orders for it that day, it
   is valued at its last close mark (so the loss stop does not fire on a split nobody has confirmed), and the session
   says what to do.
3. **By hand:** `qa paper split <TICKER> <K>[:1|1:<K>]` applies a split the owner has checked (offline; audited).

The book keeps each position's last close mark (written at the close) for the guard.

## Not in this plan

- Other corporate actions (spin-offs, rights issues, mergers): the price guard holds such a share back and says so.
- Dividend reinvestment, withholding tax, the payment-date cash lag.
