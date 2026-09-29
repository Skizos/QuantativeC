# 17 — An intraday strategy (research first, then paper)

- **Status:** Phase A started 2026-09-29. Planned the same day at the owner's request: "yes plan an intraday strategy"
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
- **Still waiting on the owner's probe:** whether "today" is the only period with minute bars decides whether a
  missed day can be caught up (`--period one_week`).
