#include "qe/core/errors.hpp"
#include "qe/core/sobol.hpp"
#include "reference/phase2_reference.inc"

#include <cstdint>
#include <gtest/gtest.h>
#include <vector>

namespace {

using qe::core::Sobol;

TEST(Sobol, MatchesScipyUnscrambledPoints) {
    Sobol sobol(qe::testref::kSobolRefDims);
    std::vector<std::uint32_t> point(qe::testref::kSobolRefDims);
    for (int i = 0; i < qe::testref::kSobolRefPoints; ++i) {
        sobol.next(point);
        for (int d = 0; d < qe::testref::kSobolRefDims; ++d) {
            const double u = static_cast<double>(point[static_cast<std::size_t>(d)]) * 0x1.0p-32;
            EXPECT_EQ(u, qe::testref::kSobolRef[i * qe::testref::kSobolRefDims + d])
                << "point " << i << " dim " << d;
        }
    }
    EXPECT_EQ(sobol.index(), 64U);
}

TEST(Sobol, FirstDimensionIsVanDerCorput) {
    Sobol sobol(1);
    std::vector<std::uint32_t> p(1);
    const double expected[] = {0.0, 0.5, 0.75, 0.25, 0.375, 0.875, 0.625, 0.125};
    for (const double e : expected) {
        sobol.next(p);
        EXPECT_EQ(static_cast<double>(p[0]) * 0x1.0p-32, e);
    }
}

TEST(Sobol, AllDimensionsAreBalancedOverPowersOfTwo) {
    // Every dimension of a (t, s)-sequence puts exactly half of the first 2^k points below 1/2.
    Sobol sobol(Sobol::max_dims());
    std::vector<std::uint32_t> p(static_cast<std::size_t>(Sobol::max_dims()));
    std::vector<int> below(p.size(), 0);
    for (int i = 0; i < 1024; ++i) {
        sobol.next(p);
        for (std::size_t d = 0; d < p.size(); ++d) {
            below[d] += p[d] < 0x80000000U ? 1 : 0;
        }
    }
    for (std::size_t d = 0; d < p.size(); ++d) {
        EXPECT_EQ(below[d], 512) << "dim " << d;
    }
}

TEST(Sobol, RejectsBadDimensionsAndSpans) {
    EXPECT_THROW(Sobol(0), qe::InvalidArgument);
    EXPECT_THROW(Sobol(Sobol::max_dims() + 1), qe::InvalidArgument);
    Sobol sobol(3);
    std::vector<std::uint32_t> wrong(2);
    EXPECT_THROW(sobol.next(wrong), qe::InvalidArgument);
}

TEST(Sobol, OpenUnitMappingNeverHitsZeroOrOne) {
    EXPECT_GT(Sobol::to_open_unit(0U), 0.0);
    EXPECT_LT(Sobol::to_open_unit(0xFFFFFFFFU), 1.0);
}

} // namespace
