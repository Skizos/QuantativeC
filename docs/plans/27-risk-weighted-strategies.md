# 27 — Risk-weighted strategies

- **Status:** built 2026-10-06 at the owner's request ("do all of them": item 5 of the 2026-10-01 improvement review).
- **Gate:** tests for every part; all managed tests green; nothing live. No backtest on the owner's data was run here (the
  data is on the owner's PC; and every run is a trial in the ledger: the owner chooses what to try).

## Why

The two real strategies were `buy-and-hold` and `ma-cross`. The native engine has had risk-based portfolio methods since
Phase 2 (`qe::portfolio`: minimum variance, mean-variance, risk parity, HRP) that no strategy used. Weighing shares by
their risk is the classic alternative to equal weights: on a short list of Swedish large caps it mainly avoids letting
one volatile share drive the account.

## What

- **`inverse-vol [lookback=63] [rebalance=21]`**: weight ∝ 1 / the standard deviation of the last `lookback` daily
  returns.
- **`risk-parity [lookback=126] [rebalance=21]`**: equal risk contribution from the covariance of the last `lookback`
  daily returns (Ledoit-Wolf shrinkage, `qe_covariance`), solved by `qe_optimize` with `QE_OPT_RISK_PARITY`. Shares need
  a close on every day of the window; without convergence the weights fall back to inverse volatility. The strategy
  owns a native engine (created at the first solve) and is disposed where strategies are made (the backtest, its
  leakage check, Paper and Confirm: `StrategyReplay.DecideAtLastBar(panel, factory)`).
- Both: fully invested among the shares with enough history (0 for the rest), recomputed every `rebalance` bars (and on
  each bar until the first weights exist), and **stated on every bar** (never "hold"), so a new book is invested at
  once; the backtest's and Paper's no-trade band keeps small drift from trading.
- Parameters are checked: 20 ≤ lookback ≤ 1000, 1 ≤ rebalance ≤ 252.

**A hole found on the way (fixed):** Paper and Confirm replay the strategy over the whole history and trade its last
bar's targets. `buy-and-hold` sets targets only in its first `entry` bars and then says "hold" (NaN), which keeps a
backtest's position, but a new Paper book has none: with a year of history it would never have bought. The replay now
gives a share left as "hold" the last target the strategy set (`StrategyReplay.DecideAtLastBar`); a share never given a
target stays "hold". `ma-cross` and the new strategies state targets every bar, so nothing changes for them.

| Part | Code | Tests |
|---|---|---|
| The strategies | `RiskWeightedStrategy`, `InverseVolatility`, `RiskParity` (`RiskStrategies.cs`), `StrategyCatalog` | `RiskStrategiesTests` (2:1 volatility → 2/3 and 1/3; no history → 0; constant between rebalances and stated every bar; two shares that move together get less than one alone under risk parity; a late-listed share left out; parameter checks), `BacktestTests.HonestStrategies_PassTheLeakageCheck` (both, through the whole runner), `BacktestTests.CatalogMetadata_…` |
| Disposal | `BacktestRunner` (run and leakage check), `StrategyReplay.DecideAtLastBar(panel, factory)` (Paper, Confirm) | the same |
| Buy-and-hold in Paper | `StrategyReplay.DecideAtLastBar` (the last target set) | `PaperSessionTests.TheDecisionIsTheOneAtTheLastBar_…` |
| The app | The Strategy page lists them (from the catalog) | `ViewModelTests.TheStrategyForm_…` |

## How to use them (the owner)

Pick up front which to compare, e.g. `buy-and-hold`, `inverse-vol` and `risk-parity` with their defaults, backtest each
once on the list (`qa backtest run --strategy inverse-vol`), and keep one only if it beats `buy-and-hold` after costs
with a Deflated Sharpe that still means something. Then `qa paper strategy <name>`; the weekly summary compares it with
holding the list (plan 24).
