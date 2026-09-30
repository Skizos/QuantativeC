# `qa` command-line tool

`qa` is `src/QuantAnalyst.Cli`. It needs the native library built once by `.\build.ps1` (or `cmake --build --preset dev`; see `docs/setup.md`).

**Prefer a window?** `.\qa-app.ps1` opens the QuantAnalyst Windows app, which runs these same commands for you. It covers status, instruments, strategy and backtest, the Paper session with BankID and KILL, and reports (`docs/guide.md` §0).

`qa` is **not on your PATH**, so typing a bare `qa` fails with "The term 'qa' is not recognized". Use the launcher at the repository root instead. After a `git pull` it rebuilds what changed and then runs qa with the repository as its working folder:
- the native library, via `build.ps1 -NoManaged`: a few minutes the first time, and it runs the native tests
- qa itself
- the copy of the native library next to qa

Set `QA_SKIP_NATIVE_BUILD=1` to skip the native step.

```powershell
cd C:\path\to\QuantativeC
.\qa history import ERIC-B          # PowerShell 7 (Windows, macOS, Linux)
```

To type just `qa` from any folder, add one line to your PowerShell profile (`notepad $PROFILE`, then open a new window):

```powershell
function qa { & 'C:\path\to\QuantativeC\qa.ps1' @args }
```

Without the launcher, either of these works:
- `dotnet run --project src/QuantAnalyst.Cli -- <command> [options]`
- the built executable `src\QuantAnalyst.Cli\bin\Debug\net10.0\qa.exe`, run from the repository folder so `data\` and `config\` are found

In this document, `qa …` means any of these.

**Output conventions:**
- Numbers are parsed and printed with the **invariant culture**: use `0.05`, not `0,05`, even on a Swedish Windows.
- Every seeded result prints the seed it used (default `20260925`).
- `--json` prints machine-readable output.
- Errors go to stderr as `error: …` with exit code 1.

These are model outputs for research, not financial advice.

## `qa status`: start here

`qa status` shows, in one screen, everything a trading day depends on, marks each line `ok`, `todo`, `warn` or `FAIL`, and ends with **numbered next steps** in the order to take them. It is offline and read-only (no login, nothing written), so run it whenever you are unsure what to do. The daily routine around it is in `docs/guide.md`.

It checks:
- the native engine's ABI
- the mode and promotion state
- the kill switch and a running session
- the paper account (courtage class, cash, decision time) and the limits for that cash
- the paper book
- the saved strategy, and whether the trial ledger has a backtest of it on imported history
- the allowlist and how far each name's history reaches; a `FAIL` for a share that does not trade continuously (plan 18: it would stop every Paper decision), with the `qa universe remove` to fix it
- the intraday collection, once there is one: the last collected day, today's bars still to collect after the close, missed trading days of the last two weeks that `qa intraday import` still catches up (a week back, at 10 minutes), and those that are lost
- the calendar's verification
- the audit chain, the last session's report, and the progress towards the Confirm gate

A `FAIL` line (a kill switch that is on, a broken audit chain, a file that doesn't load) comes first in the next steps. Otherwise the last step says when to start the next session, e.g. "Next session: Monday 2026-09-28. Start it that morning before 09:10: qa paper run". With US or Canadian shares on the list it names each market's decision and the end, e.g. "It decides per market (XSTO at 09:10, XNYS at 15:40), trades, and ends after the 22:00 close".

## `qa price`

| Model | Example |
|---|---|
| Black-Scholes-Merton price + Greeks (default) | `qa price --spot 100 --strike 100 --rate 0.05 --vol 0.2 --expiry 1` |
| CRR binomial, European or `--american` | `qa price --spot 50 --strike 50 --rate 0.10 --vol 0.4 --expiry 0.4167 --type put --model crr --steps 500 --american` |
| Monte Carlo with SE, paths and seed | `qa price … --model mc --paths 100000 [--antithetic] [--control-variate] [--sobol] [--seed N]` |
| Implied volatility (Brent) | `qa price --spot 100 --strike 100 --rate 0.05 --expiry 1 --implied-from 10.45` |

Units:
- `--vol`, `--rate` and `--dividend` are annualized decimals (continuous compounding).
- `--expiry` is in years.
- Vega is per 1.00 of vol, theta is −dV/dT per year, rho is per 1.00 of rate.

## Price files for `risk` and `optimize`

```
# comments and blank lines are ignored
date,ERIC-B,VOLV-B,OMXS30
2025-01-02,74.12,251.30,2450.10
2025-01-03,75.00,250.10,2461.00
```
- Dates are `yyyy-MM-dd` and strictly increasing; prices are positive.
- A semicolon-separated file (Swedish Excel export) may use decimal commas: `2025-01-02;74,12;251,3`.
- Errors cite `file:line`.
- `samples/synthetic-prices.csv` is **synthetic** demo data (instruments `SYN-A` … `SYN-D`, index `SYN-INDEX`). It is not market data.

Every report prints its **labels**:
- data source and date range
- number of returns and their definition
- currency: SEK (assumed)
- survivorship: **not survivorship-free**
- account: **ISK**, no tax modelled, not tax advice

## `qa risk`

```bash
qa risk --prices samples/synthetic-prices.csv --index SYN-INDEX --fx-exposed SYN-D \
        --weights 0.4,0.3,0.2,0.1 --confidence 0.99 --cov lw --paths 100000 --value 1000000
