#pragma once

#include <cstdint>
#include <optional>
#include <string_view>

namespace qe::pricing {

enum class OptionType : std::int32_t { Call = 0, Put = 1 };

/// European option under Black-Scholes-Merton with a continuous dividend yield.
struct BsParams {
    double spot{};
    double strike{};
    double rate{};
    double dividend_yield{};
    double volatility{};
    double expiry_years{};
    OptionType type{OptionType::Call};
};

/// Analytic sensitivities. Units: vega per 1.00 of volatility, theta = -dV/dT per year,
/// rho per 1.00 of rate.
struct BsGreeks {
    double price{};
    double delta{};
    double gamma{};
    double vega{};
    double theta{};
    double rho{};
};

/// Returns a reason when the parameters are outside the model's domain, std::nullopt when valid.
/// Domain: all values finite, spot > 0, strike > 0, volatility >= 0, expiry_years >= 0.
[[nodiscard]] std::optional<std::string_view> validate(const BsParams& p) noexcept;

/// validate() plus volatility > 0 and expiry_years > 0 (Greeks are not smooth at the boundary).
[[nodiscard]] std::optional<std::string_view> validate_for_greeks(const BsParams& p) noexcept;

/// Black-Scholes-Merton price. Precondition: validate(p) == std::nullopt.
/// Zero volatility or zero expiry yields the discounted forward intrinsic value.
[[nodiscard]] double bs_price(const BsParams& p) noexcept;

/// Price and Greeks. Precondition: validate_for_greeks(p) == std::nullopt.
[[nodiscard]] BsGreeks bs_greeks(const BsParams& p) noexcept;

} // namespace qe::pricing
