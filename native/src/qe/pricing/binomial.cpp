#include "qe/pricing/binomial.hpp"

#include "qe/core/errors.hpp"

#include <algorithm>
#include <cmath>
#include <string>
#include <vector>

namespace qe::pricing {

double crr_price(const BsParams& p, int steps, Exercise exercise) {
    if (auto reason = validate(p)) {
        throw InvalidArgument(std::string(*reason));
    }
    if (p.volatility <= 0.0 || p.expiry_years <= 0.0) {
        throw InvalidArgument("lattice requires volatility > 0 and expiry_years > 0");
    }
    if (steps < 1 || steps > kMaxLatticeSteps) {
        throw InvalidArgument("steps must be in [1, 100000]");
    }
    if (exercise != Exercise::European && exercise != Exercise::American) {
        throw InvalidArgument("exercise must be EUROPEAN (0) or AMERICAN (1)");
    }

    const double dt = p.expiry_years / steps;
    const double u = std::exp(p.volatility * std::sqrt(dt));
    const double d = 1.0 / u;
    const double growth = std::exp((p.rate - p.dividend_yield) * dt);
    const double prob = (growth - d) / (u - d);
    if (!(prob > 0.0 && prob < 1.0)) {
        throw InvalidArgument("risk-neutral probability outside (0, 1): increase steps");
    }
    const double disc = std::exp(-p.rate * dt);
    const double disc_up = disc * prob;
    const double disc_down = disc * (1.0 - prob);
    const bool call = p.type == OptionType::Call;
    const bool american = exercise == Exercise::American;

    const auto n = static_cast<std::size_t>(steps);
    const double d2 = d * d;
    std::vector<double> values(n + 1);
    // Node j at step k has price S * u^(k - 2j): one pow per step, then multiply by d^2.
    double s = p.spot * std::pow(u, static_cast<double>(steps));
    for (std::size_t j = 0; j <= n; ++j, s *= d2) {
        values[j] = std::max(call ? s - p.strike : p.strike - s, 0.0);
    }
    for (std::size_t step = n; step-- > 0;) {
        s = p.spot * std::pow(u, static_cast<double>(step));
        for (std::size_t j = 0; j <= step; ++j, s *= d2) {
            double v = disc_up * values[j] + disc_down * values[j + 1];
            if (american) {
                v = std::max(v, call ? s - p.strike : p.strike - s);
            }
            values[j] = v;
        }
    }
    return values[0];
}

} // namespace qe::pricing
