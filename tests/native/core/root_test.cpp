#include "qe/core/errors.hpp"
#include "qe/core/root.hpp"

#include <cmath>
#include <gtest/gtest.h>
#include <numbers>

namespace {

using qe::core::brent;

TEST(Brent, FindsSqrtTwo) {
    const auto r = brent([](double x) { return x * x - 2.0; }, 0.0, 2.0, 1e-15);
    ASSERT_TRUE(r.converged);
    EXPECT_NEAR(r.x, std::numbers::sqrt2, 4e-16);
    EXPECT_LT(r.iterations, 20);
}

TEST(Brent, FindsFixedPointOfCosine) {
    const auto r = brent([](double x) { return std::cos(x) - x; }, 0.0, 1.0, 1e-15);
    ASSERT_TRUE(r.converged);
    EXPECT_NEAR(std::cos(r.x), r.x, 1e-15);
}

TEST(Brent, HandlesRootAtBracketEnd) {
    const auto r = brent([](double x) { return x - 3.0; }, 3.0, 5.0);
    EXPECT_TRUE(r.converged);
    EXPECT_DOUBLE_EQ(r.x, 3.0);
}

TEST(Brent, RejectsUnbracketedRoot) {
    EXPECT_THROW((void)brent([](double x) { return x * x + 1.0; }, -1.0, 1.0), qe::InvalidArgument);
}

TEST(Brent, ReportsNonConvergenceWithinIterationLimit) {
    const auto r = brent([](double x) { return x * x * x - 0.3; }, 0.0, 10.0, 1e-15, 3);
    EXPECT_FALSE(r.converged);
    EXPECT_EQ(r.iterations, 3);
}

} // namespace
