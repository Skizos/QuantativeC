#include "qe/risk/covariance.hpp"

#include "qe/core/errors.hpp"

#include <Eigen/Cholesky>
#include <algorithm>
#include <cmath>

namespace qe::risk {
namespace {

void validate_returns(MatrixCRef returns) {
    if (returns.rows() < 2 || returns.cols() < 1) {
        throw InvalidArgument("returns must have at least 2 observations and 1 instrument");
    }
    if (!returns.allFinite()) {
        throw InvalidArgument("returns must be finite");
    }
}

Matrix demeaned(MatrixCRef returns) {
    const Eigen::RowVectorXd mean = returns.colwise().mean();
    return returns.rowwise() - mean;
}

} // namespace

Matrix sample_covariance(MatrixCRef returns) {
    validate_returns(returns);
    const Matrix x = demeaned(returns);
    return (x.transpose() * x) / static_cast<double>(returns.rows() - 1);
}

Matrix ewma_covariance(MatrixCRef returns, double lambda) {
    validate_returns(returns);
    if (!(lambda > 0.0 && lambda <= 1.0)) {
        throw InvalidArgument("EWMA lambda must be in (0, 1]");
    }
    const Eigen::Index t = returns.rows();
    Vector w(t);
    double weight = 1.0;
    for (Eigen::Index i = t - 1; i >= 0; --i) {
        w(i) = weight;
        weight *= lambda;
    }
    w /= w.sum();
    const Eigen::RowVectorXd mean = w.transpose() * returns;
    const Matrix x = returns.rowwise() - mean;
    return x.transpose() * w.asDiagonal() * x;
}

CovarianceResult ledoit_wolf(MatrixCRef returns) {
    validate_returns(returns);
    const Matrix x = demeaned(returns);
    const auto n_samples = static_cast<double>(x.rows());
    const auto n_features = static_cast<double>(x.cols());

    const Matrix emp_cov = (x.transpose() * x) / n_samples;
    const Matrix x2 = x.array().square().matrix();
    const Vector emp_cov_trace = x2.colwise().sum().transpose() / n_samples;
    const double mu = emp_cov_trace.sum() / n_features;

    // Same algebra as sklearn.covariance.ledoit_wolf_shrinkage (single block).
    const double beta_ = (x2.transpose() * x2).sum();
    const double delta_ = (x.transpose() * x).array().square().sum() / (n_samples * n_samples);
    double beta = (beta_ / n_samples - delta_) / (n_features * n_samples);
    double delta = (delta_ - 2.0 * mu * emp_cov_trace.sum() + n_features * mu * mu) / n_features;
    beta = std::min(beta, delta);
    const double shrinkage = beta == 0.0 ? 0.0 : beta / delta;

    CovarianceResult result;
    result.shrinkage = shrinkage;
    result.covariance = (1.0 - shrinkage) * emp_cov;
    result.covariance.diagonal().array() += shrinkage * mu;
    return result;
}

CovarianceResult covariance(MatrixCRef returns, CovarianceMethod method, double lambda) {
    switch (method) {
    case CovarianceMethod::Sample:
        return {sample_covariance(returns), 0.0};
    case CovarianceMethod::Ewma:
        return {ewma_covariance(returns, lambda), 0.0};
    case CovarianceMethod::LedoitWolf:
        return ledoit_wolf(returns);
    }
    throw InvalidArgument("unknown covariance method");
}

bool is_positive_definite(MatrixCRef matrix) {
    if (matrix.rows() != matrix.cols() || matrix.rows() == 0 || !matrix.allFinite()) {
        return false;
    }
    const Eigen::LLT<Matrix> llt(matrix);
    return llt.info() == Eigen::Success;
}

} // namespace qe::risk
