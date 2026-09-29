# 17 — An intraday strategy (research first, then paper)

- **Status:** Phase A built 2026-09-29 (A1–A6). The go/no-go now waits for about six months of collected bars; Phase B
  is not started. Planned the same day at the owner's request: "yes plan an intraday strategy"
  (after asking whether the current model can place orders through the day without a decision: it can't; it decides
  once a day per market).
- **Decisions:** ADR 0006, accepted 2026-09-29: "go with your recommendations, start phase A". D1–D7 below are the
  recommended options.
- **Gate for every step:** all tests green with new ones per step; native ABI tests on both sides for any ABI change;
  the daily strategies, `qa backtest` and `qa paper run` unchanged; nothing live called by me.

## What the research found (and why the plan starts with research)

1. **Avanza keeps little intraday history.**
   - The price chart (`/_api/price-chart/stock/{id}`) takes resolutions `minute`, `two_minutes`, `five_minutes`,
     `ten_minutes`, `thirty_minutes`, `hour` and up (Qluxzz `a6a18a94`).
   - The server picks which it allows per period and says so in `metadata.resolution.availableResolutions` (Go SDK
     `43f39025`).
   - Our own recording (2026-09-25, `one_month`) allows only `hour`, `day` and `week`. Minute bars are therefore only
     for shorter periods (`today`, `one_week`): exactly which is unknown until one read-only probe.
   - **Consequence:** a meaningful intraday backtest needs months of minute bars that we **collect ourselves** from
     now on, unless the owner buys history from a vendor (decision D6).
2. **Costs decide it, at our sizes.**
   - With the 5 000 kr account cap an order is at most 500 kr.
   - **Start** (the owner's class) is 0 kr courtage, but **only for 500 trades per 12 months**; then Avanza moves the
     account to **Mini** (0.25 %, at least 1 kr). UNVERIFIED (docs/research/market-rules.md §4).
   - An intraday strategy trading 2–6 times a day uses up the free trades in 3–8 months. After that it pays about
     **0.5 % per round trip** at 500 kr, plus the spread. Published intraday edges are of that size or smaller.
   - US and Canadian shares are worse: 1 USD minimum on a 50 USD order is 2 % per side. So: **Stockholm only**.
3. **Our trading window cuts the classic "last half hour".**
   - Continuous trading ends 17:25 and the closing auction runs to 17:30.
   - R16 stops orders at 17:20, and we never trade the closing auction (MAR hygiene, market-rules §5).
   - The intraday-momentum effect (the first half hour predicts the last; Gao, Han, Li, Zhou 2018, S&P 500 ETF) is
     earned into the close, and a Swedish master's thesis (Örebro 2024, OMXS30 and 5 stocks) reports it **weak** in
     Stockholm (search extract, UNVERIFIED).
4. **Opening-range breakout (ORB)** has the most published support for single stocks (Zarattini & Aziz 2023;
   Zarattini, Barbon & Aziz 2024: 5-minute ORB on US "stocks in play"). It is US evidence, long and short; we can only
   go long (ISK, no shorting). Whether it survives Stockholm spreads and our costs is exactly what the research phase
   measures.

So the plan has two phases:
- **Phase A** (research, no trading code) answers "is there an intraday strategy worth running here, after costs?"
- **Phase B** (trading) is built only if the owner says go after seeing A's report.

## Decisions (the owner chose the recommended option for each, 2026-09-29)

| # | Question | Chosen | Not chosen |
|---|---|---|---|
| D1 | Build order | **Research first (A), trading (B) only after a go** | build both now |
| D2 | Strategy to test | **ORB long-only** as the candidate; an adapted "late-day momentum" and a buy-open/sell-close baseline as controls | intraday mean reversion to VWAP; the daily strategies on 5-minute bars |
| D3 | Bar size | **5 minutes** (the ORB literature; light on calls) | 1 minute |
| D4 | Overnight | **Flat every day by 17:20** | allow holding overnight |
| D5 | Markets | **Stockholm only** | also US/Canada (costs above) |
| D6 | History | **Collect from Avanza from now on** (free; a first go/no-go after ~6 months) | buy vendor history (faster; costs money; coverage and terms unverified) |
| D7 | Paper account | **One strategy at a time**: the intraday one replaces the daily one while it is tested | a second paper book |

## Phase A: research (each step green, committed, pushed)

1. **The data probe and research notes.**
   - A read-only, public (no login) probe: `qa intraday probe [ERIC-B]` asks each period once and records the
     resolutions the server allows. **The owner runs it** (CLAUDE.md: live calls only when asked).
   - `docs/research/avanza-endpoints.md`: the chart periods and resolutions with the commit URLs; how many days of
     minute and 5-minute bars each period gives.
   - The owner checks the Start class's 500-trade allowance on Avanza's price list.
2. **Collecting intraday bars.**
   - A store table `intraday_bars` (orderbook, bar start UTC, resolution, OHLCV, known_at, source), added in place like
     `fx_rates`; append-only with restatements.
   - The price-chart DTO gains the resolution (Tier B, strict: an unknown resolution or missing field disables the
     feature, it never guesses).
   - `qa intraday import [TICKER…]`: today's (or the probe's longest) 1- and 5-minute bars. Public endpoint, no login,
     one call per name, rate-limited.
   - What to collect: the allowlist plus a **research list** (`config/research-universe.json`, e.g. the OMXS30 names,
     at most 30), so the test isn't limited to 5 names.
   - When: after 17:30 each trading day, as the last step of `qa paper run` and as its own command for Task Scheduler.
   - **Spreads:** a Paper session also stores each name's bid/ask once a minute (from the quotes it already streams),
     so the backtest's spread cost comes from measured spreads, not a guess.
