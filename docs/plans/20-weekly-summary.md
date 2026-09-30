# 20 — The weekly summary: Paper against the backtest, and the intraday collection

- **Status:** done 2026-09-30 (planned and built the same day), at the owner's request: "build the weekly summary
  too" (offered after plan 19 as "a weekly summary with Paper returns against the backtest's expected returns, plus
  intraday collection coverage").
- **Command:** `qa report week [--week 2026-W40 | --date yyyy-MM-dd] [--json]`. Offline and read-only apart from
  saving `reports/week/2026-W40.json`. Default: the week of the last day with a session (so on a Monday morning it is
  the week just ended).
- **Gate:** tests for every part; all managed tests green; nothing live called; **no backtest is run** (see below).

## What it shows

1. **The Paper days of the week**, one line per Stockholm trading day (and any other day a session ran): clean, not
   clean, incomplete, or **no session**; the day's return; the share of the account invested at the close; orders sent
   and filled. Then the week: compounded return, the value at the end, fees, clean days, and the Confirm gate count.
2. **Paper against the backtest.** The expectation comes from the saved strategy's **recorded** backtest in the trial
   ledger (the latest successful one of the same strategy and parameters on imported data, preferring one on the
   current allowlist). No new backtest is run: a new run would be an evaluation for the ledger, and one over the last
   weeks could read the locked holdout.
   - Its average day: μ = Sharpe per day × daily volatility (annual volatility / √252, the runner's convention).
   - Over n Paper days at an average invested share s: expected s·n·μ, 95 % range ± 1.96·s·σ·√n. The backtest is
     (almost) fully invested and Paper is capped (the account cap, R7), so the share scales it.
   - The verdict: below, within or above the range; "not invested" below 1 %. Shown for the week and since the first
     Paper day (the range narrows relative to the expectation as days accumulate; one week says little).
3. **Limit fills** (plan 19): the week's `EodFillRate` and the same since the start.
4. **Intraday collection** (plan 17): per research share, the week's trading days with 1/5-minute bars, with only
   10-minute catch-up bars, and missing; and the collected days so far against the go/no-go's need (120 days before
   the intraday holdout's days).

## Code

- `QuantAnalyst.Data.History.IntradayCoverage.Measure` (store → per share: fine, coarse, missing days).
- `QuantAnalyst.Trading.Reports.WeeklyReport` (build from the week's inputs, text lines, save/load),
  `BacktestExpectation` (from a `TrialRecord`, and the choice of trial), `PaperVsBacktest`.
- `qa report week` in the CLI gathers the inputs: every day's `EodReport` (rebuilt from the audit), the gate, the
  ledger, the store, the XSTO calendar, the research shares and the intraday holdout's days.

## Limits

- The expectation is the backtest's average over its whole sample, in-sample for a strategy chosen on it: optimistic
  if anything. A week inside the range is not evidence that the strategy works; a run of weeks below it is evidence
  that Paper does not do what the backtest did.
- The invested share is the close's; a day's return comes from what was held during it. Scaling by the average close
  share is a first-order correction.

## Done (2026-09-30)

| Part | Code | Tests |
|---|---|---|
| Intraday coverage | `IntradayCoverage.Measure` | `IntradayDataTests.TheCoverage_…` (1-minute, 5-minute, catch-up only, missing; a share without bars) |
| Expectation and verdict | `BacktestExpectation` (`Find`, `Of`, `Compare`), `PaperVsBacktest` | `WeeklyReportTests` (μ and σ from the metrics, the range, all four verdicts; the trial choice: same list first, then the latest; synthetic, failed and other parameters never) |
| The week | `WeeklyReport` (`Build`, `Lines`, `Save`/`Load`, ISO weeks) | `WeeklyReportTests` (no-session days, compounding, invested share, since the start, text, round trip, ISO week parsing incl. 53-week years) |
| Command | `qa report week` | `CliUsabilityTests.ReportWeek_…` (default week, `--week`, `--date --json`, both refused; the intraday line with the holdout policy's days) |

Only trading days up to today are listed (a later day has had no session yet), and the intraday coverage counts the
days before today (today's bars come in the evening). All managed tests pass; nothing live was called.
