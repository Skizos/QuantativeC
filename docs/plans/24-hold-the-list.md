# 24 — Paper against simply holding the list

- **Status:** step A built 2026-10-05; step B (OMXS30) not started. Item 4 of the 2026-10-01 improvement review
  ("compare against simply holding the list"), after the review of plans 21–23.
- **Gate:** tests for every part; all managed tests green; nothing live (the comparison reads the audit and the store).

## Why

The weekly summary compares Paper with the strategy's own backtest (plan 20). That says whether Paper behaves like the
backtest, not whether the strategy is worth running: a strategy that matches its backtest can still earn less than
buying the same shares and doing nothing. The question for "how do I earn more" is: **does the strategy beat holding
its own list?** If it doesn't, over enough days, its trading costs and timing are losing money, and the plain holding
is the better strategy.

## A — the list, held equally (built)

**What it is:** each Paper day, the average return of the shares on the list that day, from the previous Paper close to
this close, with their dividends (equal weights, no costs; an idealised "bought them all and held them").

- **Same prices as Paper:** the returns come from the `close-mark` records (plan 19) the session writes at each close:
  the last price and the SEK rate of every listed share. Paper's day return runs from the previous Paper session's close
  marks (the book's start value) to today's, so the list's day covers the same interval, also across days without a
  session. `EodReport.CloseMarks` exposes them.
- **Dividends:** a share's dividends with an ex-date after the previous close and on or before this one are added (from
  the store, `dividend_events`, fetched at each Paper start; plan 21). Paper credits its held shares' dividends the same
  way. Without the store (busy, or none yet) the line says the dividends are left out.
- **Left out on a day:** a share without a close on both days (added or removed in between, no price at the close),
  and one with a split-like move (plan 21's guard: a split would read as a crash or a jump). The line counts them.
- **Two comparisons:** with the list **fully invested**, and **at Paper's exposure**: each day's list return times the
  share Paper had invested at the previous close (Paper's cash earns nothing). The second isolates what the strategy's
  choices and costs did with the money it had in shares; the first includes the cost of holding cash (often the risk
  limits' doing: R7 allows 20 % per name).
- **Noise:** one week says almost nothing. Since the first Paper day, the daily differences (Paper minus the list at
  Paper's exposure) get a t-statistic: below 2 in size it is "not yet more than noise". With fewer than 5 days, none.

In the weekly summary (`qa report week` and the app's Reports page):

```
Against holding the list (equal weights, dividends included, no costs; the same close prices as Paper):
  this week: 4 day(s): Paper +0.40%; the list +2.10% (at Paper's 46 % invested +0.97%): Paper 0.57 points behind at the same exposure
  since 2026-09-28: 12 day(s): Paper +1.20%; the list +3.00% (at Paper's 52 % invested +1.56%): Paper 0.36 points behind at the same exposure (t -0.8: not yet more than noise)
```

| Part | Code | Tests |
|---|---|---|
| Close marks in the day's report | `EodReport.CloseMarks` (`EodCloseMark`), from the `close-mark` records | `EodReportTests.TheDaysReport_KeepsEveryListedSharesClose` |
| The list's days | `HoldTheList.Days` (equal-weight close-to-close returns in SEK with dividends; split-like moves and missing closes left out) | `HoldTheListTests` (returns, dividends, FX, a gap day, a split, a new share) |
| The comparison | `HoldTheList.Compare` → `PaperVsList` (both returns, the exposure, the difference, the t-statistic) | `HoldTheListTests.TheComparison_…`, `FromFiveDays_…` |
| The weekly summary | `WeeklyReport.HoldThisWeek`, `HoldSinceStart`, `HoldDividendsMissing`, `HoldLeftOut`; the CLI reads the dividends from the store (`ListDividends`) | `HoldTheListTests.TheWeek_ComparesPaperWithHoldingTheList_…`, `CliUsabilityTests.ReportWeek_…` (a stored dividend counted) |
| The app | The week card's **Against holding the list** chip (`WeekCard.Hold`/`HoldMark`: coloured once \|t\| ≥ 2) and the summary's lines | `ViewModelTests.Reports_…`, the XAML binding checks |

## B — the market index (built 2026-10-06; unverified until the first import)

The market itself (e.g. OMX Stockholm 30) as one more line under "Against holding the list":

```
  the market (OMX Stockholm 30) over the same days: this week +0.80% (4 day(s)), since 2026-09-28 +1.10% (12 day(s))
```

- **The index is set by its number**, `qa benchmark set --orderbook-id <number> --name "OMX Stockholm 30"`
  (`config/benchmark.json`): the owner copies it from the index's page address on avanza.se. The program does not search
  for indices (one route fewer to trust) and never trades it (no instrument record: `qa universe add` refuses it).
- **Its daily closes** come from the same public price chart as the shares' (no login): `qa benchmark import`, and after
  every Paper session and evening import. The research (`docs/research/avanza-endpoints.md`, 2026-10-06): neither
  reference client documents the chart for an index; one reads index info through the stock route, the other compares a
  stock's chart with another orderbook's. So it is **unverified until the owner's first import**: an answer of another
  shape fails the strict DTO check, is reported, and nothing is stored.
- **Same intervals as Paper**: each Paper day from the previous Paper close to this one; a day without the index's close
  on both ends is counted as "without closes".
- The list (A) stays the fairer benchmark: same shares, same days. The index says how the list itself did against the
  market.

| Part | Code | Tests |
|---|---|---|
| Setting and comparison | `BenchmarkSettings`, `MarketBenchmark.Over`, `WeeklyReport.Benchmark*` | `HoldTheListTests.TheWeek_…` (the index's line; a missing close; no closes yet) |
| Commands | `qa benchmark`, `qa benchmark set`, `qa benchmark import` (`AvanzaCommands.Benchmark.cs`); the refresh after `qa paper run` and `qa intraday import` | `CliUsabilityTests.Benchmark_…` (set by number, imported without login from the chart route, shown, never on the list), `CliUsabilityTests.ReportWeek_…` (the line end to end) |

## Not in this plan

- Costs for the benchmark (holding costs one courtage per share, once): left out, so the benchmark is a little
  flattering, which makes it a stricter bar for Paper.
- A benchmark in the backtest (`qa backtest run` already has `buy-and-hold` as a strategy: run it on the same list and
  compare in the trial ledger).
