# `qa` command-line tool

`qa` is `src/QuantAnalyst.Cli`. It needs the native library built once by `.\build.ps1` (or `cmake --build --preset dev`; see `docs/setup.md`).

`qa` is **not on your PATH**, so typing a bare `qa` fails with "The term 'qa' is not recognized". Use the launcher at the repository root instead. It rebuilds qa when the sources changed (e.g. after `git pull`) and runs it with the repository as its working folder:

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
| `qa accounts [--json]` | Accounts, total value, buying power, available for purchase (ids masked `***123`) |
| `qa positions [--account 123] [--json]` | Holdings and cash; `--account` matches the end of the id |
| `qa orders [--json]` | Open orders |
| `qa quote ERIC-B` / `qa quote --id 5240` | Bid/ask/last, depth, tick size at the last price, lot size. Times are shown in Europe/Stockholm. |
| `qa probe [--ticker ERIC-B]` | One login, then every Phase 3 read. Prints OK / DRIFT / RECORDED per endpoint and records raw responses to `recordings/live/<utc-stamp>/`. |
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
| `qa calendar [--year 2026] [--date yyyy-MM-dd]` | The Nasdaq Stockholm (XSTO) calendar from `config/market-calendar.XSTO.<year>.json`: closed days, half days, and whether each year has been verified (`verified_on`). |

- **Store:** `data/quant.duckdb` by default (git-ignored). Change it with `--store`.
- **Calendar files:** read from `./config`, else from next to `qa`. Change the folder with `--config-dir`.

## Backtesting (offline, Phase 5)

Plan, fill model and formulas: `docs/plans/05-phase5-backtesting.md`. Every run is appended to the TrialLedger
(`research/trial-ledger.jsonl`, committed), rejected runs included. These are model outputs, not advice.

| Verb | What it does |
|---|---|
| `qa backtest run --strategy ma-cross --param fast=20 --param slow=100 --synthetic 300x2520 [--seed N] [--drift 0.05]` | One run on seeded synthetic data (GBM, weekdays from 2015-01-05). Prints the data labels, cost status, metrics, Deflated Sharpe and the ledger id. |
| `qa backtest run --strategy buy-and-hold --tickers "ERIC B,VOLV B" [--from] [--to]` | The same on imported Avanza history (`qa history import` first). Only continuously traded instruments are accepted. The output is labelled **NOT survivorship-free, NOT point-in-time, current names only**. |
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
- **Starting cash:** `--cash`, default from `config/backtest-defaults.json` (45,000 SEK with the Start class).
  - Start can only be chosen with less than 50,000 SEK, so more cash on Start is refused.
  - A run whose equity grows past 50,000 SEK is noted on the trial, because its later fees are likely understated.

**Guards:**
- **Holdout:** `config/holdout.json` locks bars from 2025-10-01. Without `--to`, data is clipped before that date (store data is not even read past it). An explicit `--to` inside the holdout is refused and logged as `rejected-holdout`. A missing policy file stops every run. Only you unlock it.
- **Look-ahead:** a strategy that reads past the current bar is rejected and logged as `rejected-leakage`. So is one whose decisions change when later bars are removed: 8 decision points are replayed on truncated data.
- **Runner:** the ledger records `QA_RUNNER` as the runner. Claude's sessions set `claude` (`.claude/settings.json`); set your own, e.g. `QA_RUNNER=owner`.
- **Paths:** the ledger defaults to `research/trial-ledger.jsonl` at the repository root (change it with `--ledger`). Config comes from `./config`, else from next to `qa` (change it with `--config-dir`).

Exit codes of `qa backtest run`: 0 ok, 1 error, 2 the run was logged but rejected (holdout, look-ahead) or failed.