```

**VaR/ES (1 period, positive = loss):**

| Method | Definition |
|---|---|
| Historical | VaR = the ⌈c·n⌉-th smallest loss; ES = mean of losses from there |
| Parametric | normal, with sample means and the chosen covariance (`sample`, `ewma` + `--lambda`, `lw` = Ledoit-Wolf) |
| Monte Carlo | multivariate normal with the same moments; seeded |

**Stress scenarios:**
- `--index` column −10 %, beta-scaled. Without `--index`, a uniform −10 %.
- SEK ±5 % on `--fx-exposed` instruments.
- Both combined.

## `qa optimize`

```bash
qa optimize --prices samples/synthetic-prices.csv --index SYN-INDEX --method minvar --max 0.4
qa optimize --prices … --method mv --risk-aversion 3      # mean-variance (sample means)
qa optimize --prices … --method rp                         # equal risk contribution
qa optimize --prices … --method hrp                        # hierarchical risk parity
```
- Output: weights, each asset's share of risk, and annualized expected return and volatility (`--periods-per-year`, default 252).
- Results are **in-sample** estimates: not validated out of sample and not a backtest. Honest evaluation arrives with the Phase 5 TrialLedger.

## Avanza (read-only, Phases 3–4)

These verbs talk to Avanza. Each invocation is **one trigger**: at most **one login**, never retried. There are **no** order or money-transfer verbs.

**Login** (`docs/setup.md` §5):
- `--login bankid` (default): scan the QR code with the BankID app and approve.
- `--login totp`: unattended, with credentials from Windows Credential Manager.
- The default comes from the `QA_AVANZA_LOGIN` environment variable when set.

| Verb | What it does |
|---|---|
| `qa secrets set` / `qa secrets check` | Store the credentials (prompts, no echo) / show which exist |
| `qa login` | One login (BankID QR by default) plus a session health check. Prints the method and where the security token came from, never the token. |
| `qa login --clear-lock` | Clear a persisted login lock after checking with BankID. No login is attempted. |
| `qa accounts [--json]` | Accounts, total value, buying power, available for purchase (ids masked `***123`). The last line says whether `AVANZA__ALLOWEDACCOUNTIDS` names an account that may trade live (R1: exactly one tradable ISK, not managed, no credit); `--json` gives `allowedForLiveTrading` per account. |
| `qa positions [--account 123] [--json]` | Holdings and cash; `--account` matches the end of the id |
| `qa orders [--json]` | Open orders |
| `qa quote ERIC-B` / `qa quote --id 5240` | Bid/ask/last, depth, tick size at the last price, lot size. Times are shown in Europe/Stockholm. |
| `qa probe [--ticker ERIC-B]` | One login, then every Phase 3 read. Prints OK / DRIFT / RECORDED per endpoint and records raw responses to `recordings/live/<utc-stamp>/`. |
| `qa probe --preflight [--account 123]` | The same, then Avanza's two read-only pre-trade checks (order validation, preliminary fee) for a **hypothetical** 1-share buy at the ask. **Nothing is placed.** It records their real answers, so the provisional Phase 7 DTOs can be confirmed. `--account` picks the account by the end of its id when several can trade. |
| `qa recordings sanitize --in <raw> --out <fixtures>` | Masks ids and names, replaces personal amounts (`--keep-amounts` to keep them), scans for leaks; writes nothing if a leak is found |
| `qa stream ERIC-B [VOLV-B …] [--duration 60] [--poll 5]` | Live quotes for 1–5 instruments. Order depth is pushed by Avanza (server-sent events) and the last trade is polled every `--poll` seconds. A line is printed whenever the visible quote changes. A quote is marked **STALE** when neither source has updated for 10 s or the depth stream is down. Records to `recordings/live/<utc-stamp>/` unless `--no-record`. Ctrl+C stops early. |
| `qa history import ERIC-B [--from yyyy-MM-dd] [--to yyyy-MM-dd] [--store data/quant.duckdb]` | Imports daily bars from Avanza's price chart (default: the last year) and adds or updates the instrument in the instrument master. Re-imports store only changed bars, as restatements with their own known-at time. |

Common options:
- `--login bankid|totp` (default `bankid`, or `QA_AVANZA_LOGIN`)
- `--state-dir` (default `state`)
- `--secret-store credman|env`
- `--verbose` (redacted debug log on stderr)

**Exit codes:**

| Code | Meaning |
|---|---|
| 0 | OK |
| 1 | Error, e.g. missing credentials or bad arguments |
| 2 | `sanitize` found a leak |
| 3 | **HALT**: schema drift, session expired, or endpoint moved |
| 4 | **LOCKED**: login is locked |

A Tier A drift message lists every unknown or missing JSON path, e.g. `$.accounts[0].newField`.

## Local data (offline, Phase 4)

These verbs never talk to Avanza.

| Verb | What it does |
|---|---|
| `qa history show ERIC-B [--from] [--to] [--as-of <time>] [--json]` | Stored daily bars with their source labels. Avanza history is labelled **NOT survivorship-free, NOT point-in-time**. `--as-of` shows what the store knew at that time: ISO 8601, and without an offset it is read as Stockholm time. |
| `qa instruments [--as-of <time>] [--json]` | The instrument master: orderbook id, ISIN, ticker, market, currency, trading model and tick table. |
| `qa calendar [--market XSTO\|XNYS\|XTSE] [--year 2026] [--date yyyy-MM-dd]` | A market's calendar from `config/market-calendar.<MIC>.<year>.json` (Nasdaq Stockholm unless `--market`; the US and Canadian ones for USD and CAD shares, ADR 0005): closed days, half days in the market's own time (and in Stockholm time for a foreign day), and whether each year has been verified (`verified_on`). |

- **Store:** `data/quant.duckdb` by default (git-ignored). Change it with `--store`.
- **Calendar files:** read from `./config`, else from next to `qa`. Change the folder with `--config-dir`.

## Backtesting (offline, Phase 5)

Plan, fill model and formulas: `docs/plans/05-phase5-backtesting.md`. Every run is appended to the TrialLedger
(`research/trial-ledger.jsonl`, committed), rejected runs included. These are model outputs, not advice.

| Verb | What it does |
|---|---|
| `qa backtest run --strategy ma-cross --param fast=20 --param slow=100 --synthetic 300x2520 [--seed N] [--drift 0.05]` | One run on seeded synthetic data (GBM, weekdays from 2015-01-05). Prints the data labels, cost status, metrics, Deflated Sharpe and the ledger id. |
| `qa backtest run --strategy buy-and-hold [--tickers ERIC-B,VOLV-B] [--from] [--to]` | The same on imported Avanza history (`qa history import` first). **Without `--tickers` (and without `--synthetic`) it uses your allowlist** (`config/universe.json`). `ERIC-B` and `"ERIC B"` both work. Only continuously traded instruments are accepted. The output is labelled **NOT survivorship-free, NOT point-in-time, current names only**. |
| `qa backtest sweep --strategy ma-cross --grid fast=10,20,50 --grid slow=100,200 --synthetic 50x2520` | Every combination is run and logged. Prints the top rows, the PBO (CSCV) across the combinations, and the Deflated Sharpe Ratio of the best against every completed trial in the study. |
| `qa costs [--amount 5000,20000,100000] [--capital 40000]` | Avanza's courtage classes (Start, Mini, Small, Medium, Fast Pris) with what one order of each amount costs in each class, and the cheapest class you can use. `--capital` drops classes you can't choose (Start: capital under 50,000 SEK). An order can't be larger than your capital, so Start is never offered for orders of 50,000 SEK or more. |
| `qa trials list [--study <key>] [--last 20] [--json]` | The ledger, newest last. |
| `qa trials verify` | Checks the hash chain. Any edited, deleted or reordered line is reported, with exit code 1. |

**Strategies:**
- `buy-and-hold [entry=5]`: equal weight, bought during the first `entry` bars, then held.
- `ma-cross fast=… slow=…`: long an instrument while its fast SMA is above the slow one; a fixed 1/N slice each.
- `random-targets seed=… [rebalance=21] [p=0.5]`: random long-only weights. This is the null model for deflation tests.

**Orders and costs:**
- `--order limit|moo|moc` (default `limit`).
- Limits sit `--limit-offset-bps 50` from the decision close and are rounded passively to the instrument's tick.
- Trades inside a 10 % no-trade band are skipped; entries and exits always trade.
- **Courtage class:** one file per Avanza class, `config/costs.<class>.json`: `avanza-start`, `avanza-mini`, `avanza-small`, `avanza-medium`, `avanza-fastpris`.
  - The values are for the Nasdaq Stockholm main market, from your screenshot of Avanza's price list (2026-09-26).
  - Pick a class per run with `--costs avanza-medium`, or change the default in `config/backtest-defaults.json`.
  - A class prints as **UNVERIFIED** until its `verified_on` is set.
- **Starting cash:** `--cash`, default from `config/backtest-defaults.json` (5,000 SEK with the Start class, your starting capital).
  - Start can only be chosen with less than 50,000 SEK, so more cash on Start is refused.
  - A run whose equity grows past 50,000 SEK is noted on the trial, because its later fees are likely understated.

**Guards:**
- **Holdout:** `config/holdout.json` locks bars from 2025-10-01. Without `--to`, data is clipped before that date (store data is not even read past it). An explicit `--to` inside the holdout is refused and logged as `rejected-holdout`. A missing policy file stops every run. Only you unlock it.
- **Look-ahead:** a strategy that reads past the current bar is rejected and logged as `rejected-leakage`. So is one whose decisions change when later bars are removed: 8 decision points are replayed on truncated data.
- **Runner:** the ledger records `QA_RUNNER` as the runner. Claude's sessions set `claude` (`.claude/settings.json`); set your own, e.g. `QA_RUNNER=owner`.
- **Paths:** the ledger defaults to `research/trial-ledger.jsonl` at the repository root (change it with `--ledger`). Config comes from `./config`, else from next to `qa` (change it with `--config-dir`).

Exit codes of `qa backtest run`: 0 ok, 1 error, 2 the run was logged but rejected (holdout, look-ahead) or failed.

## Paper trading (Phase 6)

Plan, decisions and gate: `docs/plans/06-phase6-trading-core.md`; rules: ADR 0003. In Paper mode every order goes
through the whole pipeline (lots → tick rounding → risk checks R1–R21 → `OrderGateway` → OMS) and is filled by a
simulated channel on **live quotes**. **Nothing is ever sent to Avanza:** Avanza is read for quotes and tick tables
only. Confirm and Auto cannot start in Phase 6.

| Verb | What it does |
|---|---|
| `qa fx import USD [CAD] --from yyyy-MM-dd [--to …]` / `qa fx show USD [--from … --to …]` | FX rates for foreign shares (ADR 0005): the Riksbank's daily fixing, SEK per unit, stored in the history store with the time it became known. Import calls the Riksbank, never Avanza; show is offline. `qa history import` of a USD or CAD share imports its fixings first (from 10 days before its first bar). |
| `qa universe list` / `qa universe add ERIC-B [VOLV-B …]` / `qa universe remove ERIC-B` | The instrument allowlist (risk check R2), `config/universe.json`, by orderbook id. It starts **empty**, so every order is rejected until you add names. `add` looks the ticker up offline in the instrument master (`qa history import` first). Shares in SEK, USD or CAD (USD and CAD trade on paper only, ADR 0005; any other currency is refused), at most 5 names (a Paper session streams each; `add` refuses a 6th), and only a share that trades continuously: Nasdaq Stockholm's main market (`XSTO`) or a US/Canadian exchange. Anything else (e.g. a First North share, which may trade only in auctions) is refused, because the backtest refuses it and the Paper decision loads the whole list the same way, so one such name would stop every decision (plan 18). The app's **Find a share** does the import and the add in one click. `remove` of a share the Paper book still holds (`--state-dir`, default `state`) moves it to the **exiting** list instead: the next session sells it (R2 lets an exiting share be sold, never bought), `list` and `qa status` show it, and once it is sold the same `remove` drops it (plan 21). |
| `qa risk-limits [--account-value 100000]` | Every limit with its R number, sized for the paper cash (5,000 SEK allows **500 SEK per order** and **1,000 SEK per instrument**) or the value you give. The first row is the **account cap** (`max_account_value_sek`, 5,000 SEK): the limits are sized on the account's value, but never on more than this, so `--account-value 100000` still gives 500 SEK per order. |
| `qa paper strategy ma-cross --param fast=20 --param slow=100` | Saves the strategy Paper trades in `config/paper.json`, after checking its name and parameters. Without a name it shows the saved one; `--clear` removes it. It notes when the trial ledger has no backtest of exactly that strategy on imported history. |
| `qa paper run [--strategy … --param …] [--duration 3600]` | One Paper session, with the saved strategy unless you give `--strategy`. Before any login it checks the promotion state, the limits, `config/paper.json`, the strategy, the allowlist (1–5 names) and the courtage class. Then: one read-only login, tick tables from Avanza, and **each name's daily history brought up to the last trading day** (read-only chart calls, a year back when there is none; if that fails it warns, and the decision refuses history that doesn't reach yesterday). It polls live prices every 5 s (since 2026-09-30 Paper opens no order-book stream: Avanza refuses it; a price is fresh while its poll is under 10 s old), and at the decision time (09:10 by default) the strategy decides on bars through **yesterday** and places day limit orders, paced 13 s apart. Started after 09:10, it decides as soon as every name has a live price (at most a minute; then it decides anyway and skips a name without one). Orders fill on the live quotes by the ADR 0003 §8 model and expire at the close. The session runs until two minutes after the close, or `--duration` seconds; Ctrl+C stops early and cancels everything. **US and Canadian shares on the list (ADR 0005, paper only):** the session first brings their Riksbank fixing up to date and uses the latest one all day (older than 4 days or missing: those names are skipped, the Swedish ones still trade). Each market decides on its own clock (09:40 New York / Toronto = 15:40 Stockholm), its orders expire at its own close, and the session ends two minutes after the last close (22:02 Stockholm) with one report. **Intraday research (plan 17):** after the session it stores the bid/ask it saw once a minute per name, and after Stockholm's close it collects the day's 1- and 5-minute bars like `qa intraday import`; a failure there is a warning only. |
| `qa paper status` | The paper book (`state/paper/book.json`): cash, positions at cost, realised P&L, fees; and whether a session is running. |
| `qa kill [--reason "…"]` | **Kill switch.** Writes `./KILL`: a running session halts within a second and cancels every working order through the gateway. Without a session, the next one refuses to start. |
| `qa kill --status` / `qa kill --reset [--reason "…"]` | Shows / clears the kill switch. Reset works only while no session runs (`state/session.lock`) and is audited. |
| `qa audit verify [--dir audit]` | Checks the audit log's hash chain across all days (`audit/YYYY-MM-DD.jsonl`, one record per pipeline step). Exit 2 when broken. |
| `qa intraday probe [TICKER \| --id N]` | Intraday research (plan 17, ADR 0006). Asks Avanza's public price chart **without a login** which bar sizes it gives for today, one week, one month and three months, then how many days of 1- and 5-minute bars come back. About 10 read-only calls; the raw answers are recorded under `recordings/live` (`--no-record` to skip). Nothing is stored or traded. |
| `qa intraday import [TICKER…] [--period today] [--resolution both] [--no-catch-up]` | Stores today's 1- and 5-minute bars from the public price chart, **without a login** (plan 17 step A2). Default: the allowlist's Stockholm shares and the research list; foreign shares are skipped (ADR 0006: Stockholm only). The answer must be the resolution asked for and on its minute grid, or nothing is stored; bars still open are left out, so run it after 17:30 and before midnight (Avanza keeps 1- and 5-minute bars for today only). **Catch-up (A2b):** for each share, the XSTO trading days of the last week without 5- or 10-minute bars are fetched once as 10-minute bars (`one_week`), and only those days are stored. There is no call when nothing is missing; without the calendar it is skipped, never guessed; `--no-catch-up` skips it. One name failing is reported and the others go on (exit 1). `qa paper run` does all this by itself after Stockholm's close. |
| `qa intraday research add\|remove\|list TICKER…` | The research list, `config/research-universe.json` (at most 30): Stockholm shares whose intraday bars are collected besides the allowlist's. `add` finds each ticker with Avanza's public search (no login) and takes only the SEK share with exactly that ticker. Nothing on it is ever traded. |
| `qa intraday backtest --strategy orb-long\|late-momentum\|open-close [--param k=v] [--grid k=v1,v2] [--tickers A,B] [--resolution five_minutes]` | Backtests an intraday strategy on the collected bars (plan 17 step A5, ADR 0006). Offline; every run and every grid point is logged to the TrialLedger. Default shares: the allowlist's Stockholm shares and the research list. Each day's open and close come from the XSTO calendar (half days close early). Market orders at the next bar's open pay half the spread (a share's own median when Paper sessions sampled it 30+ times, else the cost model's) plus slippage; everything is flat before the close. A share's day whose bars stop more than 30 minutes before the close is left out, and the output says how many. `--fallback` (with `backtest` and `report`) also uses the caught-up 10-minute bars, only for days no share has 5-minute bars for (a day is never mixed). The strategies get each day's bar length, so their times stay true. The source becomes `avanza-price-chart:5m+10m`, a separate study. The statistics, and the PBO of a `--grid` sweep, are on daily P&L. **The intraday holdout** (`config/holdout.intraday.json`, the owner's file): the last 20 collected days are never read while it is locked; with fewer days than that nothing runs yet. |
| `qa intraday report [--tickers A,B] [--resolution five_minutes] [--from D]` | The go/no-go report (plan 17 step A6). Offline. On the days before the intraday holdout it runs a fixed set, every run logged: `orb-long` with range 5, 15 and 30 minutes, `late-momentum` and `open-close`, at the configured costs (Start and its free trades by default). The candidate is the range with the best daily Sharpe. It shows the candidate's Deflated Sharpe over the whole study, the grid's PBO, a walk-forward (choose on the days before, trade the next 20), and the free trades' life at its pace. It shows the candidate and open-close with the same trades at Mini's courtage (re-costed, not re-run), and the net return per round trip at Mini with a t-statistic clustered by day. Then the proposed bar, each line PASS, FAIL or WAIT: at least 120 days, Deflated Sharpe ≥ 0.95, PBO ≤ 0.2, net per trade > 0 at Mini, beats open-close at Mini, holds on the holdout. The holdout line waits until you unlock the holdout; then the candidate, chosen without those days, and open-close are run on them (marked in the ledger). `--json` for the numbers. Each report adds its runs to the ledger again. |
| `qa report eod [--date yyyy-MM-dd \| --all] [--json]` | The end-of-day report of a day, **rebuilt from the audit log**, saved to `reports/eod/YYYY-MM-DD.json`. It covers orders sent and accepted, risk rejections by check, and every paper fill against the market's VWAP over the fill window (or the arrival mid for fills at entry). It also shows reconciliation runs, violations, events, and the day's value and fees. **Fill rate** (plan 19): one `limit` line per order that reached the market, with how much filled, the day's low/high and the close, whether the daily backtest would have filled it (the day traded through the limit), and what the unfilled part missed to the close, in SEK and bps (positive is a cost). The summary adds the day's totals and "missed vs the backtest" (only the orders the backtest would fill), and the last line gives the same totals over every day so far. `qa paper run` writes it at the close, and a partial one when stopped early. On a Confirm day it adds the **live execution quality**:
<br>• every confirmed order, with its average fill against the decision price and against the mid when it was sent, in bps (positive is a cost)
<br>• Avanza's quoted fee against the model's
<br>• the value-weighted mean slippage against the backtest's cost assumption (half-spread + slippage from the cost file) |
| `qa report gate` | Rebuilds every day and shows both promotion gates: how far Paper is from Confirm, and how far Confirm is from **Auto**. The Auto gate needs 20 confirmed live orders, none still Unknown at the end of a day, the mean slippage against the arrival mid within the backtest's assumption, no violations on Confirm days, and an intact audit chain. Auto itself arrives in Phase 8. |
| `qa report week [--week 2026-W40 \| --date yyyy-MM-dd] [--json]` | The **weekly summary** (plan 20), offline; saved to `reports/week/2026-W40.json`. Default: the week of the last day a session ran. It shows each trading day of the week up to today (clean, not clean, incomplete or **no session**, the day's return, the share invested, orders), the week's return, fees and the Confirm gate count. It compares **Paper with the saved strategy's recorded backtest** from the trial ledger (no backtest is run): the backtest's average day and its spread, scaled by Paper's share invested, give an expected return and a 95 % range for this week and since the first Paper day; the verdict is below, within or above. It also gives the limit fill rate (plan 19) and, per research share, the intraday days collected (1/5-minute, 10-minute catch-up only, missing) and the days collected so far against the go/no-go's need. |

**Before your first session** (`qa status` lists whichever of these are still missing):
1. `qa history import ERIC-B` (and your other names) so they are in the instrument master. After that, `qa paper run` keeps the history up to date itself.
2. `qa universe add ERIC-B …` (at most 5 names).
3. `qa backtest run --strategy …` to see how a strategy did on those names, then `qa paper strategy <name> --param …` to save it.
4. Check `config/paper.json`: courtage class (`avanza-start`), starting cash (5,000 SEK), decision time (09:10). An existing `state/paper/book.json` keeps its own cash and class. Move it away to start over.
5. `qa risk-limits` to see what the limits allow.

**How Paper decides and fills:**
- **Targets become orders like the backtest's:** whole lots, a 1 % cash buffer, a 10 % no-trade band, and limits 50 bps toward the market.
  - The limit is anchored on the **live** reference price (last trade if fresh, else the mid), not yesterday's close; R5's ±2 % collar would reject most gaps otherwise.
  - Orders are clipped to what R6 (per order) and R7 (per position) allow, so a large target is reached over several days.
- **Marketable at entry:** fills at the ask (buys) or bid (sells), up to the displayed volume. The rest rests.
- **Resting:** fills only when a later trade prints **through** the limit, at the limit, taking at most 10 % of the traded volume (shared by your resting orders, oldest first). A touch is not a fill.
- **Courtage** comes from the courtage class, charged per order (the minimum once), plus the class's FX fee for non-SEK instruments (none on Start while you are under its limit).
- **With 5,000 SEK:** R6 allows 500 SEK per order and R7 1,000 SEK per instrument, so a share priced above 500 SEK cannot be bought at all, and a larger target is built over several days. The limits are ADR 0003's, in `config/risk-limits.json`.
- **The account cap** (`max_account_value_sek`, 5,000 SEK) sizes R6, R7, R8 and R19 and the plan on the account's value, but never on more than the cap. A larger account doesn't raise any limit, and the loss stop never exceeds 100 SEK a day. The plan also clips buys to R7's and R8's room, counting the buys still working, so a full account gets no order rather than a rejection.

**Safety:**
- **Unknown outcomes:** a submit with an unknown outcome is never retried, and it blocks its instrument (R18) until reconciliation resolves it.
- **Reconciliation:** runs every 30 s. A mismatch halts trading, and if it lasts more than 60 s the kill switch fires.
- **The kill switch fires automatically on:**
  - 3 consecutive rejects
  - the daily loss stop (−2 %)
  - schema drift or a gone order endpoint
  - an order Unknown for more than 2 minutes
  - a reconciliation mismatch lasting more than 60 s
- **Promotion to Confirm is your step** (ADR 0003 §3). Claude's settings and hook block both that command and any write to the local promotion state.

Exit codes of `qa paper run`: 0 ok, 1 error, 3 halted (the kill switch fired, or was active at start), 4 login locked.

**What the end-of-day report counts:**
- **Violations** mean the system misbehaved or was unsafe:
  - an OMS invariant or reconciliation halt
  - schema drift or a gone endpoint
  - a refused fill, or a fill outside its limit
  - an order sent without a passing risk check just before it
  - an order still Unknown at the close
  - an automatic kill (other than the daily loss stop)
  - a broken audit chain
- **Events** are the rules working: risk rejections, a manual kill, the daily loss stop, a stale-data halt.
- **Clean day:** complete (it reached the close), no violations, every reconciliation matched, and every fill within ±200 bps of its reference (the R5 collar).

## Promotion (your command, ADR 0003 §3)

`qa promote` raises (or lowers) the highest mode the program may run in. It is **yours**: Claude's hook and settings block it, and block any write to `promotion/state.json`.

| Command | What it does |
|---|---|
| `qa promote --init-key` | Once: creates your promotion key in Windows Credential Manager (`QuantAnalyst:Promotion`). It signs every record, and it is never shown. Windows only. |
| `qa promote --to Confirm [--operator you]` | Checks the Confirm gate from the rebuilt reports, prints it, and asks you to type `Confirm` exactly. Then it appends an HMAC-SHA256-signed record to `promotion/state.json`, with the evidence reports' SHA-256 hashes, and sets `maxAllowed`. Nothing is written if the gate is not met or you type anything else. |
| `qa promote --to Paper` | Lowers the mode. No gate, but typed and signed the same way. |
| `qa promote --verify` | Checks every record's signature, the mode chain (each record starts where the last ended), `maxAllowed`, and that no evidence file changed. |

**The Confirm gate:**
- **10 clean Paper trading days in a row.** Any day that is not clean restarts the count, and it is never part of the evidence.
- **At least one order sent** in those days.
- **An intact audit chain.**

Promoting to Confirm does **not** start Confirm mode. Every Confirm session runs the startup checks first, including the check that `qa promote --verify` would pass, and refuses to start if any fails. Auto's gate needs Confirm results (20 confirmed live orders, slippage within the backtest's assumption), so `--to Auto` waits for Phase 7's reports (step 6).

## Confirm mode (Phase 7): real orders, each typed by you

**You** start these, in your own terminal. Claude Code can't: the hook blocks them, and the program refuses to start
Confirm when Claude Code started it. Before the first real session, go through the checklist in
`docs/handover-confirm.md`.

| Command | What it does |
|---|---|
| `qa trade run --mode confirm` | The daily Confirm session. It runs the startup checks, logs in once, brings the history up to yesterday and waits for the decision time. Then it shows an **order card** per order. You type the ticker plus `JA` (`ERIC-B JA`) within 30 s, and it re-checks everything before it sends. Anything else skips the order. It re-plans before every card, and each instrument gets one card a day. Stopping it (Ctrl+C, `qa kill`) cancels its working orders. |
| `qa rebalance` | What the strategy would trade **now** on your live account (R1): one login, the plan, and **nothing sent**. |
| `qa rebalance --mode confirm --execute` | The same cards as `qa trade run`, starting now (from 09:05) instead of at the decision time. |

Common options:
- the same as `qa paper run`: `--strategy`, `--param`, `--duration`, the folders, and `--login`
- `--mode` is required for every live start; only `confirm` exists, and Auto is Phase 8

**The startup checks.** The session prints all nine and refuses to start if any fails. The checks that need no login
run before the login, so a refusal there never asks for BankID:
1. not started from Claude Code (`CLAUDECODE` / `CLAUDE_CODE_ENTRYPOINT`)
2. the promotion verifies with your key and allows Confirm
3. this year's calendar and your courtage class are verified (R20)
4. the kill switch is off
5. no `state/trading-disabled.json`
6. this session holds the session lock
7. the audit chain is intact
8. **after the login:** the account in `AVANZA__ALLOWEDACCOUNTIDS` passes R1
9. the order channel is ready. **Today it isn't:** the Avanza order format stays provisional until your capture (O4, O5) and plan 07 step 7. Until then every start stops at this check, before any login.

`qa status` lists the checks that can run offline under "Confirm checks" once you are promoted to Confirm.

Exit codes of `qa trade run` and `qa rebalance --execute`:
- 0 ok
- 1 error (e.g. a missing `--mode`)
- 3 refused by the startup checks, or halted or killed during the session
- 4 login locked
