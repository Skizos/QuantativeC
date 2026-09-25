#include "qe/core/errors.hpp"
#include "qe/risk/covariance.hpp"
#include "reference/phase2_reference.inc"

#include <cmath>
#include <gtest/gtest.h>

namespace {

using namespace qe;
using namespace qe::risk;
namespace ref = qe::testref;

Matrix reference_returns() {
    return Eigen::Map<const Eigen::Matrix<double, Eigen::Dynamic, Eigen::Dynamic, Eigen::RowMajor>>(
        ref::kCovReturns, ref::kCovT, ref::kCovN);
}

void expect_matrix_near(const Matrix& actual, const double* expected_row_major, double rel_tol) {
    const Eigen::Index n = actual.rows();
    for (Eigen::Index i = 0; i < n; ++i) {
        for (Eigen::Index j = 0; j < actual.cols(); ++j) {
            const double e = expected_row_major[i * actual.cols() + j];
            EXPECT_NEAR(actual(i, j), e, rel_tol * std::max(std::abs(e), 1e-12))
                << "(" << i << "," << j << ")";
        }
    }
}

TEST(Covariance, SampleMatchesNumpyCov) {
    expect_matrix_near(sample_covariance(reference_returns()), ref::kCovSample, 1e-10);
}

TEST(Covariance, EwmaMatchesReferenceAndReducesToBiasedSampleAtLambdaOne) {
    const Matrix x = reference_returns();
    expect_matrix_near(ewma_covariance(x, 0.94), ref::kCovEwma094, 1e-10);
    const Matrix biased =
        sample_covariance(x) * (static_cast<double>(x.rows() - 1) / static_cast<double>(x.rows()));
    EXPECT_TRUE(ewma_covariance(x, 1.0).isApprox(biased, 1e-12));
}

TEST(Covariance, LedoitWolfMatchesScikitLearn) {
    const CovarianceResult lw = ledoit_wolf(reference_returns());
    EXPECT_NEAR(lw.shrinkage, ref::kLedoitWolfShrinkage, 1e-10);
    expect_matrix_near(lw.covariance, ref::kCovLedoitWolf, 1e-10);
}

// <verification_requirements>: covariance SPD.
TEST(Covariance, AllEstimatorsArePositiveDefinite) {
    const Matrix x = reference_returns();
    EXPECT_TRUE(is_positive_definite(sample_covariance(x)));
    EXPECT_TRUE(is_positive_definite(ewma_covariance(x, 0.94)));
    EXPECT_TRUE(is_positive_definite(ledoit_wolf(x).covariance));
}

TEST(Covariance, LedoitWolfIsPositiveDefiniteWhenSampleIsSingular) {
    const Matrix few = reference_returns().topRows(3); // T = 3 < N = 5
    EXPECT_FALSE(is_positive_definite(sample_covariance(few)));
    const CovarianceResult lw = ledoit_wolf(few);
    EXPECT_GT(lw.shrinkage, 0.0);
    EXPECT_TRUE(is_positive_definite(lw.covariance));
}

TEST(Covariance, RejectsBadInput) {
    EXPECT_THROW((void)sample_covariance(Matrix::Zero(1, 3)), InvalidArgument);
    Matrix bad = reference_returns();
    bad(3, 2) = std::nan("");
    EXPECT_THROW((void)sample_covariance(bad), InvalidArgument);
    EXPECT_THROW((void)ewma_covariance(reference_returns(), 0.0), InvalidArgument);
    EXPECT_THROW((void)ewma_covariance(reference_returns(), 1.5), InvalidArgument);
}

} // namespace
