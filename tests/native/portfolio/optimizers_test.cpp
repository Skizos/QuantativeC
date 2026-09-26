#include "qe/core/errors.hpp"
#include "qe/portfolio/hrp.hpp"
#include "qe/portfolio/optimizers.hpp"
#include "qe/risk/covariance.hpp"
#include "reference/phase2_reference.inc"

#include <Eigen/Cholesky>
#include <cmath>
#include <gtest/gtest.h>
#include <vector>

namespace {

using namespace qe;
using namespace qe::portfolio;
namespace ref = qe::testref;

Matrix hrp_cov() {
    return Eigen::Map<const Eigen::Matrix<double, Eigen::Dynamic, Eigen::Dynamic, Eigen::RowMajor>>(
        ref::kHrpCov, ref::kHrpN, ref::kHrpN);
}

Matrix small_cov() {
    Matrix c(4, 4);
    c << 0.040, 0.006, 0.004, 0.002, //
        0.006, 0.025, 0.005, 0.001,  //
        0.004, 0.005, 0.090, 0.012,  //
        0.002, 0.001, 0.012, 0.010;
    return c;
}

// KKT for min 0.5*l*w'Cw - mu'w s.t. sum w = 1, lo <= w <= hi:
// free coordinates share one gradient value g*, lower-bound ones have g >= g*, upper ones g <= g*.
void expect_kkt(const Vector& w, const Vector& grad, const Vector& lo, const Vector& hi,
                double tol) {
    std::vector<double> free_grads;
    for (Eigen::Index i = 0; i < w.size(); ++i) {
        if (w(i) > lo(i) + 1e-9 && w(i) < hi(i) - 1e-9) {
            free_grads.push_back(grad(i));
        }
    }
    ASSERT_FALSE(free_grads.empty());
    const double g_star = free_grads.front();
    for (const double g : free_grads) {
        EXPECT_NEAR(g, g_star, tol);
    }
    for (Eigen::Index i = 0; i < w.size(); ++i) {
        if (w(i) <= lo(i) + 1e-9) {
            EXPECT_GE(grad(i), g_star - tol) << i;
        }
        if (w(i) >= hi(i) - 1e-9) {
            EXPECT_LE(grad(i), g_star + tol) << i;
        }
    }
}

TEST(Projection, LandsOnBudgetAndBox) {
    Vector v(4);
    v << 0.9, -0.3, 0.6, 0.2;
    const Vector lo = Vector::Zero(4);
    const Vector hi = Vector::Constant(4, 0.5);
    const Vector w = project_budget_box(v, lo, hi);
    EXPECT_NEAR(w.sum(), 1.0, 1e-15);
    EXPECT_TRUE((w.array() >= lo.array()).all() && (w.array() <= hi.array()).all());
    EXPECT_THROW((void)project_budget_box(v, lo, Vector::Constant(4, 0.2)),
                 InvalidArgument); // sum(hi) < 1
}

TEST(MinVariance, MatchesClosedFormWhenBoundsAreInactive) {
    const Matrix c = small_cov();
    const Vector ones = Vector::Ones(4);
    const Vector inv = c.llt().solve(ones);
    const Vector expected = inv / ones.dot(inv);
    const auto r = min_variance(c, Vector::Constant(4, -10.0), Vector::Constant(4, 10.0));
    ASSERT_TRUE(r.converged);
    EXPECT_TRUE(r.weights.isApprox(expected, 1e-9)) << r.weights.transpose() << "\n"
                                                    << expected.transpose();
}

// <verification_requirements>: optimizer weights sum to 1 and respect bounds.
TEST(MeanVariance, RespectsBudgetBoundsAndKkt) {
    const Matrix c = small_cov();
    Vector mu(4);
    mu << 0.08, 0.05, 0.12, 0.03;
    const Vector lo = Vector::Constant(4, 0.05);
    const Vector hi = Vector::Constant(4, 0.40);
    for (const double lambda : {1.0, 3.0, 10.0}) {
        const auto r = mean_variance(mu, c, lambda, lo, hi);
        ASSERT_TRUE(r.converged) << lambda;
        EXPECT_NEAR(r.weights.sum(), 1.0, 1e-12);
        EXPECT_TRUE((r.weights.array() >= lo.array() - 1e-15).all());
        EXPECT_TRUE((r.weights.array() <= hi.array() + 1e-15).all());
        expect_kkt(r.weights, lambda * (c * r.weights) - mu, lo, hi, 1e-8);
    }
}

TEST(MeanVariance, RejectsInfeasibleOrInvalidInput) {
    const Matrix c = small_cov();
    const Vector mu = Vector::Zero(4);
    EXPECT_THROW((void)mean_variance(mu, c, 1.0, Vector::Constant(4, 0.3), Vector::Ones(4)),
                 InvalidArgument);
    EXPECT_THROW((void)mean_variance(mu, c, 0.0, Vector::Zero(4), Vector::Ones(4)),
                 InvalidArgument);
    Matrix asym = c;
    asym(0, 1) += 0.01;
    EXPECT_THROW((void)min_variance(asym, Vector::Zero(4), Vector::Ones(4)), InvalidArgument);
}

TEST(RiskParity, EqualRiskContributions) {
    for (const Matrix& c : {small_cov(), hrp_cov()}) {
        const auto r = risk_parity(c);
        ASSERT_TRUE(r.converged);
        EXPECT_NEAR(r.weights.sum(), 1.0, 1e-14);
        EXPECT_TRUE((r.weights.array() > 0.0).all());
        const Vector marginal = c * r.weights;
        const double var = r.weights.dot(marginal);
        for (Eigen::Index i = 0; i < c.rows(); ++i) {
            EXPECT_NEAR(r.weights(i) * marginal(i) / var, 1.0 / static_cast<double>(c.rows()),
                        1e-10);
        }
        EXPECT_LT(r.objective, 1e-8);
    }
}

TEST(RiskParity, UncorrelatedAssetsGetInverseVolatility) {
    Matrix c = Matrix::Zero(3, 3);
    c.diagonal() << 0.04, 0.01, 0.0025; // vols 20 %, 10 %, 5 %
    const auto r = risk_parity(c);
    Vector expected(3);
    expected << 1.0 / 0.2, 1.0 / 0.1, 1.0 / 0.05;
    expected /= expected.sum();
    EXPECT_TRUE(r.weights.isApprox(expected, 1e-12));
}

TEST(Hrp, MatchesPublishedAlgorithmWithScipyLinkage) {
    const HrpResult r = hrp(hrp_cov());
    ASSERT_EQ(r.order.size(), static_cast<std::size_t>(ref::kHrpN));
    for (int i = 0; i < ref::kHrpN; ++i) {
        EXPECT_EQ(r.order[static_cast<std::size_t>(i)], ref::kHrpOrder[i]) << i;
        EXPECT_NEAR(r.weights(i), ref::kHrpWeights[i], 1e-12) << i;
    }
    EXPECT_NEAR(r.weights.sum(), 1.0, 1e-14);
    EXPECT_TRUE((r.weights.array() > 0.0).all());
}

TEST(Hrp, TwoAssetsGetInverseVarianceWeights) {
    Matrix c(2, 2);
    c << 0.04, 0.01, 0.01, 0.09;
    const HrpResult r = hrp(c);
    EXPECT_NEAR(r.weights(0), (1 / 0.04) / (1 / 0.04 + 1 / 0.09), 1e-15);
    EXPECT_NEAR(r.weights(1), (1 / 0.09) / (1 / 0.04 + 1 / 0.09), 1e-15);
    EXPECT_THROW((void)hrp(Matrix::Zero(2, 2)), InvalidArgument);
}

} // namespace
