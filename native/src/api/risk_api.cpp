// ABI 1.1 risk exports: covariance, VaR/ES, betas, stress.

#include "api/common.hpp"
#include "qe/risk/covariance.hpp"
#include "qe/risk/stress.hpp"
#include "qe/risk/var_es.hpp"
#include "qe_api.h"

#include <cstdint>
#include <span>

namespace {

using namespace qe::api;
namespace risk = qe::risk;

qe_var_es to_c(const risk::VarEs& v) noexcept {
    return {v.var, v.es, v.observations, v.seed};
}

qe::Vector mean_or_zero(const double* mean, std::int64_t assets) {
    return mean != nullptr ? qe::Vector(ConstVectorMap(mean, assets)) : qe::Vector::Zero(assets);
}

} // namespace

extern "C" {

QE_API qe_status QE_CALL qe_covariance(const qe_engine* engine, const qe_cov_config* config,
                                       const double* returns, std::int64_t observations,
                                       std::int64_t assets, double* out_cov,
                                       double* out_shrinkage) noexcept {
    return guarded([&]() -> qe_status {
        require_engine(engine);
        require(config != nullptr && returns != nullptr && out_cov != nullptr,
                "config, returns and out_cov must not be NULL");
        require(config->struct_size == static_cast<std::int32_t>(sizeof(qe_cov_config)),
                "config->struct_size must equal sizeof(qe_cov_config) (16)");
        require(config->method >= QE_COV_SAMPLE && config->method <= QE_COV_LEDOIT_WOLF,
                "config->method must be QE_COV_SAMPLE, QE_COV_EWMA or QE_COV_LEDOIT_WOLF");
        require_dims(observations, assets,
                     "observations and assets must be >= 1 and not too large");
        require_dims(assets, assets, "assets too large");
        const qe::Matrix x = ConstRowMajorMap(returns, observations, assets);
        const risk::CovarianceResult r = risk::covariance(
            x, static_cast<risk::CovarianceMethod>(config->method), config->ewma_lambda);
        RowMajorMap(out_cov, assets, assets) = r.covariance;
        if (out_shrinkage != nullptr) {
            *out_shrinkage = r.shrinkage;
        }
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_var_es_historical(const double* returns, std::int64_t count,
                                              double confidence, qe_var_es* out) noexcept {
    return guarded([&]() -> qe_status {
        require(out != nullptr && returns != nullptr, "returns and out must not be NULL");
        require(count >= 1 && count <= kMaxElements, "count must be in [1, 1e9]");
        *out = to_c(risk::historical_var_es(
            std::span<const double>(returns, static_cast<std::size_t>(count)), confidence));
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_var_es_parametric(const double* weights, const double* mean,
                                              const double* cov, std::int64_t assets,
                                              double confidence, qe_var_es* out) noexcept {
    return guarded([&]() -> qe_status {
        require(out != nullptr && weights != nullptr && cov != nullptr,
                "weights, cov and out must not be NULL");
        require_dims(assets, assets, "assets must be >= 1 and not too large");
        const qe::Matrix c = ConstRowMajorMap(cov, assets, assets);
        *out = to_c(risk::parametric_var_es(ConstVectorMap(weights, assets),
                                            mean_or_zero(mean, assets), c, confidence));
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_var_es_monte_carlo(const qe_engine* engine, const double* weights,
                                               const double* mean, const double* cov,
                                               std::int64_t assets, std::int64_t paths,
                                               std::uint64_t seed, double confidence,
                                               qe_var_es* out) noexcept {
    return guarded([&]() -> qe_status {
        require(out != nullptr && weights != nullptr && cov != nullptr,
                "weights, cov and out must not be NULL");
        require_engine(engine);
        require_dims(assets, assets, "assets must be >= 1 and not too large");
        const qe::Matrix c = ConstRowMajorMap(cov, assets, assets);
        const std::uint64_t used = seed != 0 ? seed : engine->seed;
        *out =
            to_c(risk::monte_carlo_var_es(ConstVectorMap(weights, assets),
                                          mean_or_zero(mean, assets), c, paths, used, confidence));
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_betas(const double* returns, const double* index,
                                  std::int64_t observations, std::int64_t assets,
                                  double* out_betas) noexcept {
    return guarded([&]() -> qe_status {
        require(returns != nullptr && index != nullptr && out_betas != nullptr,
                "returns, index and out_betas must not be NULL");
        require_dims(observations, assets,
                     "observations and assets must be >= 1 and not too large");
        const qe::Matrix x = ConstRowMajorMap(returns, observations, assets);
        const qe::Vector b =
            risk::betas(x, std::span<const double>(index, static_cast<std::size_t>(observations)));
        Eigen::Map<Eigen::VectorXd>(out_betas, assets) = b;
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_stress_pnl(const double* values, const double* shocks,
                                       std::int64_t assets, std::int64_t scenarios,
                                       double* out_pnl) noexcept {
    return guarded([&]() -> qe_status {
        require(values != nullptr && shocks != nullptr && out_pnl != nullptr,
                "values, shocks and out_pnl must not be NULL");
        require_dims(scenarios, assets, "assets and scenarios must be >= 1 and not too large");
        const qe::Matrix s = ConstRowMajorMap(shocks, scenarios, assets);
        Eigen::Map<Eigen::VectorXd>(out_pnl, scenarios) =
            risk::stress_pnl(ConstVectorMap(values, assets), s);
        return QE_OK;
    });
}

} // extern "C"
