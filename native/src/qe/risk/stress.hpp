#pragma once

#include "qe/core/linalg.hpp"

#include <span>

namespace qe::risk {

/// OLS betas of each instrument (columns of a T x N return matrix) on an index return series.
/// Throws InvalidArgument if sizes disagree, T < 2, or the index has zero variance.
[[nodiscard]] Vector betas(MatrixCRef returns, std::span<const double> index);

/// Scenario P&L: pnl_s = sum_i values_i * shocks_(s, i) for an S x N shock matrix (fractional
/// returns) and position values (portfolio currency).
[[nodiscard]] Vector stress_pnl(VectorCRef values, MatrixCRef shocks);

} // namespace qe::risk
