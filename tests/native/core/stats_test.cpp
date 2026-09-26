#include "qe/core/errors.hpp"
#include "qe/core/interp.hpp"
#include "qe/core/stats.hpp"
#include "reference/phase2_reference.inc"

#include <algorithm>
#include <gtest/gtest.h>
#include <iterator>
#include <vector>

namespace {

using namespace qe::core;

TEST(Welford, MatchesTwoPassAndMergesExactly) {
    const std::vector<double> xs{1e9 + 4, 1e9 + 7, 1e9 + 13, 1e9 + 16};
    Welford all;
    Welford left;
    Welford right;
    for (std::size_t i = 0; i < xs.size(); ++i) {
        all.add(xs[i]);
        (i < 2 ? left : right).add(xs[i]);
    }
    EXPECT_DOUBLE_EQ(all.mean, 1e9 + 10);
    EXPECT_DOUBLE_EQ(all.variance(), 30.0); // catastrophic cancellation-free
    left.merge(right);
    EXPECT_EQ(left.n, 4);
    EXPECT_DOUBLE_EQ(left.mean, all.mean);
    EXPECT_NEAR(left.variance(), all.variance(), 1e-9);
}

TEST(Welford2, CovarianceMatchesDefinitionAfterMerge) {
    Welford2 a;
    Welford2 b;
    const double xs[] = {1, 2, 3, 4, 5, 6};
    const double ys[] = {2, 1, 4, 3, 6, 8};
    for (int i = 0; i < 6; ++i) {
        (i < 3 ? a : b).add(xs[i], ys[i]);
    }
    a.merge(b);
    // Co-moments by the definition (checked in Python): sum (x - 3.5)(y - 4) = 22,
    // sum (x - 3.5)^2 = 17.5, sum (y - 4)^2 = 34.
    EXPECT_NEAR(a.c_xy, 22.0, 1e-12);
    EXPECT_NEAR(a.m2_x, 17.5, 1e-12);
    EXPECT_NEAR(a.m2_y, 34.0, 1e-12);
}

TEST(NeumaierSum, RecoversLostLowOrderBits) {
    NeumaierSum s;
    s.add(1.0);
    s.add(1e100);
    s.add(1.0);
    s.add(-1e100);
    EXPECT_DOUBLE_EQ(s.value(), 2.0);
}

TEST(Quantile, MatchesNumpyLinear) {
    std::vector<double> data(std::begin(qe::testref::kQuantileData),
                             std::end(qe::testref::kQuantileData));
    std::sort(data.begin(), data.end());
    for (std::size_t i = 0; i < std::size(qe::testref::kQuantileLevels); ++i) {
        EXPECT_NEAR(quantile_linear_sorted(data, qe::testref::kQuantileLevels[i]),
                    qe::testref::kQuantileExpected[i], 1e-15);
    }
    EXPECT_THROW((void)quantile_linear_sorted({}, 0.5), qe::InvalidArgument);
    EXPECT_THROW((void)quantile_linear_sorted(data, 1.5), qe::InvalidArgument);
}

TEST(LinearInterpolator, InterpolatesAndExtrapolatesFlat) {
    const std::vector<double> x{0.0, 1.0, 3.0};
    const std::vector<double> y{1.0, 3.0, 2.0};
    const LinearInterpolator f(x, y);
    EXPECT_DOUBLE_EQ(f(-5.0), 1.0);
    EXPECT_DOUBLE_EQ(f(0.5), 2.0);
    EXPECT_DOUBLE_EQ(f(1.0), 3.0);
    EXPECT_DOUBLE_EQ(f(2.0), 2.5);
    EXPECT_DOUBLE_EQ(f(9.0), 2.0);
    const std::vector<double> not_increasing{0.0, 0.0};
    const std::vector<double> two{1.0, 2.0};
    EXPECT_THROW(LinearInterpolator(not_increasing, two), qe::InvalidArgument);
    EXPECT_THROW(LinearInterpolator(x, two), qe::InvalidArgument); // size mismatch
}

} // namespace
