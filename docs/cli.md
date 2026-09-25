# `qa` command-line tool

`qa` is `src/QuantAnalyst.Cli`. Build it with `dotnet build QuantAnalyst.sln`. It needs the native library staged by `cmake --build --preset dev` (see `docs/setup.md`).

```bash
dotnet run --project src/QuantAnalyst.Cli -- <command> [options]
# or the built executable: src/QuantAnalyst.Cli/bin/Debug/net10.0/qa
```

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

## Avanza (read-only, Phase 3)

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
