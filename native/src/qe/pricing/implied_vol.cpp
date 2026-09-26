#include "qe/pricing/implied_vol.hpp"

#include "qe/core/errors.hpp"
#include "qe/core/root.hpp"

#include <algorithm>
#include <cmath>
#include <string>

namespace qe::pricing {

ImpliedVolResult implied_vol(BsParams p, double target_price) {
    p.volatility = 0.0;
    if (auto reason = validate(p)) {
        throw InvalidArgument(std::string(*reason));
    }
    if (p.expiry_years <= 0.0) {
        throw InvalidArgument("implied vol requires expiry_years > 0");
    }
    if (!std::isfinite(target_price)) {
        throw InvalidArgument("target price must be finite");
    }
    const double df_s = p.spot * std::exp(-p.dividend_yield * p.expiry_years);
    const double df_k = p.strike * std::exp(-p.rate * p.expiry_years);
    const double lower = std::max(p.type == OptionType::Call ? df_s - df_k : df_k - df_s, 0.0);
    const double upper = p.type == OptionType::Call ? df_s : df_k;
    if (!(target_price > lower && target_price < upper)) {
        throw InvalidArgument("target price is outside the no-arbitrage bounds");
    }

    auto objective = [&](double vol) {
        BsParams q = p;
        q.volatility = vol;
        return bs_price(q) - target_price;
    };
    const double lo = 1e-8;
    double hi = 10.0;
    while (objective(hi) < 0.0 && hi < 1000.0) {
        hi *= 2.0;
    }
    if (objective(lo) > 0.0) {
        // Target is below the price at the smallest volatility: indistinguishable from intrinsic.
        throw InvalidArgument("target price is too close to intrinsic value to imply a volatility");
    }
    const core::RootResult root = core::brent(objective, lo, hi, 1e-15, 300);
    if (!root.converged) {
        throw NumericError("implied vol: Brent did not converge");
    }
    return {root.x, root.iterations};
}

} // namespace qe::pricing
