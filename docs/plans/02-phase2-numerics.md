# 02 — Phase 2: pricing, risk, portfolio (C++)

- **Status:** in progress (2026-09-25)
- **Scope:** master plan §4 Phase 2 and the `<modules>` NATIVE section of the spec.
- **Gate:** every numerical item in `<verification_requirements>` passes, with seeds recorded.

## Independent reference values

`tools/reference/phase2_reference.py` uses numpy 2.4, scipy 1.17 and scikit-learn 1.9. It generates expected values into `tests/native/reference/phase2_reference.inc`, and the Joe-Kuo Sobol table into `native/src/qe/core/sobol_joe_kuo.inc`. The script is **never used at runtime**; the generated files are committed together with the script's provenance header.

| Check | Reference |
|---|---|
| Inverse normal CDF | `scipy.stats.norm.ppf` |
| Sobol points (unscrambled) | `scipy.stats.qmc.Sobol(scramble=False)`, direction numbers `new-joe-kuo-6.21201` via scipy |
| Sample covariance | `numpy.cov` |
| Ledoit-Wolf | `sklearn.covariance.LedoitWolf` |
| HRP | the published algorithm (López de Prado 2016) with `scipy.cluster.hierarchy.linkage(…, 'single')` |
| Quantiles | `numpy.percentile` (linear / type 7) |
| CRR lattice | an independent numpy implementation |

**Textbook anchors** (Hull, *Options, Futures, and Other Derivatives*):
- BS c = 4.76, p = 0.81 (Ex. 15.6)
- 5-step CRR American put = 4.49 (Ex. 21.1)

## Conventions (fixed here, tested)

### Returns, VaR and ES
- Returns are fractional (−0.02 = −2 %).
- VaR and ES are **positive loss fractions**. Confidence α ∈ (0.5, 1).
- **Historical:** losses lᵢ = −rᵢ, sorted ascending.
  - VaR = l₍ₖ₎ with k = ⌈α·n⌉ (empirical quantile, no interpolation).
  - ES = the mean of l₍ₖ₎ … l₍ₙ₎. So ES ≥ VaR by construction.
  - Hand check: losses 1..100, α = 0.95 → VaR 95, ES 97.5.
- **Parametric (normal):** VaR = −μₚ + σₚ·z_α; ES = −μₚ + σₚ·φ(z_α)/(1−α).
- **Monte Carlo:** draw r ~ N(μ, Σ) via a Cholesky factor, then apply the historical estimator to wᵀr. The result reports seed and path count.

### Covariance
- **Sample:** unbiased (T−1).
- **EWMA:** weights ∝ λ^(T−1−t), normalized to sum 1, with the weighted mean removed. λ = 1 gives the biased (1/T) sample covariance. The default λ = 0.94 follows RiskMetrics.
- **Ledoit-Wolf** toward μ·I (Ledoit & Wolf 2004): identical to scikit-learn's `LedoitWolf(assume_centered=False)`, including the 1/T empirical covariance. It returns the shrinkage intensity.

