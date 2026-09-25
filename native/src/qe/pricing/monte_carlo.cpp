#include "qe/pricing/monte_carlo.hpp"

#include "qe/core/errors.hpp"
#include "qe/core/math.hpp"
#include "qe/core/rng.hpp"
#include "qe/core/sobol.hpp"
#include "qe/core/stats.hpp"

#include <algorithm>
#include <cmath>
#include <span>
#include <string>
#include <vector>

namespace qe::pricing {
namespace {

struct PathModel {
    double log_spot;
    double drift_dt;
    double vol_sqrt_dt;
    double discount;
    double strike;
    bool call;
    double control_mean; // E[discount * S_T] = S * exp(-q T)
};

// Discounted payoff and control (discounted terminal price) for one path; sign = +1 or -1.
struct Sample {
    double payoff;
    double control;
};

Sample simulate(const PathModel& m, std::span<const double> z, double sign) noexcept {
    double log_s = m.log_spot;
    for (const double zk : z) {
        log_s += m.drift_dt + m.vol_sqrt_dt * sign * zk;
    }
    const double s_t = std::exp(log_s);
    const double intrinsic = m.call ? s_t - m.strike : m.strike - s_t;
    return {m.discount * std::max(intrinsic, 0.0), m.discount * s_t};
}

Sample sample(const PathModel& m, std::span<const double> z, bool antithetic) noexcept {
    const Sample a = simulate(m, z, 1.0);
    if (!antithetic) {
        return a;
    }
    const Sample b = simulate(m, z, -1.0);
    return {0.5 * (a.payoff + b.payoff), 0.5 * (a.control + b.control)};
}

struct Estimate {
    double value;
    double variance_of_mean;
};

Estimate estimate(const core::Welford2& w, bool control_variate, double control_mean) {
    const auto n = static_cast<double>(w.n);
    if (!control_variate || w.m2_x <= 0.0 || w.n < 3) {
        return {w.mean_y, w.n < 2 ? 0.0 : (w.m2_y / (n - 1.0)) / n};
    }
    const double beta = w.c_xy / w.m2_x;
    const double value = w.mean_y - beta * (w.mean_x - control_mean);
    const double residual = std::max(w.m2_y - beta * w.c_xy, 0.0) / (n - 2.0);
    return {value, residual / n};
}

void validate_config(const McConfig& c) {
    if (c.steps < 1 || c.steps > kMcMaxSteps) {
        throw InvalidArgument("steps must be in [1, 4096]");
    }
    const std::int64_t per_sample = c.antithetic ? 2 : 1;
    if (c.paths < 2 * per_sample || c.paths % per_sample != 0) {
        throw InvalidArgument("paths must be >= 2 samples (and even with antithetic)");
    }
    if (c.paths > std::int64_t{1'000'000'000'000}) {
        throw InvalidArgument("paths must be <= 1e12");
    }
    if (c.sobol) {
        if (c.steps > core::Sobol::max_dims()) {
            throw InvalidArgument("Sobol supports at most " +
                                  std::to_string(core::Sobol::max_dims()) + " steps");
        }
        if (c.replications < 2) {
            throw InvalidArgument("Sobol needs replications >= 2 for a standard error");
        }
        const std::int64_t samples = c.paths / per_sample;
        if (samples % c.replications != 0 || samples / c.replications < 2) {
            throw InvalidArgument("with Sobol, paths / (antithetic ? 2 : 1) must be a multiple of "
                                  "replications with >= 2 points each");
        }
        if (samples / c.replications > (std::int64_t{1} << 32)) {
            throw InvalidArgument("Sobol supports at most 2^32 points per replication");
        }
    }
}

} // namespace

McResult mc_european(const BsParams& p, const McConfig& config) {
    if (auto reason = validate(p)) {
        throw InvalidArgument(std::string(*reason));
    }
    validate_config(config);

    const double dt = p.expiry_years / config.steps;
    const PathModel model{
        .log_spot = std::log(p.spot),
        .drift_dt = (p.rate - p.dividend_yield - 0.5 * p.volatility * p.volatility) * dt,
        .vol_sqrt_dt = p.volatility * std::sqrt(dt),
        .discount = std::exp(-p.rate * p.expiry_years),
        .strike = p.strike,
        .call = p.type == OptionType::Call,
        .control_mean = p.spot * std::exp(-p.dividend_yield * p.expiry_years),
    };
    const auto steps = static_cast<std::size_t>(config.steps);
    const std::int64_t samples = config.paths / (config.antithetic ? 2 : 1);
    std::vector<double> z(steps);

    McResult result{};
    result.paths = config.paths;
    result.seed = config.seed;

    if (!config.sobol) {
        core::Welford2 total;
        for (std::int64_t block = 0, done = 0; done < samples; ++block) {
            core::Rng rng(core::derive_seed(config.seed, static_cast<std::uint64_t>(block)));
            core::Welford2 acc;
            const std::int64_t n = std::min(kMcBlockSamples, samples - done);
            for (std::int64_t i = 0; i < n; ++i) {
                for (double& zk : z) {
                    zk = rng.normal();
                }
                const Sample s = sample(model, z, config.antithetic);
                acc.add(s.control, s.payoff);
            }
            total.merge(acc);
            done += n;
        }
        const Estimate e = estimate(total, config.control_variate, model.control_mean);
        result.price = e.value;
        result.std_error = std::sqrt(e.variance_of_mean);
        return result;
    }

    // Randomized QMC: each replication applies an independent random digital shift.
    const std::int64_t points = samples / config.replications;
    std::vector<std::uint32_t> point(steps);
    std::vector<std::uint32_t> shift(steps);
    core::Welford replications;
    for (int r = 0; r < config.replications; ++r) {
        core::Rng rng(core::derive_seed(config.seed, static_cast<std::uint64_t>(r)));
        for (auto& s : shift) {
            s = static_cast<std::uint32_t>(rng.next_u64() >> 32U);
        }
        core::Sobol sobol(config.steps);
        core::Welford2 acc;
        for (std::int64_t i = 0; i < points; ++i) {
            sobol.next(point);
            for (std::size_t k = 0; k < steps; ++k) {
                z[k] = core::norm_inv(core::Sobol::to_open_unit(point[k] ^ shift[k]));
            }
            const Sample s = sample(model, z, config.antithetic);
            acc.add(s.control, s.payoff);
        }
        replications.add(estimate(acc, config.control_variate, model.control_mean).value);
    }
    result.price = replications.mean;
    result.std_error = replications.std_error();
    return result;
}

} // namespace qe::pricing
