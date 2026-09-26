#pragma once

#include "qe/pricing/black_scholes.hpp"

#include <cstdint>

namespace qe::pricing {

inline constexpr std::int64_t kMcBlockSamples = 4096;
inline constexpr int kMcMaxSteps = 4096;

struct McConfig {
    std::int64_t paths{100'000}; ///< simulated paths; antithetic partners count as paths
    std::uint64_t seed{0};
    bool antithetic{false};
    bool control_variate{false};
    bool sobol{false};    ///< randomized QMC with digital shifts
    int replications{16}; ///< Sobol only: independent digital shifts (>= 2) for the SE
    int steps{1};         ///< time steps per path (exact GBM increments)
};

struct McResult {
    double price{};
    double std_error{};
    std::int64_t paths{};
    std::uint64_t seed{};
};

/// Monte Carlo price of a European option under GBM with the given variance-reduction options.
/// Pseudo-random: blocks of kMcBlockSamples samples, block b seeded derive_seed(seed, b).
/// Sobol: `replications` digital shifts of the same point set, SE across replications.
/// Throws InvalidArgument on invalid inputs or configuration.
[[nodiscard]] McResult mc_european(const BsParams& p, const McConfig& config);

} // namespace qe::pricing