3. **The intraday backtest.**
   - An intraday panel: bar start times, the day each bar belongs to, the session's first and last bar.
   - **Native ABI 1.4** `qe_bt_set_fill_mode(backtest, mode)` (additive, like 1.3). Intraday mode:
     - a resting limit fills **at its limit**, never at a better bar open (a bar's open is not an auction)
     - only when the price trades **through** it by at least one tick
     - market-type orders pay half the measured spread plus slippage
     - tests on both sides
   - Strategies decide at a bar's close on bars up to that close and trade from the next bar (the CLAUDE.md rule, per
     bar).
   - The runner checks each day ends flat (D4).
   - A leakage canary for intraday: a strategy that peeks at the next bar must be caught.
4. **Costs with the free-trade allowance.**
   - The cost model counts trades over rolling 12 months: Start is free for the first 500, then Mini's courtage.
     UNVERIFIED until the owner confirms (step A1).
   - The same count runs in the backtest and later in the paper book.
5. **The strategies.**
   - `orb-long`:
     - the range of the first N minutes after 09:05 (N = 5, 15 or 30)
     - buy on a close above the range high plus a buffer
     - stop at the range low (or a volatility stop); take profit optional
     - out by 17:10 at the latest
     - at most one entry per name per day
   - `late-momentum` (control): if the first hour is up by more than a threshold, buy at 16:30 and sell by 17:15.
   - `open-close` (baseline): buy at 09:10 and sell at 17:10 every day. Anything that can't beat it after costs is
     noise.
   - Every run goes to the TrialLedger with all its parameters, including mine.
6. **The go/no-go report** (`qa intraday report`):
   - statistics on the **daily** P&L (one number per day, so Sharpe, PSR, Deflated SR and PBO mean what they mean for
     the daily strategies)
   - per-trade net return with a day-clustered t-statistic
   - turnover against the free trades, and the result after they run out (Mini)
   - walk-forward
   - the **final holdout** (the last 20 collected trading days) stays locked until the owner unlocks it
   - Proposed bar for "go" (the owner decides):
     - at least 120 collected trading days
     - Deflated SR ≥ 0.95 and PBO ≤ 0.2
     - net per-trade mean > 0 **at Mini costs**
     - beats `open-close`
     - holds on the holdout once unlocked

