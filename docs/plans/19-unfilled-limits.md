# 19 — How often limit orders go unfilled, and what that costs against the backtest

- **Status:** done 2026-09-30 (planned and built the same day), at the owner's request: "build the unfilled-limit
  measurement in the eod report" (after plan 18 left the execution policy unchanged and said the end-of-day reports
  should give the evidence).
- **Why:** the strategy's orders are limits at the reference ± 0.5 %, and a resting order fills on paper only when a
  later trade prints through it, for at most 10 % of that trade's volume (ADR 0003 §8). The daily backtest fills the
  same limit **whole** whenever the next day opens at or through it or trades through it during the day
  (`native/src/qe/backtest/engine.cpp`, `try_fill`). FASTAT's buy of 2026-09-30 rested unfilled. Whether that is rare
  or common, and whether it costs anything, decides whether the limit policy should change.
- **Gate:** tests for every part; all managed tests green; the report stays buildable from the audit file alone; nothing
  live called.

## What is measured, per order that reached the market

| Field | From |
|---|---|
| side, volume, limit, decision price | `intent` and `oms-new` audit records |
| filled volume and the order's last state (Filled, Cancelled at the close, …) | `oms-fill`, `oms-state` |
| the last price at the close and the day's high and low | a new `close-mark` record (below) |
| **would the backtest have filled it?** | buy: the day's low below the limit; sell: the day's high above it (the backtest's continuous-trading rule; the open is not in the poll, see "Limits") |
| **missed** (unfilled volume only) | buy: unfilled × (close − limit); sell: unfilled × (limit − close); in SEK at the day's rate. Positive: the price moved away, so the fill the backtest books at the limit would be worth that much more at the close. Negative: not filling saved it. |

The day's totals: orders, filled fully / partly / not at all, the share of the volume filled, how many the backtest
would fill, and **missed against the backtest**: the missed value of the orders the backtest would fill, in SEK and in
basis points of their unfilled value. `qa report eod` also prints the same totals over every day so far, which is the
evidence.

## Changes

1. `Quote` carries the day's high and low (`DayHigh`, `DayLow`). They come from the market-data poll's `highest` and
   `lowest`, already deserialized and mapped (`MarketSnapshot.High/Low`), until now dropped when the quote was
   composed. The recorded answer of 2026-09-25 shows them as the day's range (last 94.96, lowest 94.54, highest 95.82 on
   6.1 M shares). No endpoint or field is added.
2. `OrderGateway.AuditCloseMarks`: at a market's close, one `close-mark` record per allowlisted share of that market
   (last, its time, bid, ask, day high and low, SEK per unit). Paper calls it before its day orders expire; Confirm at
   its end of day.
3. `EodReport.FillRate` (`EodFillRate`, `EodLimitOrder`): built from the records above. It is information, not a
   violation, so it never changes whether a day is clean.
4. `qa report eod` prints one line per order and the all-days totals; the summary line gains a short fill-rate part
   (also in the app's report list and `qa report gate`).

## Limits of the measurement

- The close is the last trade the poll saw at the session's close time, before the closing auction's price may print.
- The backtest also fills a limit at the open when the open is at or through it. The poll has no open price, so an
  order whose only fill chance was an open exactly at the limit counts as "the backtest would not fill".
- The missed value runs to the close of the order's day only. The strategy re-decides the next morning, so the true
  cost of a missed fill continues until it fills; the day's measure is the part that can be told from the day alone.
- An order stopped by the owner (Ctrl+C, `qa kill`) counts as unfilled too; the line shows its state.

## Done (2026-09-30)

| Part | Code | Tests |
|---|---|---|
| Day range in the quote | `Quote.DayHigh/DayLow`, `QuoteComposer`, the CLI's poll-only quote | `QuoteComposerTests` (from the poll, also while the stream is newer) |
| Close marks | `OrderGateway.AuditCloseMarks`; `PaperSession.CloseAsync` (per market), `ConfirmSession` at the close | `EodReportTests.ARealPaperSession…` (the marks feed the report) |
| The measurement | `EodLimitOrder`, `EodFillRate` (`From`, `Combine`, `Describe`), `EodReport.FillRate`, the summary's `Limits:` part | `EodReportTests.TheFillRate_…` (filled, partly, a sell, a foreign share the backtest would not fill, a broker-refused order left out; totals across days), `…WithoutTheDaysRange…` |
| Output | `qa report eod` `limit` lines and `All N days …`; the app's report details | `CliPromotionTests.ReportEod_ShowsWhatEachLimitMissed…` (and `--json` stays JSON), `ViewModelTests.Reports_…` |

All managed tests pass; a mutation of the missed value's sign fails them. Nothing live was called.
