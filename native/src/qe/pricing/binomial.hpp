#pragma once

#include "qe/pricing/black_scholes.hpp"

#include <cstdint>

namespace qe::pricing {

enum class Exercise : std::int32_t { European = 0, American = 1 };

inline constexpr int kMaxLatticeSteps = 100'000;

/// Cox-Ross-Rubinstein binomial price with continuous dividend yield. O(steps) memory.
/// Throws InvalidArgument unless inputs are valid, volatility > 0, expiry > 0,
/// 1 <= steps <= kMaxLatticeSteps and the risk-neutral probability lies in (0, 1).
[[nodiscard]] double crr_price(const BsParams& p, int steps, Exercise exercise);

} // namespace qe::pricing
