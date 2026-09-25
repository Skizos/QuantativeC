#include "qe/risk/var_es.hpp"

#include "qe/core/errors.hpp"
#include "qe/core/math.hpp"
#include "qe/core/rng.hpp"
#include "qe/core/stats.hpp"

#include <Eigen/Eigenvalues>
#include <algorithm>
#include <cmath>
#include <vector>

namespace qe::risk {
namespace {

void validate_confidence(double confidence) {
    if (!(confidence > 0.5 && confidence < 1.0)) {
        throw InvalidArgument("confidence must be in (0.5, 1)");
    }
}

void validate_portfolio(VectorCRef weights, VectorCRef mean, MatrixCRef cov) {
    const Eigen::Index n = weights.size();
    if (n < 1 || mean.size() != n || cov.rows() != n || cov.cols() != n) {
        throw InvalidArgument("weights, mean and cov dimensions must agree");
    }
    if (!weights.allFinite() || !mean.allFinite() || !cov.allFinite()) {
        throw InvalidArgument("weights, mean and cov must be finite");
    }
    if (!cov.isApprox(cov.transpose(), 1e-12)) {
        throw InvalidArgument("cov must be symmetric");
    }
}

VarEs tail_of_sorted_losses(std::vector<double>& losses, double confidence) {
    std::sort(losses.begin(), losses.end());
    const auto n = losses.size();
    // k = ceil(confidence * n), guarded against 95.00000000000001-style rounding.
    auto k = static_cast<std::size_t>(std::ceil(confidence * static_cast<double>(n) - 1e-9));
    k = std::clamp<std::size_t>(k, 1, n);
    core::NeumaierSum tail;
    for (std::size_t i = k - 1; i < n; ++i) {
        tail.add(losses[i]);
    }
    return {losses[k - 1], tail.value() / static_cast<double>(n - k + 1),
            static_cast<std::int64_t>(n), 0};
}

} // namespace

VarEs historical_var_es(std::span<const double> returns, double confidence) {
    validate_confidence(confidence);
    if (returns.empty()) {
        throw InvalidArgument("returns must not be empty");
    }
    std::vector<double> losses;
    losses.reserve(returns.size());
    for (const double r : returns) {
        if (!std::isfinite(r)) {
            throw InvalidArgument("returns must be finite");
        }
        losses.push_back(-r);
    }
    return tail_of_sorted_losses(losses, confidence);
}

VarEs parametric_var_es(VectorCRef weights, VectorCRef mean, MatrixCRef cov, double confidence) {
    validate_confidence(confidence);
    validate_portfolio(weights, mean, cov);
    const double variance = weights.dot(cov * weights);
    if (variance < -1e-12 * std::max(1.0, cov.diagonal().cwiseAbs().maxCoeff())) {
        throw NumericError("portfolio variance is negative: cov is not positive semidefinite");
    }
    const double sigma = std::sqrt(std::max(variance, 0.0));
    const double mu = weights.dot(mean);
    const double z = core::norm_inv(confidence);
    return {-mu + sigma * z, -mu + sigma * core::norm_pdf(z) / (1.0 - confidence), 0, 0};
}

VarEs monte_carlo_var_es(VectorCRef weights, VectorCRef mean, MatrixCRef cov, std::int64_t paths,
                         std::uint64_t seed, double confidence) {
    validate_confidence(confidence);
    validate_portfolio(weights, mean, cov);
    if (paths < 100 || paths > 100'000'000) {
        throw InvalidArgument("paths must be in [100, 1e8]");
    }
    // Symmetric square root A with A A' = cov; works for singular (PSD) matrices.
    const Eigen::SelfAdjointEigenSolver<Matrix> eig(cov);
    if (eig.info() != Eigen::Success) {
        throw NumericError("eigendecomposition of cov failed");
    }
    const double scale = std::max(1.0, eig.eigenvalues().cwiseAbs().maxCoeff());
    if (eig.eigenvalues().minCoeff() < -1e-10 * scale) {
        throw NumericError("cov is not positive semidefinite");
    }
    const Vector root_eigen = eig.eigenvalues().cwiseMax(0.0).cwiseSqrt();
    // Portfolio return = w'mean + (A' w)' z, so only the vector b = A' w is needed.
    const Matrix a = eig.eigenvectors() * root_eigen.asDiagonal();
    const Vector b = a.transpose() * weights;
    const double mu = weights.dot(mean);

    std::vector<double> losses(static_cast<std::size_t>(paths));
    constexpr std::int64_t kBlock = 4096;
    const auto dims = static_cast<std::size_t>(b.size());
    for (std::int64_t block = 0, done = 0; done < paths; ++block) {
        core::Rng rng(core::derive_seed(seed, static_cast<std::uint64_t>(block)));
        const std::int64_t n = std::min(kBlock, paths - done);
        for (std::int64_t i = 0; i < n; ++i) {
            double r = mu;
            for (std::size_t k = 0; k < dims; ++k) {
                r += b(static_cast<Eigen::Index>(k)) * rng.normal();
            }
            losses[static_cast<std::size_t>(done + i)] = -r;
        }
        done += n;
    }
    VarEs out = tail_of_sorted_losses(losses, confidence);
    out.seed = seed;
    return out;
}

} // namespace qe::risk
