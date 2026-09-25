#pragma once

#include "qe/core/linalg.hpp"

namespace qe::portfolio {

struct SolverOptions {
    int max_iterations{100'000};
    double tolerance{1e-12}; ///< max-norm change of the iterate between iterations
};

struct OptimizationResult {
    Vector weights;
    double objective{};
    int iterations{};
    bool converged{};
};

/// Euclidean projection of v onto {w : sum(w) = 1, lower <= w <= upper} (bisection on the
/// budget multiplier). Throws InvalidArgument when the set is empty (sum(lower) > 1 or
/// sum(upper) < 1) or lower > upper somewhere.
[[nodiscard]] Vector project_budget_box(VectorCRef v, VectorCRef lower, VectorCRef upper);

/// Minimizes 0.5 * risk_aversion * w' cov w - mu' w  s.t.  sum(w) = 1, lower <= w <= upper.
/// FISTA projected gradient with adaptive restart; step 1 / (risk_aversion * lambda_max(cov)).
[[nodiscard]] OptimizationResult mean_variance(VectorCRef mu, MatrixCRef cov, double risk_aversion,
                                               VectorCRef lower, VectorCRef upper,
                                               const SolverOptions& options = {});

/// mean_variance with mu = 0 and risk_aversion = 1 (objective = 0.5 * portfolio variance).
[[nodiscard]] OptimizationResult min_variance(MatrixCRef cov, VectorCRef lower, VectorCRef upper,
                                              const SolverOptions& options = {});

/// Equal risk contribution, long-only, fully invested: cyclical coordinate descent on
/// 0.5 y' cov y - (1/N) sum log y (Griveau-Billion, Richard, Roncalli 2013), w = y / sum(y).
/// Objective reported: max relative deviation of risk contributions from 1/N.
[[nodiscard]] OptimizationResult risk_parity(MatrixCRef cov, const SolverOptions& options = {});

} // namespace qe::portfolio
