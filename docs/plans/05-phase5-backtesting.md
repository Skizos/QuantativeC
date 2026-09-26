# 05 — Phase 5: Honest backtesting

- **Status:** implemented and gated on synthetic data (2026-09-26); real-data runs wait for the history import. Started in parallel with Phase 4's stop point, at the owner's request.
- **Scope:** master plan §4 Phase 5; CLAUDE.md "Backtesting rules"; ADR 0001 (C ABI); ADR 0003 (costs, limits).
- **Gate:**
  - the leakage canary is caught
  - a random strategy is not significant after deflation
  - untouched limits do not fill
  - the 10 y × 300 synthetic benchmark is saved
- **Not part of this start:** anything that needs your data. That covers backtests on imported Avanza history (after `qa history import` works for you), the OMXS30 effective-dated membership file, and verified cost numbers. The code paths exist; they run on real data once it's there.

## Decisions I took (tell me if you want any changed)

| Question | Decision | Why |
|---|---|---|
| Where strategies run | In **.NET**. The **C++ engine** does fills, costs and bookkeeping, with **one batched call per bar** (all instruments). | Strategies stay easy to write and test. The per-bar call is one interop transition for N instruments (ADR 0001: batch APIs). |
| Look-ahead protection | **Structural:** a strategy sees a window that ends at bar t, and its orders can only fill on t+1 or later. It is also **checked:** the leakage check recomputes the targets on history truncated at seeded checkpoints, and any difference fails the run. | The master plan's "leakage canary" gate: a strategy that peeks is caught, not trusted. |
| Fill model | See "Engine" below. Limits fill only when the market **trades through** them; touching is not enough. Marketable limits and market orders fill in the auctions (open/close). Participation is capped at 10 % of the bar volume. Day orders expire. | Conservative, and matches the master plan. |
| Costs | `config/costs.avanza-small.json`: courtage `max(39 SEK, 0.15 % × value)`, FX fee 0.25 % for non-SEK, slippage 5 bps and half-spread 5 bps on market-type fills. **`verified_on: null`**: every report says "costs UNVERIFIED" until you check Avanza's price list. | CLAUDE.md: costs always on; don't hard-code from memory. The figures are the master plan's search extract and live in config, not in code. |
| Final holdout | `config/holdout.json`: **all data from 2025-10-01 on is locked.** The runner refuses any backtest that touches it unless you set `"locked": false` with a reason. Claude is blocked from editing the file (settings deny rule plus the guardrail hook). | CLAUDE.md: "Final holdout window locked unless I unlock it." The start date is your call; change it before you run anything real. |
| TrialLedger | `research/trial-ledger.jsonl`, **committed**, append-only, SHA-256 hash-chained. Every `qa backtest` evaluation is logged, including rejected ones (leakage, holdout). `qa trials verify` checks the chain. | CLAUDE.md: log EVERY evaluation, including mine. Committing it makes my runs visible to you. |
| Deflated Sharpe | N and the Sharpe variance come from the ledger's trials of the same **study** (strategy, universe, date range, data source). With N < 5 the report says the deflation is weak. | Bailey & López de Prado (2014); the expected-maximum approximation needs N ≫ 1. |
| PBO | CSCV (Bailey, Borwein, López de Prado, Zhu 2017) with S = 16 by default, for `qa backtest sweep` (several configurations). | The paper's recommended S; a single configuration has no PBO. |
| Annualisation | 252 trading days per year. The report prints the factor. | Common convention. The calendar has ~250 XSTO days; the difference is < 0.5 % in Sharpe. |
| Money in the engine | **double.** The backtest is a model (CLAUDE.md: models use double). Nothing it produces is sent to Avanza. | Live order prices stay `decimal` and tick-rounded in .NET. |
| Tick rounding | Limit prices are rounded **passively in .NET** (buy down, sell up) with the instrument's `TickSizeTable`, before the engine sees them. Synthetic instruments use a flat 0.01 table, and the report says so. | The same rounding code as live (CLAUDE.md: round before risk checks). |
| Short selling, leverage | Not allowed (ISK). Sells are capped at the position; buys at the cash available when they fill. | Master plan §1 (ISK). |

## Independent reference values

`tools/reference/phase5_reference.py` (numpy/scipy only) computes expected values for the statistics tests into `tests/QuantAnalyst.Analytics.Tests/Reference/phase5_reference.json`:
- Sharpe
- PSR and DSR, from the formulas in the papers
- PBO, from a direct CSCV implementation