### Stress
- Scenario P&L = Σ vᵢ·sᵢ over position values v (SEK) and shocks s.
- **Built-in scenarios** (C#): OMXS30 −10 % applied through OLS betas to the index; SEK ±5 % applied to non-SEK positions.

### Monte Carlo
- **RNG:** `std::mt19937_64`, whose output sequence the C++ standard specifies exactly. Normals come from our own inverse CDF, **not** `std::normal_distribution`, which differs between standard libraries.
- **Blocks:** paths run in fixed blocks of 4096, and block b uses seed `splitmix64(seed ⊕ splitmix64(b))`. The result is therefore identical whatever thread count we use later.
- **Estimators:**
  - plain
  - antithetic (pair averages)
  - control variate: discounted S_T with known mean S·e^(−qT); β is estimated from the same samples
  - Sobol randomized QMC: R digital shifts, SE computed across replications

  The flags combine.
- **Result:** price, SE, sample count, and the seed actually used. A seed of 0 means "derive from the engine seed".

### Greeks and implied vol
- **Greeks** are per unit: vega per 1.00 of vol; theta = −∂V/∂T per year; rho per 1.00 of rate. They require σ > 0 and T > 0.
- **Implied vol** uses Brent on σ ∈ [1e-8, 10], expanding the upper end to 1000 if needed. A target outside the strict no-arbitrage bounds gives a per-element `INVALID_ARG`.

### CRR lattice
u = e^(σ√Δt), d = 1/u, p = (e^((r−q)Δt) − d)/(u − d). The code requires 0 < p < 1, σ > 0 and T > 0.

### Optimizers
- **MV / min-variance:** minimize ½λ·wᵀΣw − μᵀw subject to Σw = 1 and l ≤ w ≤ u.
  - Method: FISTA projected gradient with adaptive restart. The exact projection onto {budget, box} uses bisection on the multiplier.
  - Step size is 1/L, where L = λ·λ_max(Σ).
  - Σl ≤ 1 ≤ Σu is checked up front.
  - Tests check KKT optimality and agreement with the closed-form min-variance solution.
- **Risk parity (equal risk contribution):** cyclical coordinate descent (Griveau-Billion, Richard, Roncalli 2013). Long-only by construction; bounds are rejected.
- **HRP:** López de Prado (2016).
  1. Compute the correlation distance d = √(½(1−ρ)).
  2. Compute the Euclidean distance between columns of d.
  3. Cluster with single linkage.
  4. Quasi-diagonalize the order.
  5. Allocate by recursive bisection with inverse-variance weights.

  Bounds are rejected.

### Integer-lot rebalance
Inputs:
- per asset: price, target weight, current quantity, lot size
- cash, cash buffer, minimum trade value
- a courtage model fee = max(fee_min, fee_rate·value). This form fits every Avanza class: Small = max(39, 0.15 %), Mini = max(1, 0.25 %), Medium = max(69, 0.069 %), Fast Pris = max(99, 0).

Algorithm:
1. Compute ideal quantities from w·(V − buffer).
2. Round trades toward the target in lot multiples; a sell never goes below zero.
3. Drop trades under the minimum value.
4. Repair cash feasibility by trimming the most-overweight buys lot by lot.
5. Greedily add lots to the most-underweight names while cash allows and tracking error falls.

Every trade is a lot multiple, final quantities are ≥ 0, cash after trades is ≥ the buffer (or `feasible = 0` is reported), and ties break by index for determinism.

## ABI 1.1 (additions only; minor version bump)
- **Pricing:** `qe_bs_greeks_batch`, `qe_implied_vol_batch`, `qe_lattice_batch`, `qe_mc_european`.
- **Risk:** `qe_covariance`, `qe_var_es_historical`, `qe_var_es_parametric`, `qe_var_es_monte_carlo`, `qe_betas`, `qe_stress_pnl`.
- **Portfolio:** `qe_optimize` (min-var / MV / risk parity / HRP), `qe_rebalance`.
- **Layouts:** matrices are row-major `double*` with explicit dimensions. Every new struct is pinned by `static_assert`s and by managed layout tests.

## .NET
- **Bindings:** `QuantAnalyst.Native` wrappers over spans.
- **`QuantAnalyst.Analytics`:**
  - CSV price/return loader (date column + one column per instrument; simple or log returns)
  - result labels (source, date range, observations, currency, survivorship status, method, confidence, seed/paths)
- **`QuantAnalyst.Cli` (`qa`):** `price`, `risk`, `optimize`.

## Out of scope for Phase 2 (noted for later)
- Knock-out/turbo payoffs. The MC already supports multiple steps; barrier payoffs arrive with warrant analysis.
- Multithreading. The block structure keeps results independent of the thread count once it is added.
- A QuantLib cross-check (optional vcpkg feature, still off).
