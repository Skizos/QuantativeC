#include "qe/portfolio/optimizers.hpp"

#include "qe/core/errors.hpp"

#include <Eigen/Eigenvalues>
#include <algorithm>
#include <cmath>

namespace qe::portfolio {
namespace {

void validate_cov(MatrixCRef cov) {
    if (cov.rows() < 1 || cov.rows() != cov.cols()) {
        throw InvalidArgument("cov must be a non-empty square matrix");
    }
    if (!cov.allFinite()) {
        throw InvalidArgument("cov must be finite");
    }
    if (!cov.isApprox(cov.transpose(), 1e-12)) {
        throw InvalidArgument("cov must be symmetric");
    }
}

double largest_eigenvalue(MatrixCRef cov) {
    const Eigen::SelfAdjointEigenSolver<Matrix> eig(cov, Eigen::EigenvaluesOnly);
    if (eig.info() != Eigen::Success) {
        throw NumericError("eigenvalue computation failed");
    }
    const double scale = std::max(1.0, eig.eigenvalues().cwiseAbs().maxCoeff());
    if (eig.eigenvalues().minCoeff() < -1e-10 * scale) {
        throw InvalidArgument("cov must be positive semidefinite");
    }
    return eig.eigenvalues().maxCoeff();
}

} // namespace

Vector project_budget_box(VectorCRef v, VectorCRef lower, VectorCRef upper) {
    const Eigen::Index n = v.size();
    if (lower.size() != n || upper.size() != n || n < 1) {
        throw InvalidArgument("projection: dimension mismatch");
    }
    if ((lower.array() > upper.array()).any()) {
        throw InvalidArgument("lower bound exceeds upper bound");
    }
    if (lower.sum() > 1.0 + 1e-12 || upper.sum() < 1.0 - 1e-12) {
        throw InvalidArgument("bounds are infeasible for a fully invested portfolio");
    }
    auto clipped = [&](double tau) {
        return (v.array() - tau).max(lower.array()).min(upper.array()).matrix().eval();
    };
    // sum(clip(v - tau)) is non-increasing in tau: >= 1 at tau_lo, <= 1 at tau_hi.
    double tau_lo = (v - upper).minCoeff();
    double tau_hi = (v - lower).maxCoeff();
    for (int i = 0; i < 200 && tau_hi - tau_lo > 0.0; ++i) {
        const double mid = 0.5 * (tau_lo + tau_hi);
        if (mid <= tau_lo || mid >= tau_hi) {
            break;
        }
        (clipped(mid).sum() > 1.0 ? tau_lo : tau_hi) = mid;
    }
    Vector w = clipped(0.5 * (tau_lo + tau_hi));
    // Put the remaining rounding residual on a variable with room to absorb it.
    const double residual = 1.0 - w.sum();
    for (Eigen::Index i = 0; i < n; ++i) {
        const double moved = std::clamp(w(i) + residual, lower(i), upper(i));
        if (moved - w(i) == residual) {
            w(i) = moved;
            break;
        }
    }
    return w;
}

OptimizationResult mean_variance(VectorCRef mu, MatrixCRef cov, double risk_aversion,
                                 VectorCRef lower, VectorCRef upper, const SolverOptions& options) {
    validate_cov(cov);
    const Eigen::Index n = cov.rows();
    if (mu.size() != n || !mu.allFinite()) {
        throw InvalidArgument("mu must be finite with one entry per asset");
    }
    if (!(risk_aversion > 0.0) || !std::isfinite(risk_aversion)) {
        throw InvalidArgument("risk_aversion must be > 0");
    }
    if (options.max_iterations < 1 || !(options.tolerance > 0.0)) {
        throw InvalidArgument("max_iterations must be >= 1 and tolerance > 0");
    }
    const double lipschitz = std::max(risk_aversion * largest_eigenvalue(cov), 1e-300);
    auto objective = [&](const Vector& w) {
        return 0.5 * risk_aversion * w.dot(cov * w) - mu.dot(w);
    };
    auto gradient = [&](const Vector& w) { return (risk_aversion * (cov * w) - mu).eval(); };

    Vector x = project_budget_box(Vector::Constant(n, 1.0 / static_cast<double>(n)), lower, upper);
    Vector y = x;
    double t = 1.0;
    double fx = objective(x);
    OptimizationResult result;
    for (int iter = 1; iter <= options.max_iterations; ++iter) {
        Vector x_next = project_budget_box(y - gradient(y) / lipschitz, lower, upper);
        const double f_next = objective(x_next);
        if (f_next > fx) {
            // Adaptive restart (O'Donoghue & Candes 2015): drop momentum, take a plain step.
            y = x;
            t = 1.0;
            x_next = project_budget_box(x - gradient(x) / lipschitz, lower, upper);
        }
        const double change = (x_next - x).cwiseAbs().maxCoeff();
        const double t_next = 0.5 * (1.0 + std::sqrt(1.0 + 4.0 * t * t));
        y = x_next + ((t - 1.0) / t_next) * (x_next - x);
        x = std::move(x_next);
        fx = objective(x);
        t = t_next;
        result.iterations = iter;
        if (change < options.tolerance) {
            result.converged = true;
            break;
        }
    }
    result.weights = std::move(x);
    result.objective = fx;
    return result;
}

OptimizationResult min_variance(MatrixCRef cov, VectorCRef lower, VectorCRef upper,
                                const SolverOptions& options) {
    return mean_variance(Vector::Zero(cov.rows()), cov, 1.0, lower, upper, options);
}

OptimizationResult risk_parity(MatrixCRef cov, const SolverOptions& options) {
    validate_cov(cov);
    const Eigen::Index n = cov.rows();
    if ((cov.diagonal().array() <= 0.0).any()) {
        throw InvalidArgument("risk parity needs strictly positive variances");
    }
    (void)largest_eigenvalue(cov); // PSD check
    const double budget = 1.0 / static_cast<double>(n);
    Vector y = cov.diagonal().cwiseSqrt().cwiseInverse();
    Vector cov_y = cov * y;
    OptimizationResult result;
    for (int sweep = 1; sweep <= options.max_iterations; ++sweep) {
        double max_change = 0.0;
        for (Eigen::Index i = 0; i < n; ++i) {
            const double sii = cov(i, i);
            const double c = cov_y(i) - sii * y(i);
            const double updated = (-c + std::sqrt(c * c + 4.0 * sii * budget)) / (2.0 * sii);
            const double delta = updated - y(i);
            if (delta != 0.0) {
                cov_y += cov.col(i) * delta;
                y(i) = updated;
            }
            max_change =
                std::max(max_change, std::abs(delta) / std::max(std::abs(updated), 1e-300));
        }
        result.iterations = sweep;
        if (max_change < options.tolerance) {
            result.converged = true;
            break;
        }
    }
    result.weights = y / y.sum();
    const Vector marginal = cov * result.weights;
    const double variance = result.weights.dot(marginal);
    const Vector contributions = result.weights.cwiseProduct(marginal) / variance;
    result.objective = ((contributions.array() - budget).abs() / budget).maxCoeff();
    return result;
}

} // namespace qe::portfolio
