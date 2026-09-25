#include "qe/pricing/black_scholes.hpp"

#include <algorithm>
#include <cmath>
#include <numbers>

namespace qe::pricing {

std::optional<std::string_view> validate(const BsParams& p) noexcept {
    if (!std::isfinite(p.spot) || !std::isfinite(p.strike) || !std::isfinite(p.rate) ||
        !std::isfinite(p.dividend_yield) || !std::isfinite(p.volatility) ||
        !std::isfinite(p.expiry_years)) {
        return "all inputs must be finite";
    }
    if (p.spot <= 0.0) {
        return "spot must be > 0";
    }
    if (p.strike <= 0.0) {
        return "strike must be > 0";
    }
    if (p.volatility < 0.0) {
        return "volatility must be >= 0";
    }
    if (p.expiry_years < 0.0) {
        return "expiry_years must be >= 0";
    }
    if (p.type != OptionType::Call && p.type != OptionType::Put) {
        return "option_type must be CALL (0) or PUT (1)";
    }
    return std::nullopt;
}

double norm_cdf(double x) noexcept {
    // erfc keeps full relative precision in the lower tail, unlike 1 + erf.
    return 0.5 * std::erfc(-x / std::numbers::sqrt2);
}

double bs_price(const BsParams& p) noexcept {
    const double discounted_spot = p.spot * std::exp(-p.dividend_yield * p.expiry_years);
    const double discounted_strike = p.strike * std::exp(-p.rate * p.expiry_years);
    const double total_vol = p.volatility * std::sqrt(p.expiry_years);

    if (total_vol == 0.0) {
        const double forward_intrinsic = p.type == OptionType::Call
                                             ? discounted_spot - discounted_strike
                                             : discounted_strike - discounted_spot;
        return std::max(forward_intrinsic, 0.0);
    }

    const double d1 = (std::log(discounted_spot / discounted_strike) / total_vol) + 0.5 * total_vol;
    const double d2 = d1 - total_vol;

    if (p.type == OptionType::Call) {
        return discounted_spot * norm_cdf(d1) - discounted_strike * norm_cdf(d2);
    }
    return discounted_strike * norm_cdf(-d2) - discounted_spot * norm_cdf(-d1);
}

} // namespace qe::pricing
