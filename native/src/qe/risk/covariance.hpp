#pragma once

#include "qe/core/linalg.hpp"

namespace qe::risk {

enum class CovarianceMethod : int { Sample = 0, Ewma = 1, LedoitWolf = 2 };

struct CovarianceResult {
    Matrix covariance;
    double shrinkage{0.0}; ///< Ledoit-Wolf intensity in [0, 1]; 0 for other methods
};

/// Unbiased (T - 1) sample covariance of a T x N return matrix (rows = observations). T >= 2.
[[nodiscard]] Matrix sample_covariance(MatrixCRef returns);

/// Exponentially weighted covariance: weights proportional to lambda^(T-1-t), normalized to sum 1,
/// weighted mean removed. lambda in (0, 1]; lambda = 1 equals the biased (1/T) sample covariance.
[[nodiscard]] Matrix ewma_covariance(MatrixCRef returns, double lambda);

/// Ledoit & Wolf (2004) shrinkage toward mu * I, identical to scikit-learn's
/// LedoitWolf(assume_centered=False) (empirical covariance with 1/T). T >= 2.
[[nodiscard]] CovarianceResult ledoit_wolf(MatrixCRef returns);

/// Dispatches on method (lambda is used by EWMA only).
[[nodiscard]] CovarianceResult covariance(MatrixCRef returns, CovarianceMethod method,
                                          double lambda);

/// True when the symmetric matrix admits a Cholesky factorization (strictly positive definite).
[[nodiscard]] bool is_positive_definite(MatrixCRef matrix);

} // namespace qe::risk
