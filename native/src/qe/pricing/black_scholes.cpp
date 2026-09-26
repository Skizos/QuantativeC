#include "qe/pricing/black_scholes.hpp"

#include "qe/core/math.hpp"

#include <algorithm>
#include <cmath>

namespace qe::pricing {

using core::norm_cdf;
using core::norm_pdf;

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

std::optional<std::string_view> validate_for_greeks(const BsParams& p) noexcept {
    if (auto reason = validate(p)) {
        return reason;
    }
    if (p.volatility <= 0.0 || p.expiry_years <= 0.0) {
        return "Greeks require volatility > 0 and expiry_years > 0";
    }
    return std::nullopt;
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

BsGreeks bs_greeks(const BsParams& p) noexcept {
    const double t = p.expiry_years;
    const double sqrt_t = std::sqrt(t);
    const double df_q = std::exp(-p.dividend_yield * t);
    const double df_r = std::exp(-p.rate * t);
    const double total_vol = p.volatility * sqrt_t;
    const double d1 = (std::log(p.spot * df_q / (p.strike * df_r)) / total_vol) + 0.5 * total_vol;
    const double d2 = d1 - total_vol;
    const double pdf_d1 = norm_pdf(d1);

    BsGreeks g{};
    g.price = bs_price(p);
    g.gamma = df_q * pdf_d1 / (p.spot * total_vol);
    g.vega = p.spot * df_q * pdf_d1 * sqrt_t;
    const double decay = -p.spot * df_q * pdf_d1 * p.volatility / (2.0 * sqrt_t);
    if (p.type == OptionType::Call) {
        g.delta = df_q * norm_cdf(d1);
        g.theta = decay - p.rate * p.strike * df_r * norm_cdf(d2) +
                  p.dividend_yield * p.spot * df_q * norm_cdf(d1);
        g.rho = p.strike * t * df_r * norm_cdf(d2);
    } else {
        g.delta = -df_q * norm_cdf(-d1);
        g.theta = decay + p.rate * p.strike * df_r * norm_cdf(-d2) -
                  p.dividend_yield * p.spot * df_q * norm_cdf(-d1);
        g.rho = -p.strike * t * df_r * norm_cdf(-d2);
    }
    return g;
}

} // namespace qe::pricing
