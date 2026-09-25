#include "qe/core/math.hpp"
#include "reference/phase2_reference.inc"

#include <cmath>
#include <cstddef>
#include <gtest/gtest.h>
#include <iterator>

namespace {

using qe::core::norm_cdf;
using qe::core::norm_inv;
using qe::core::norm_pdf;

TEST(NormCdf, KnownValuesAndSymmetry) {
    EXPECT_DOUBLE_EQ(norm_cdf(0.0), 0.5);
    EXPECT_NEAR(norm_cdf(1.959963984540054), 0.975, 1e-15);
    for (const double x : {0.1, 0.7, 1.3, 2.9, 5.0}) {
        EXPECT_NEAR(norm_cdf(x) + norm_cdf(-x), 1.0, 1e-15);
    }
    // Lower tail keeps relative precision (erfc, not 1 + erf): N(-10) ≈ 7.6199e-24.
    EXPECT_NEAR(norm_cdf(-10.0) / 7.619853024160527e-24, 1.0, 1e-12);
}

TEST(NormPdf, IntegratesToOneAndPeaksAtZero) {
    EXPECT_NEAR(norm_pdf(0.0), 0.3989422804014327, 1e-16);
    double integral = 0.0;
    const double h = 1e-3;
    for (double x = -12.0; x < 12.0; x += h) {
        integral += norm_pdf(x + 0.5 * h) * h;
    }
    EXPECT_NEAR(integral, 1.0, 1e-9);
}

TEST(NormInv, MatchesScipyPpf) {
    static_assert(std::size(qe::testref::kPpfP) == std::size(qe::testref::kPpfX));
    for (std::size_t i = 0; i < std::size(qe::testref::kPpfP); ++i) {
        const double expected = qe::testref::kPpfX[i];
        const double actual = norm_inv(qe::testref::kPpfP[i]);
        EXPECT_NEAR(actual, expected, 1e-14 * std::max(1.0, std::abs(expected)))
            << "p=" << qe::testref::kPpfP[i];
    }
}

TEST(NormInv, RoundTripsThroughCdfAndHandlesEdges) {
    for (double p = 1e-6; p < 1.0; p += 0.0137) {
        EXPECT_NEAR(norm_cdf(norm_inv(p)), p, 1e-15);
    }
    EXPECT_TRUE(std::isinf(norm_inv(0.0)) && norm_inv(0.0) < 0.0);
    EXPECT_TRUE(std::isinf(norm_inv(1.0)) && norm_inv(1.0) > 0.0);
    EXPECT_TRUE(std::isnan(norm_inv(-0.1)));
    EXPECT_TRUE(std::isnan(norm_inv(std::nan(""))));
    EXPECT_DOUBLE_EQ(norm_inv(0.5), 0.0);
}

} // namespace
