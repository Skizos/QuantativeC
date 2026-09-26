#pragma once

#include "qe/pricing/black_scholes.hpp"

namespace qe::pricing {

struct ImpliedVolResult {
    double volatility{};
    int iterations{};
};

/// Black-Scholes implied volatility by Brent's method on [1e-8, 10] (upper end expanded up to
/// 1000). p.volatility is ignored. Throws InvalidArgument when inputs are invalid or the target
/// price is not strictly inside the no-arbitrage bounds (lower: discounted forward intrinsic;
/// upper: discounted spot for calls, discounted strike for puts); throws NumericError if Brent does
/// not converge.
[[nodiscard]] ImpliedVolResult implied_vol(BsParams p, double target_price);

} // namespace qe::pricing
