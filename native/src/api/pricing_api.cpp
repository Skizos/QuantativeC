// ABI 1.1 pricing exports: Greeks, implied volatility, CRR lattice, Monte Carlo.

#include "api/common.hpp"
#include "qe/pricing/binomial.hpp"
#include "qe/pricing/black_scholes.hpp"
#include "qe/pricing/implied_vol.hpp"
#include "qe/pricing/monte_carlo.hpp"
#include "qe_api.h"

#include <cmath>
#include <cstdint>

namespace {

using namespace qe::api;
namespace pricing = qe::pricing;

pricing::BsParams to_params(const qe_bs_input& in) noexcept {
    return {.spot = in.spot,
            .strike = in.strike,
            .rate = in.rate,
            .dividend_yield = in.dividend_yield,
            .volatility = in.volatility,
            .expiry_years = in.expiry_years,
            .type = static_cast<pricing::OptionType>(in.option_type)};
}

/// Runs one batch element, mapping qe exceptions to a per-element status.
template <typename Fn>
qe_status element_status(Fn&& fn) {
    try {
        fn();
        return QE_OK;
    } catch (const qe::InvalidArgument&) {
        return QE_E_INVALID_ARG;
    } catch (const qe::NumericError&) {
        return QE_E_NUMERIC;
    }
}

} // namespace

extern "C" {

QE_API qe_status QE_CALL qe_bs_greeks_batch(const qe_engine* engine, const qe_bs_input* inputs,
                                            qe_bs_greeks* outputs, std::int64_t count,
                                            std::int64_t* failed_count) noexcept {
    return guarded([&]() -> qe_status {
        require_batch(engine, inputs, outputs, count, failed_count);
        std::int64_t failed = 0;
        for (std::int64_t i = 0; i < count; ++i) {
            const qe_bs_input& in = inputs[i];
            qe_bs_greeks& out = outputs[i];
            const pricing::BsParams p = to_params(in);
            out = qe_bs_greeks{kNaN, kNaN, kNaN, kNaN, kNaN, kNaN, QE_E_INVALID_ARG, 0};
            if (in.reserved != 0 || pricing::validate_for_greeks(p).has_value()) {
                ++failed;
                continue;
            }
            const pricing::BsGreeks g = pricing::bs_greeks(p);
            if (!std::isfinite(g.price) || !std::isfinite(g.delta) || !std::isfinite(g.gamma) ||
                !std::isfinite(g.vega) || !std::isfinite(g.theta) || !std::isfinite(g.rho)) {
                out.status = QE_E_NUMERIC;
                ++failed;
                continue;
            }
            out = qe_bs_greeks{g.price, g.delta, g.gamma, g.vega, g.theta, g.rho, QE_OK, 0};
        }
        *failed_count = failed;
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_implied_vol_batch(const qe_engine* engine, const qe_iv_input* inputs,
                                              qe_iv_output* outputs, std::int64_t count,
                                              std::int64_t* failed_count) noexcept {
    return guarded([&]() -> qe_status {
        require_batch(engine, inputs, outputs, count, failed_count);
        std::int64_t failed = 0;
        for (std::int64_t i = 0; i < count; ++i) {
            const qe_iv_input& in = inputs[i];
            qe_iv_output& out = outputs[i];
            out = qe_iv_output{kNaN, QE_E_INVALID_ARG, 0};
            if (in.reserved != 0) {
                ++failed;
                continue;
            }
            const pricing::BsParams p{.spot = in.spot,
                                      .strike = in.strike,
                                      .rate = in.rate,
                                      .dividend_yield = in.dividend_yield,
                                      .volatility = 0.0,
                                      .expiry_years = in.expiry_years,
                                      .type = static_cast<pricing::OptionType>(in.option_type)};
            pricing::ImpliedVolResult iv{};
            out.status = element_status([&] { iv = pricing::implied_vol(p, in.price); });
            if (out.status == QE_OK) {
                out.volatility = iv.volatility;
                out.iterations = iv.iterations;
            } else {
                ++failed;
            }
        }
        *failed_count = failed;
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_lattice_batch(const qe_engine* engine, const qe_lattice_input* inputs,
                                          qe_bs_output* outputs, std::int64_t count,
                                          std::int64_t* failed_count) noexcept {
    return guarded([&]() -> qe_status {
        require_batch(engine, inputs, outputs, count, failed_count);
        std::int64_t failed = 0;
        for (std::int64_t i = 0; i < count; ++i) {
            const qe_lattice_input& in = inputs[i];
            qe_bs_output& out = outputs[i];
            out = qe_bs_output{kNaN, QE_E_INVALID_ARG, 0};
            if (in.option.reserved != 0) {
                ++failed;
                continue;
            }
            double price = kNaN;
            out.status = element_status([&] {
                price = pricing::crr_price(to_params(in.option), in.steps,
                                           static_cast<pricing::Exercise>(in.exercise));
            });
            if (out.status == QE_OK) {
                out.price = price;
            } else {
                ++failed;
            }
        }
        *failed_count = failed;
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_mc_european(const qe_engine* engine, const qe_mc_config* config,
                                        const qe_bs_input* option, qe_mc_result* out) noexcept {
    return guarded([&]() -> qe_status {
        require(out != nullptr && config != nullptr && option != nullptr,
                "config, option and out must not be NULL");
        *out = qe_mc_result{kNaN, kNaN, 0, 0};
        require_engine(engine);
        require(config->struct_size == static_cast<std::int32_t>(sizeof(qe_mc_config)),
                "config->struct_size must equal sizeof(qe_mc_config) (32)");
        constexpr std::int32_t known = QE_MC_ANTITHETIC | QE_MC_CONTROL_VARIATE | QE_MC_SOBOL;
        require((config->flags & ~known) == 0, "config->flags has unknown bits");
        require(option->reserved == 0, "option->reserved must be 0");

        pricing::McConfig c;
        c.paths = config->paths;
        c.seed = config->seed != 0 ? config->seed : engine->seed;
        c.antithetic = (config->flags & QE_MC_ANTITHETIC) != 0;
        c.control_variate = (config->flags & QE_MC_CONTROL_VARIATE) != 0;
        c.sobol = (config->flags & QE_MC_SOBOL) != 0;
        c.replications = config->replications;
        c.steps = config->steps;
        const pricing::McResult r = pricing::mc_european(to_params(*option), c);
        if (!std::isfinite(r.price) || !std::isfinite(r.std_error)) {
            throw qe::NumericError("Monte Carlo produced a non-finite estimate");
        }
        *out = qe_mc_result{r.price, r.std_error, r.paths, r.seed};
        return QE_OK;
    });
}

} // extern "C"
