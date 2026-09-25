// ABI 1.1 portfolio exports: optimizers and the integer-lot rebalance solver.

#include "api/common.hpp"
#include "qe/portfolio/hrp.hpp"
#include "qe/portfolio/optimizers.hpp"
#include "qe/portfolio/rebalance.hpp"
#include "qe_api.h"

#include <cstdint>
#include <vector>

namespace {

using namespace qe::api;
namespace portfolio = qe::portfolio;

} // namespace

extern "C" {

QE_API qe_status QE_CALL qe_optimize(const qe_engine* engine, const qe_opt_config* config,
                                     const double* mu, const double* cov, const double* lower,
                                     const double* upper, std::int64_t assets, double* out_weights,
                                     qe_opt_result* out_result) noexcept {
    return guarded([&]() -> qe_status {
        require_engine(engine);
        require(config != nullptr && cov != nullptr && out_weights != nullptr &&
                    out_result != nullptr,
                "config, cov, out_weights and out_result must not be NULL");
        require(config->struct_size == static_cast<std::int32_t>(sizeof(qe_opt_config)),
                "config->struct_size must equal sizeof(qe_opt_config) (32)");
        require_dims(assets, assets, "assets must be >= 1 and not too large");
        *out_result = qe_opt_result{kNaN, 0, 0};

        portfolio::SolverOptions options;
        if (config->max_iterations != 0) {
            options.max_iterations = config->max_iterations;
        }
        if (config->tolerance != 0.0) {
            options.tolerance = config->tolerance;
        }
        const qe::Matrix c = ConstRowMajorMap(cov, assets, assets);
        auto bound = [&](const double* b, double fallback) {
            return b != nullptr ? qe::Vector(ConstVectorMap(b, assets))
                                : qe::Vector::Constant(assets, fallback);
        };

        portfolio::OptimizationResult r;
        switch (config->method) {
        case QE_OPT_MIN_VARIANCE:
            r = portfolio::min_variance(c, bound(lower, 0.0), bound(upper, 1.0), options);
            break;
        case QE_OPT_MEAN_VARIANCE:
            require(mu != nullptr, "mu must not be NULL for QE_OPT_MEAN_VARIANCE");
            r = portfolio::mean_variance(ConstVectorMap(mu, assets), c, config->risk_aversion,
                                         bound(lower, 0.0), bound(upper, 1.0), options);
            break;
        case QE_OPT_RISK_PARITY:
            require(lower == nullptr && upper == nullptr, "risk parity does not accept bounds");
            r = portfolio::risk_parity(c, options);
            break;
        case QE_OPT_HRP: {
            require(lower == nullptr && upper == nullptr, "HRP does not accept bounds");
            const portfolio::HrpResult h = portfolio::hrp(c);
            r.weights = h.weights;
            r.objective = h.weights.dot(c * h.weights);
            r.iterations = 0;
            r.converged = true;
            break;
        }
        default:
            throw qe::InvalidArgument("config->method must be one of QE_OPT_*");
        }
        Eigen::Map<Eigen::VectorXd>(out_weights, assets) = r.weights;
        *out_result = qe_opt_result{r.objective, r.iterations, r.converged ? 1 : 0};
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_rebalance(const qe_rebalance_config* config,
                                      const qe_rebalance_asset* assets, std::int64_t count,
                                      qe_rebalance_trade* out_trades,
                                      qe_rebalance_summary* out_summary) noexcept {
    return guarded([&]() -> qe_status {
        require(config != nullptr && assets != nullptr && out_trades != nullptr &&
                    out_summary != nullptr,
                "config, assets, out_trades and out_summary must not be NULL");
        require(config->struct_size == static_cast<std::int32_t>(sizeof(qe_rebalance_config)),
                "config->struct_size must equal sizeof(qe_rebalance_config) (48)");
        require(count >= 1 && count <= 100'000, "count must be in [1, 100000]");

        std::vector<portfolio::RebalanceAsset> in(static_cast<std::size_t>(count));
        for (std::size_t i = 0; i < in.size(); ++i) {
            const qe_rebalance_asset& a = assets[i];
            in[i] = {a.price, a.target_weight, a.current_quantity, a.lot_size};
        }
        const portfolio::RebalanceConfig cfg{.cash = config->cash,
                                             .cash_buffer = config->cash_buffer,
                                             .min_trade_value = config->min_trade_value,
                                             .fee_min = config->fee_min,
                                             .fee_rate = config->fee_rate};
        const portfolio::RebalanceResult r = portfolio::rebalance(in, cfg);
        for (std::size_t i = 0; i < in.size(); ++i) {
            const auto& t = r.trades[i];
            out_trades[i] = {t.target_quantity, t.trade_quantity, t.trade_value, t.fee,
                             t.final_weight};
        }
        const auto& s = r.summary;
        *out_summary = {s.portfolio_value, s.cash_after, s.total_fees,
                        s.tracking_error,  s.trades,     s.feasible ? 1 : 0};
        return QE_OK;
    });
}

} // extern "C"