## Phase B: trading on paper (only after a go)

1. **An intraday session.**
   - The Paper session gets an intraday mode: at each 5-minute bar close it reads the bars from the **same source as
     the backtest** (the public minute chart, polled at the bar's close) and decides.
   - Quotes (the depth stream) are for execution only.
   - One strategy per paper account (D7).
2. **Orders within ADR 0003, unchanged.**
   - The plan never counts on a risk check to stop it. It budgets up front: at most 5 entries a day, and at most 2
     orders per exit, so at most 15 orders a day (R10 allows 20).
   - Place, re-price and cancel stay 5 s apart per name (R12) and 5 a minute (R11).
3. **Exits.**
   - **Stops are the session's own:** it watches the quotes, and on a trigger it cancels the working exit and sends a
     marketable sell limit through the gateway (Avanza's stop-loss orders are not used; they are another endpoint and
     another decision).
   - **Flat by the close:** sell limits at 17:05, re-priced at 17:10 and 17:15, never in the closing auction.
   - A position still held at 17:20 is an event: it is sold at the next open, and the report shows it.
4. **Reports and the app.**
   - The end-of-day report gains an intraday section: trades, holding times, paper fills against the backtest's fill
     model, spread paid, free trades left.
   - The Trading page shows the day's trades and the next bar's decision.
   - `docs/guide.md`.
5. **The paper track record.**
   - The same clean-day gate as now.
   - Plus an honesty check: the paper fills' slippage must match what the backtest assumed. If paper is worse, the
     backtest numbers are too optimistic and are re-run with the measured costs.

**Live trading is not in this plan.** Confirm asks for a typed answer to every order within 30 s, so a stop that must
wait for a person is not a stop. Live intraday needs its own ADR, after Phase 8 (Auto, ADR 0004) or with the owner at
the screen all day.

## Rough size

- **Phase A:** about as much work as plan 16. The data then needs months to accumulate; the code does not wait for it
  (every step is tested on recorded and synthetic bars).
- **Phase B:** somewhat less work.

## Step notes
### A1: the decisions and the chart probe (done 2026-09-29)

- ADR 0006 accepted with the recommended D1–D7.
- `PriceHistory.AvailableResolutions`: the resolutions the chart says it gives for a period (already in the DTO, now
  passed on; no DTO change).
- **`qa intraday probe [TICKER | --id N]`:**
  - asks the public price chart **without a login**: today, one week, one month and three months with the server's
    own resolution, then each offered 1- or 5-minute resolution
  - prints the bars, the Stockholm days they cover, the offered resolutions, and a summary ("5-minute bars: today 1
    day(s) …; one_week 5 day(s) …")
  - records the raw answers (`--no-record` to skip), to be sanitized into fixtures
  - one question failing (e.g. HTTP 400 for a combination) is an error row and the probe goes on; a chart that wants
    a login (401/403) stops it, and no login is tried
- **Tests (4 new):** the questions and the table over fake answers shaped like the recording; no login, GET only, six
  answers recorded; a failing question; a login-wanting chart; the ticker from the instrument master.
- **Waiting on the owner:** run `.\qa intraday probe` once on a trading day (after 09:30, so "today" has bars) and
  share the output, or sanitize the recording into `recordings/fixtures/`. Step A2 stores what it shows.

### A2: collecting intraday bars and spreads (done 2026-09-29)

- **The store** gains `intraday_bars` (orderbook, resolution, bar start UTC, OHLCV, known_at, source) and
  `spread_samples` (orderbook, time, bid, ask, their volumes, source), added in place; the schema version stays 1.
  Bars are append-only with restatements, like the daily ones; a spread sample is an observation, kept once.
- **`IntradayImporter`** (1- and 5-minute bars, periods up to three months):
  - the answer must be the resolution asked for
  - every bar must start on its grid (whole minutes; :00, :05, … for 5 minutes) and the starts must rise
  - otherwise nothing is stored (Tier B strictness); a bar still open when asked is left out
  - the source is the daily bars' own: NOT point-in-time, NOT survivorship-free
- **The research list** `config/research-universe.json` (`qa intraday research add|remove|list`, at most 30):
  - Stockholm shares found by exact ticker with the public search (no login); the SEK listing only
  - no orderbook id is ever typed in or guessed
  - never tradable because of the list (it is not R2's allowlist)
- **`qa intraday import`** (public, no login): the allowlist's Stockholm shares and the research list by default,
  foreign shares skipped. One name failing is reported and the others go on (exit 1).
- **The Paper session:**
  - `SpreadSampler` keeps the first fresh, two-sided quote per name per minute; the samples are stored when the
    session ends (source `session-quotes`, point-in-time)
  - after Stockholm's close, the day's bars are collected like `qa intraday import`
  - research data only: a failure is a warning, and the day's trading and report are untouched
- **Tests (17 new):**
  - the store: versions and restatements, bad bars, spread samples, an old store gaining the tables
  - the importer: open bars left out, another resolution, off the grid, out of order, only 1/5 minutes and short
    periods
  - the research list, and the sampler
  - the CLI: research add/remove/list over the recorded search (the foreign listing ignored), import of the
    allowlist and the list without a login, a failing name, the refusals
  - the Paper spy: spreads kept; bars collected after the close, not before
- All 1242 managed tests pass (1 skipped).
- **The owner's probe (2026-09-29, after the close):** only `today` gives 1- and 5-minute bars (`one_week` gives
  10-minute bars at best, `one_month` hourly). A missed day cannot be caught up, so the import must run every trading
  evening; `today` still gave the whole day at 23:24. Details in docs/research/avanza-endpoints.md.

### A3: the intraday backtest (done 2026-09-29)

- **Native ABI 1.4** (additive, like 1.3; both only before the first step):
  - `qe_bt_set_fill_mode(backtest, QE_BT_FILL_INTRADAY)`: a limit fills only at its own limit, and only when the
    bar trades through it (buy: low < limit; sell: high > limit). Prices sit on the tick grid, so "through" is at
    least one tick; a touch is no fill, and no limit ever fills at a better bar open (a minute bar's open is not an
    auction). MOO and MOC are unchanged.
  - `qe_bt_set_half_spread(backtest, instrument, bps)`: a share's own half-spread for market-type fills.
  - The managed side requires 1.4 (`QeBacktest.SetFillMode`, `SetHalfSpread`, `BacktestFillMode`).
  - The 1.3 test now reads "minor ≥ 3", and the export check still matches the header.
- **The panel** (`MarketPanel`) takes intraday bars:
  - `FromIntradayBars`: bars aligned on their starts, each dated by its Stockholm trading date; `BarStartsUtc`,
    `IsIntraday`, `IsLastOfDay`, `Label`
  - a daily panel is unchanged: strictly increasing dates, no times
- **The strategy's window** gives `Time(t)`, with the same look-ahead guard as every other read.
- **The runner**, for an intraday panel:
  - uses the intraday fill mode and each share's measured half-spread
  - fails a run that holds a position after a day's last bar (ADR 0006 D4), naming the share and the day
  - computes the statistics on daily P&L: the starting cash, then each day's last equity, so Sharpe, PSR and DSR
    are per day as for the daily strategies
  - the truncation replay and the ledger work as before; its message names the bar's time
- **Tests (16 new):**
  - native: the version, daily unchanged, the intraday limit at its limit on a trade-through, a touch, market orders
    with the spread, a share's own spread, bad input and too late
  - managed: the same through the binding
  - the panel (alignment, a missing bar, dates, labels, views, refusals)
  - a day trader flat each evening and judged per day
  - holding overnight failing the run
  - an intraday limit at its limit, not at the better open
  - a measured spread paid (three times the cost at 25 + 5 against 5 + 5 bps)
  - reading the next bar's time, and an intraday leakage canary caught by the truncation replay
  - daily bars having no times
- All 1254 managed and 138 native tests pass (1 skipped).

### A4: the free-trade allowance (done 2026-09-29)

- **`config/costs.avanza-start.json`** gains `free_trades`: 500 trades per 12 months, then `avanza-mini`,
  `verified_on` null (from market-rules.md §4, a search-engine extract; the owner's screenshot of 2026-09-26 did not
  show the footnote). **The owner checks it.**
- **`CostModel.FreeTrades`** (`FreeTradeAllowance`): the class it turns into is read from its own file beside it. The
  next class may not have an allowance of its own, and that is refused before it could loop. Also refused: pointing
  at itself, a missing file, a bad count or window.
- **The backtest:**
  - the engine charges the free class
  - the runner then counts the trades on Swedish shares in a rolling window
  - each trade past the allowance pays the next class's courtage, taken off the equity from its bar on
    (`BacktestResult.AllowanceCourtage`, included in the trial's total costs)
  - the note says when the free trades ran out, and that the allowance is unverified
  - foreign trades pay their own courtage and don't count
  - approximation (stated): the engine's cash check did not see this courtage
- **`BacktestResult.Fills`:** every fill with its bar, for the per-trade statistics of A6.
- Tests that copy the Start class into a temporary config folder copy Mini too, as a real config folder has it.
- **Tests (7 new):** the repository's Start class, three kinds of broken allowance, a chain refused, trades past the
  allowance paying Mini off the equity, and the window rolling.
- All 1261 managed tests pass (1 skipped).

### A5: the strategies and `qa intraday backtest` (done 2026-09-29)

- **The frame** (`IntradayStrategy`), shared by the three:
  - each day's open and close come from the XSTO calendar (`IntradayClock`; half days close early)
  - a decision is made at a bar's close (its start plus the bar length) and trades at the next bar's open
  - state never carries overnight
  - each name is waiting, in, or done for the day: at most one entry per name per day
  - `weight` of equity per position (default 0.1, R6's 500 kr at 5 000 kr), at most ⌊1/weight⌋ names at once; when
    more want in on the same bar, the strongest signal goes first
  - from `exit` minutes before the close (at least 10: R16) everything goes flat and nothing enters
  - bars on a day the calendar has closed fail the run instead of being guessed
- **`orb-long`** (`range` 15, `skip` 5, `buffer` 0 bps, `exit` 20):
  - the range is the high and low of the bars in the `range` minutes from `skip` after the open (09:05: the opening
    auction's bar is left out, as the plan says)
  - a close above high × (1 + buffer) buys
  - a close below the low sells: a stop at a bar's close, not inside the bar (no stop orders, ADR 0006 §3)
  - no take-profit
- **`late-momentum`** (`lookback` 60, `threshold` 0 bps, `entry` 60, `exit` 15):
  - the first hour's return, from the first open to the close at 10:00
  - buys at 16:30 when it is above the threshold, and sells at 17:15
- **`open-close`** (`entry` 10, `exit` 20): every name at 09:10, out at 17:10, each with min(weight, 1/N).
- **They live in `IntradayStrategyCatalog`, not `StrategyCatalog`:** `qa paper strategy` cannot choose them (Phase B
  comes only after a go).
- **The runner:** on intraday bars a market exit is sent even after a bar without trades (it needs no price). Before,
  a quiet decision minute before the close could carry a position overnight. Daily bars are unchanged.
- **`BacktestResult.DailyReturns`:** one return per day. A sweep's PBO now uses them, so on intraday bars it is per day,
  as plan 17 says, not per bar.
- **`qa intraday backtest`:**
  - reads the collected bars of one resolution (5 minutes by default) for the allowlist's Stockholm shares and the
    research list, or `--tickers`
  - bars outside each day's session are dropped; a share's day whose bars stop more than 30 minutes before the close
    is left out (a mid-day import or a halt), and the output counts both
  - half-spreads: a share's median from the Paper sessions' samples once there are 30, else the cost model's
  - market orders at the next bar's open; costs from `backtest-defaults.json` (Start with its free-trade allowance)
  - `--grid` sweeps and prints PBO and the best's Deflated Sharpe
  - every run is logged; the data source is named with the resolution (`avanza-price-chart:5m`), so 1- and 5-minute
    runs are separate studies
- **The intraday holdout, `config/holdout.intraday.json`** (new, locked, 20 days):
  - the last 20 trading days with collected bars are never read while it is locked
  - it rolls with the collection, so the newest days are always unseen
  - with fewer days collected, nothing runs
  - `--to` into it is refused and logged, as for the daily holdout
  - like `holdout.json`, only the owner edits it: added to the settings deny list and to hook rule 5, with hook tests
  - `holdout.json` still guards the daily bars. Intraday bars exist only from this collection on, all inside the daily
    holdout window, so ADR 0006's own holdout is the one that applies to them.
- **Tests (25 new):**
  - the strategies on hand-made bars:
    - the breakout at the next open and out at 17:10; the range after `skip`; the buffer
    - the stop, one entry a day, and a new day
    - the strongest taking the slots
    - late momentum up and down, with a threshold
    - open-close at 1/N
    - a half day's early exit
    - an exit after a minute without trades (it fails without the runner change)
    - daily bars and a weekend refused
    - bad parameters, and the catalog's defaults
  - `qa intraday backtest` on a temporary store:
    - a run up to the holdout
    - a sweep
    - `--to` into the holdout refused and logged
    - an unlocked holdout read and marked
    - too few days
    - an incomplete day left out and a measured spread used
    - a daily strategy refused
    - the holdout rule
- All 1285 managed tests pass (1 skipped); the hook tests pass.

### A6: the go/no-go report (done 2026-09-29; Phase A is built, the verdict waits for the data)

- **`qa intraday report`** runs a fixed set of runs on the days before the intraday holdout, every one logged:
  - `orb-long` with range 5, 15 and 30 (the plan's grid, the rest at the defaults)
  - `late-momentum` and `open-close`
  - all at the configured costs: Start and its free trades by default
- **The candidate** is the range with the best daily Sharpe. For it the report shows:
  - its Deflated Sharpe over the whole study (every orb-long run on the same names, days and bars, earlier ones too)
  - the grid's PBO on daily returns
  - a walk-forward: choose the range on all days before (at least 60), trade the next 20
  - how long the 500 free trades last at its pace
- **"At Mini"** (`BacktestRunner.Recost`):
  - the same fills, each Swedish one paying Mini's courtage instead of what the run charged
  - nothing is run again, so no trial is added and the study is not padded with near-copies
  - approximation (stated): sizes and the engine's cash check stay as they were
- **Per trade:**
  - round trips per share and day, net of Mini's courtage (the fill prices already carry the spread and slippage)
  - the mean, the share won, and a t-statistic with standard errors clustered by day (CR1)
- **The bar**, each line PASS, FAIL or WAIT:
  - at least 120 days before the holdout (WAIT until then)
  - Deflated Sharpe ≥ 0.95
  - PBO ≤ 0.2 (WAIT under 40 days)
  - net per trade > 0 at Mini
  - beats `open-close` at Mini (daily Sharpe)
  - holds on the holdout: WAIT while it is locked. Once the owner unlocks it, the candidate (chosen without those
    days) and `open-close` run on the held-out days only (marked in the ledger), and it holds when its return at Mini
    is positive and above open-close's.
- **The verdict:**
  - NOT YET while there are too few days
  - NO-GO on any FAIL
  - PASSES SO FAR while the holdout waits
  - GO: Phase B may be built; the owner decides
- **The report never chooses on the holdout's days**, locked or not. With no more days than the holdout, it stops and
  says so. `--json` gives the numbers, with a non-finite value (a Sharpe without trades) as null.
- **Found while building it:** the runner resized an open intraday position whenever the price drift made a 1-share
  change reach the 10 % no-trade band (about 10 shares at 500 kr). That meant extra trades, extra courtage and free
  trades used up. An intraday position is now entered and left whole; a missed entry is still retried, since the
  position is then zero. Daily bars are unchanged.
- **Tests (11 new):**
  - on hand-made bars (a real breakout edge on one share, a falling second share):
    - every criterion passing, the holdout too, with seven runs logged and only the two holdout runs touching it
    - the holdout locked: PASSES SO FAR and never read
    - too few days: NOT YET
    - no edge: NO-GO on the per-trade line
    - re-costing (at Mini, at its own class, and after an allowance ran out, which changes no trade)
    - one round trip by hand
    - a position never resized
  - the CLI: the report's five runs before the holdout, text and JSON; the unlocked holdout checked; too few days
- All 1295 managed tests pass (1 skipped).
- **What remains is data and the owner:**
  - about six months of collection (`qa paper run` or `qa intraday import` each trading day)
  - the probe
  - the research list
  - verifying the Start allowance and the calendar
  - then the report, and unlocking the holdout for its last line

### A2b: the 10-minute fallback for missed days (planned and built 2026-09-29, owner: "yes add the 10-minute fallback for missed days")

Why: the owner's probe showed 1- and 5-minute bars exist for the current day only; `one_week` gives 10-minute bars.
A day the evening import missed (PC off, a failed call) is otherwise lost for good.

- **Collection (the catch-up):**
  - every `qa intraday import` of today's bars (and the Paper session's collection after the close) also looks back
    over the last week
  - per share: the XSTO trading days of the last 7 days, before today, with neither 5-minute nor 10-minute bars
  - if there are any: one public `one_week` call at `ten_minutes` (the same route and resolution names as before,
    Qluxzz `a6a18a94`)
  - the same strictness as the 1- and 5-minute import: the answer must be `ten_minutes`, on the 10-minute grid,
    rising, and open bars are left out
  - only the missed days' bars are stored, as resolution `TenMinutes`
  - no calendar loaded: the catch-up is skipped and says so (trading days are never guessed)
  - `--no-catch-up` skips it
- **Use in backtests and the report (`--fallback`, off by default):**
  - a day becomes a 10-minute day only when **no** share has 5-minute bars for it. Mixing bar sizes within one day
    would let one share's later data (a 10-minute bar ends 5 minutes after the 5-minute clock) steer another's
    earlier trade.
  - the strategies get each day's bar length from the clock (`IntradayClock` per day), so decisions and exits keep
    their real times. Consequence: on a 10-minute day, a 5-minute opening range can't be formed, so `orb-long
    range=5` does not trade that day.
  - such runs name their source `avanza-price-chart:5m+10m`: a separate study, never mixed with the pure 5-minute
    one
  - the output says how many of the days are 10-minute days
- **The intraday holdout** counts the days with bars of any resolution, so it is the same days whatever the run uses.
- **Done 2026-09-29.** Tests (6 new):
  - the store: collected days of every resolution
  - the catch-up:
    - only the week's missed trading days filled, in one call, nothing asked the next evening
    - a day the answer no longer has reported as lost
    - another resolution refused
  - `qa intraday import`: skipped without a calendar, then catching up; nothing missing the next evening; `--no-catch-up`
  - the strategies on a 10-minute day, with and without the day's bar length (5 minutes late without it)
  - `qa intraday backtest --fallback`:
    - a whole missed day used and a mixed day not
    - the separate study
    - the hint without the flag
    - refused for 1-minute runs
- The existing holdout tests now read "trading day(s) with intraday bars".
- All 1301 managed tests pass (1 skipped).