The same values were also cross-checked, **outside the repo**, against [esvhd/pypbo@4d723f0](https://github.com/esvhd/pypbo/tree/4d723f06498267a2a6280cb9d7d5649348e961d1). pypbo is AGPL-3.0, so none of its code is copied.

**Formulas** (per-period Sharpe; T observations; γ₃ skewness; γ₄ kurtosis, non-excess):
- PSR(SR*) = Φ( (SR̂ − SR*)·√(T−1) / √(1 − γ₃·SR̂ + (γ₄−1)/4·SR̂²) )
  - Bailey & López de Prado 2012, "The Sharpe Ratio Efficient Frontier", *Journal of Risk* 15(2); SSRN 1821643.
- DSR = PSR(SR₀), with SR₀ = √V[SR_n] · ((1−γ)·Φ⁻¹(1−1/N) + γ·Φ⁻¹(1−1/(N·e))), where γ is the Euler–Mascheroni constant.
  - Bailey & López de Prado 2014, "The Deflated Sharpe Ratio", *Journal of Portfolio Management* 40(5); SSRN 2460551.
- PBO:
  1. Split the T×N performance matrix into S blocks.
  2. For every C(S, S/2) split into train and test halves, take the best in-sample configuration n*.
  3. Compute its out-of-sample relative rank ω = rank/(N+1) and λ = ln(ω/(1−ω)).
  4. PBO = share of splits with λ ≤ 0.
  - Bailey, Borwein, López de Prado, Zhu 2017, "The Probability of Backtest Overfitting", *Journal of Computational Finance* 20(4); SSRN 2326253.
- Purged, embargoed K-fold:
  - drop training observations whose label window overlaps the test fold
  - drop an embargo of h observations after each test fold
  - López de Prado 2018, *Advances in Financial Machine Learning*, ch. 7.

The papers' own PDFs can't be reached from this container (SSRN is blocked). The formulas above are the published ones; the reference script implements them independently of our C# code.

## Engine (`qe::backtest`, ABI 1.2)

**One call per bar:** `qe_bt_step(handle, bars[N], orders[], …) → fills[], state`.
- Orders submitted in the call for bar t+1 were decided at the close of bar t.
- A bar with `valid = 0` (no trading: holiday, halt, not listed yet) fills nothing, and its day orders expire.

**Order types:**

| Type | Fills on the bar | Price |
|---|---|---|
| `LIMIT` buy L | open ≤ L ⇒ in the opening auction; else low < L ⇒ in continuous trading; else nothing (**a touch is not a fill**) | open, or L |
| `LIMIT` sell L | open ≥ L; else high > L; else nothing | open, or L |
| `MOO` (market on open) | always, if the bar is valid | open ± (half-spread + slippage) |
| `MOC` (market on close) | always, if the bar is valid | close ± (half-spread + slippage) |

**Limits on every fill:**
- **Participation:** quantity ≤ ⌊cap · bar volume / lot⌋ · lot. The unfilled rest expires (day orders).
- **Long-only:** a sell is capped at the position; a buy is capped at the lots its cost can buy from the cash available.

**Costs per fill:**
- courtage = max(min, rate × notional)
- FX fee = fx_rate × notional for non-SEK instruments
- spread and slippage cost is reported separately for market-type fills

**Bookkeeping:** the engine keeps cash, positions, mark-to-market equity at the close, and cumulative costs by kind.

**No allocation per bar:** every buffer is sized at `qe_bt_create`.

## .NET (`QuantAnalyst.Analytics.Backtesting`)
- **`IStrategy`:** `Decide(BarWindow window, Span<double> targetWeights)`. `BarWindow` exposes bars `0..t` only; indexing past t throws.
- **Built-in strategies:** `BuyAndHold`, `MovingAverageCross(fast, slow)` and `RandomTargets(seed)`. `LeakyCanary`, which peeks at t+1, lives in the tests only.
- **`BacktestRunner`:**
  1. step the engine with bar t
  2. record fills and equity
  3. ask the strategy for targets at the close of t
  4. convert targets to orders: integer lots, limit = close·(1 ± offset), rounded passively to the tick
  5. submit them for t+1
  6. at the end, compute metrics and log a trial

  The runner is sealed against the holdout and runs the leakage check.
- **`Metrics`:** daily returns, total return, CAGR, volatility, Sharpe (per period and annualised), skewness, kurtosis, maximum drawdown, turnover, costs by kind, PSR(0), and DSR (study N and variance from the ledger).
- **`TrialLedger`:** see the decisions table. Each record holds:
  - id, UTC time, runner (`QA_RUNNER`, e.g. `claude`)
  - git commit
  - study key, strategy and canonical parameters, universe
  - data source with its labels, date range, seed
  - cost config and its verification status, holdout status
  - status (`ok`, `rejected-leakage`, `rejected-holdout`) and metrics
  - `prevHash`, `hash`
- **Validation:** `Pbo.Cscv`, `PurgedKFold`, `WalkForward`.
- **Synthetic data:** seeded GBM daily bars with consistent OHLC and lognormal volume. The seed is stored with every result.

## CLI
| Verb | What it does |
|---|---|
| `qa backtest run --strategy ma-cross --param fast=20 --param slow=100 (--tickers ERIC-B,VOLV-B \| --synthetic 300x2520) [--from] [--to] [--seed]` | One run: metrics, DSR, labels, ledger id |
| `qa backtest sweep --strategy ma-cross --grid fast=10,20,50 --grid slow=100,200 …` | All combinations; ranks; PBO; DSR of the best; every combination is logged |
| `qa trials list [--study]` / `qa trials verify` | The ledger and its hash chain |

Real-data runs read `data/quant.duckdb` (Phase 4 import) and label it "Avanza history — NOT survivorship-free, NOT point-in-time". There is no OMXS30 membership file yet, so universe selection is labelled "current names only (survivorship-biased)".

## Test map (gate items in bold)
| Area | Tests |
|---|---|
| Engine (gtest) | **untouched limit (at the low, or above it) does not fill**; trade-through fills at L; gap fills at the open; MOO/MOC prices with spread/slippage; participation cap and expiry; long-only caps; courtage min/rate; FX fee; invalid bar; t+1 (orders never fill on the bar they were decided on); no allocation in `step` (counting allocator) |
| ABI | layouts of the new structs on both sides; version 1.2 |
| Statistics | Sharpe/PSR/DSR/PBO against `phase5_reference.json`; purged/embargoed K-fold has no overlap and honours the embargo; walk-forward windows |
| Runner | **leakage canary rejected** (and logged as rejected); honest strategies pass the check; holdout refused while locked; tick rounding passive; lots; ISK caps; deterministic for a seed |
| **Deflation** | 200 random-target strategies on seeded random-walk data: the best raw Sharpe looks good, **its DSR < 0.95**, and PBO ≈ 0.5 |
| Ledger | hash chain verifies; a tampered line is detected; concurrent appends are serialised; the study N and variance feed DSR |
| **Benchmark** | 10 y × 300 instruments (2520 × 300) MA-cross run, timed; saved to `bench/results/phase5-<machine>.json` |

## Results

Gate run on 2026-09-26 in the cloud container (Linux x64, GCC 13.3, .NET 10.0.12). Test counts at 7389223: native dev 127/127, ASan + UBSan 126/126 (the allocation test does not run under sanitizers), managed 447 (1 skipped, Windows-only).

| Gate item | Evidence | Result |
|---|---|---|
| **The leakage canary is caught** | `BacktestRunnerTests.LeakageCanary_IsRejected_AndLogged`: a strategy whose factory reads tomorrow's close from the data it is given. `ReadingTheNextBarThroughTheWindow_IsRejected`: one that indexes past the current bar. | Both are rejected as `RejectedLeakage` and logged. Honest strategies (buy-and-hold, ma-cross, random-targets) pass the same check. |
| **A random strategy is not significant after deflation** | `DeflationGateTests`: 200 random-target strategies on a driftless random walk (20 instruments × 756 days, seed 20260926). | The best has annualised Sharpe 1.02 and PSR(0) 0.961: significant before deflation. Its **DSR is 0.414** (< 0.95), and **PBO is 0.388**. |
| **Untouched limits do not fill** | gtest `BacktestEngine.UntouchedLimitsDoNotFill`: limits at the low/high (a touch), and beyond it | No fills. A trade-through fills at the limit; a gap fills at the open. |
| **10 y × 300 benchmark saved** | `bench/results/phase5-cloud-linux-x64-managed.{json,md}` (BenchmarkDotNet, Release) | **169 ms** per full run (2520 steps, decisions, orders, 8 truncation replays, metrics); 77 ms without the leakage check; 0.83 s end to end through the CLI. |

**What the first logged run shows (ledger T000001, runner `claude`):**
- The CLI run of the benchmark configuration (300 names, 1 MSEK, MA-cross 20/100) lost 96 %.
- The cause was 25,412 fills × the 39 SEK minimum courtage on 3,333 SEK slices.
- With Avanza's minimum fee, many small positions are ruinous; the cost model makes that visible.

**Found and fixed on the way:**
- **Engine UB:** the engine formed a reference one past the fill buffer once every order had filled; MSVC's checked `std::span` caught it in CI. Debug builds now also define `_GLIBCXX_ASSERTIONS`, so GCC/Clang check the same thing.
- **Ledger locking:**
  - The TrialLedger's lock retry gave up after about 1 s on Windows, where concurrent writers each fsync.
  - Readers had no retry at all.
  - Both now wait up to 30 s, and only on lock contention.

**Open (needs you):**
- **Real-data backtests:** they need a working `qa history import`. The code path (`--tickers`) is tested against a temporary store.
- ~~**Cost model:** check `config/costs.avanza-small.json` against Avanza's price list and set `verified_on`.~~ Done by the owner on 2026-09-26; the values were unchanged. Runs logged before that (T000001) keep `costsVerified: false`.
- **Courtage classes (2026-09-26):**
  - The owner sent Avanza's class table: Start 0 % / 0 kr (only with capital under 50,000 kr), Mini 0.25 % / min 1 kr, Small 0.15 % / min 39 kr, Medium 0.069 % / min 69 kr, and Fast Pris a flat 99 kr. All are Nasdaq Stockholm main market.
  - Each class is a `config/costs.<class>.json`. `config/backtest-defaults.json` picks Start with 45,000 SEK, a placeholder until the owner gives the real starting capital.
  - `qa costs` compares the classes for given trade sizes.
  - Backtests refuse cash at or above a class's capital limit, and note a run whose equity grows past it.
  - Open: the FX fee per class is not on that table, so 0.25 % is kept for all of them.
- **OMXS30 membership:** there is no membership file yet, so real universes are "current names only (survivorship-biased)".
