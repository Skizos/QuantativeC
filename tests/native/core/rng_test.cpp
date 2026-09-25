#include "qe/core/rng.hpp"
#include "qe/core/stats.hpp"

#include <cmath>
#include <cstdint>
#include <gtest/gtest.h>
#include <random>

namespace {

using qe::core::derive_seed;
using qe::core::Rng;
using qe::core::splitmix64;

TEST(Rng, Mt19937_64MatchesTheStandardsRequiredValue) {
    // [rand.predef]: the 10000th invocation of a default-constructed mt19937_64 yields this value.
    std::mt19937_64 engine;
    engine.discard(9999);
    EXPECT_EQ(engine(), 9981545732273789042ULL);
}

TEST(Rng, SplitMix64KnownValue) {
    // splitmix64 with state 0 yields 0xE220A8397B1DCDAF as its first output.
    EXPECT_EQ(splitmix64(0), 0xE220A8397B1DCDAFULL);
    EXPECT_NE(derive_seed(1, 0), derive_seed(1, 1));
    EXPECT_NE(derive_seed(1, 0), derive_seed(2, 0));
}

TEST(Rng, SameSeedSameStreamDifferentSeedDifferentStream) {
    Rng a(42);
    Rng b(42);
    Rng c(43);
    int differences = 0;
    for (int i = 0; i < 1000; ++i) {
        const double x = a.normal();
        EXPECT_EQ(x, b.normal());
        differences += x != c.normal() ? 1 : 0;
    }
    EXPECT_EQ(differences, 1000);
}

TEST(Rng, UniformIsOpenAndNormalHasUnitMoments) {
    Rng rng(7);
    qe::core::Welford u;
    qe::core::Welford z;
    for (int i = 0; i < 200'000; ++i) {
        const double x = rng.uniform_open();
        ASSERT_GT(x, 0.0);
        ASSERT_LT(x, 1.0);
        u.add(x);
        z.add(rng.normal());
    }
    EXPECT_NEAR(u.mean, 0.5, 5 * std::sqrt(1.0 / 12.0 / 200'000));
    EXPECT_NEAR(z.mean, 0.0, 5 * std::sqrt(1.0 / 200'000));
    EXPECT_NEAR(z.variance(), 1.0, 0.02);
}

} // namespace
