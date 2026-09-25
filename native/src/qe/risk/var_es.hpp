#pragma once

#include "qe/core/linalg.hpp"

#include <cstdint>
#include <span>

namespace qe::risk {

/// Value-at-Risk and Expected Shortfall as positive loss fractions (0.03 = 3 % loss).
struct VarEs {
    double var{};
    double es{};
    std::int64_t observations{}; ///< sample size (historical) or simulated paths (Monte Carlo)
    std::uint64_t seed{};        ///< seed used (Monte Carlo only)
};

/// Historical simulation on fractional returns. Losses l = -r sorted ascending;
/// VaR = l_(k) with k = ceil(confidence * n) (1-based, no interpolation); ES = mean(l_(k..n)).
/// confidence in (0.5, 1); returns finite and non-empty.
[[nodiscard]] VarEs historical_var_es(std::span<const double> returns, double confidence);

/// Normal (variance-covariance) VaR/ES of a portfolio with weights w, mean returns mu, covariance.
/// VaR = -w'mu + sigma_p * z_a;  ES = -w'mu + sigma_p * phi(z_a) / (1 - a).
[[nodiscard]] VarEs parametric_var_es(VectorCRef weights, VectorCRef mean, MatrixCRef cov,
                                      double confidence);

/// Monte Carlo: r ~ N(mean, cov) via a symmetric square root of cov (PSD allowed), portfolio
/// return w'r, then the historical estimator. Blocks of 4096 paths, block b seeded
/// derive_seed(seed, b). 100 <= paths <= 1e8.
[[nodiscard]] VarEs monte_carlo_var_es(VectorCRef weights, VectorCRef mean, MatrixCRef cov,
                                       std::int64_t paths, std::uint64_t seed, double confidence);

} // namespace qe::risk
