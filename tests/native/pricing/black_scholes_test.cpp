#include "qe/pricing/black_scholes.hpp"

#include <algorithm>
#include <cmath>
#include <gtest/gtest.h>
#include <limits>

namespace {

using qe::pricing::bs_price;
using qe::pricing::BsParams;
using qe::pricing::norm_cdf;
using qe::pricing::OptionType;
using qe::pricing::validate;

BsParams make(double s, double k, double r, double q, double vol, double t, OptionType type) {
    return BsParams{.spot = s,
                    .strike = k,
                    .rate = r,
                    .dividend_yield = q,
                    .volatility = vol,
                    .expiry_years = t,
                    .type = type};
}

// Spec reference (master prompt <verification_requirements>): S=100 K=100 r=5% q=0 vol=20% T=1.
TEST(BlackScholes, SpecReferenceCallAndPut) {
    const double call = bs_price(make(100, 100, 0.05, 0.0, 0.20, 1.0, OptionType::Call));
    const double put = bs_price(make(100, 100, 0.05, 0.0, 0.20, 1.0, OptionType::Put));
    EXPECT_NEAR(call, 10.4506, 1e-4);
    EXPECT_NEAR(put, 5.5735, 1e-4);
    // Closed form evaluated independently (Python math.erfc) to 1e-12.
    EXPECT_NEAR(call, 10.450583572185565, 1e-12);
    EXPECT_NEAR(put, 5.573526022256971, 1e-12);
}

// Hull, "Options, Futures, and Other Derivatives", Example 15.6: c = 4.76, p = 0.81.
TEST(BlackScholes, HullTextbookExample) {
    EXPECT_NEAR(bs_price(make(42, 40, 0.10, 0.0, 0.20, 0.5, OptionType::Call)), 4.76, 5e-3);
    EXPECT_NEAR(bs_price(make(42, 40, 0.10, 0.0, 0.20, 0.5, OptionType::Put)), 0.81, 5e-3);
}

TEST(BlackScholes, DividendYieldCase) {
    EXPECT_NEAR(bs_price(make(90, 110, 0.02, 0.03, 0.35, 0.75, OptionType::Call)),
                4.296710994571292, 1e-12);
    EXPECT_NEAR(bs_price(make(90, 110, 0.02, 0.03, 0.35, 0.75, OptionType::Put)),
                24.661413003507924, 1e-12);
}

TEST(BlackScholes, PutCallParityHoldsOnGrid) {
    for (const double s : {50.0, 80.0, 100.0, 120.0, 250.0}) {
        for (const double k : {60.0, 100.0, 140.0}) {
            for (const double vol : {0.05, 0.2, 0.6, 1.5}) {
                for (const double t : {0.01, 0.25, 1.0, 5.0}) {
                    for (const double r : {-0.01, 0.0, 0.04}) {
                        const double q = 0.015;
                        const double c = bs_price(make(s, k, r, q, vol, t, OptionType::Call));
                        const double p = bs_price(make(s, k, r, q, vol, t, OptionType::Put));
                        const double parity = s * std::exp(-q * t) - k * std::exp(-r * t);
                        EXPECT_NEAR(c - p, parity, 1e-10)
                            << "s=" << s << " k=" << k << " vol=" << vol << " t=" << t
                            << " r=" << r;
                    }
                }
            }
        }
    }
}

TEST(BlackScholes, NoArbitrageBounds) {
    for (const double s : {70.0, 100.0, 130.0}) {
        const auto p = make(s, 100, 0.03, 0.01, 0.25, 2.0, OptionType::Call);
        const double c = bs_price(p);
        const double df_s = s * std::exp(-0.01 * 2.0);
        const double df_k = 100 * std::exp(-0.03 * 2.0);
        EXPECT_LE(c, df_s);
        EXPECT_GE(c, std::max(df_s - df_k, 0.0));
    }
}

TEST(BlackScholes, MonotoneInSpotStrikeAndVol) {
    const double base = bs_price(make(100, 100, 0.03, 0.0, 0.25, 1.0, OptionType::Call));
    EXPECT_GT(bs_price(make(101, 100, 0.03, 0.0, 0.25, 1.0, OptionType::Call)), base);
    EXPECT_LT(bs_price(make(100, 101, 0.03, 0.0, 0.25, 1.0, OptionType::Call)), base);
    EXPECT_GT(bs_price(make(100, 100, 0.03, 0.0, 0.30, 1.0, OptionType::Call)), base);
}

TEST(BlackScholes, ZeroExpiryIsIntrinsic) {
    EXPECT_DOUBLE_EQ(bs_price(make(110, 100, 0.05, 0.0, 0.2, 0.0, OptionType::Call)), 10.0);
    EXPECT_DOUBLE_EQ(bs_price(make(110, 100, 0.05, 0.0, 0.2, 0.0, OptionType::Put)), 0.0);
    EXPECT_DOUBLE_EQ(bs_price(make(90, 100, 0.05, 0.0, 0.2, 0.0, OptionType::Put)), 10.0);
}

TEST(BlackScholes, ZeroVolIsDiscountedForwardIntrinsic) {
    const double expected = 100.0 - 100.0 * std::exp(-0.05);
    EXPECT_NEAR(bs_price(make(100, 100, 0.05, 0.0, 0.0, 1.0, OptionType::Call)), expected, 1e-14);
    EXPECT_DOUBLE_EQ(bs_price(make(100, 100, 0.05, 0.0, 0.0, 1.0, OptionType::Put)), 0.0);
}

TEST(BlackScholes, ValidateRejectsOutOfDomainInputs) {
    const auto ok = make(100, 100, 0.05, 0.0, 0.2, 1.0, OptionType::Call);
    EXPECT_FALSE(validate(ok).has_value());

    auto bad = ok;
    bad.spot = 0.0;
    EXPECT_TRUE(validate(bad).has_value());
    bad = ok;
    bad.strike = -1.0;
    EXPECT_TRUE(validate(bad).has_value());
    bad = ok;
    bad.volatility = -0.1;
    EXPECT_TRUE(validate(bad).has_value());
    bad = ok;
    bad.expiry_years = -1.0;
    EXPECT_TRUE(validate(bad).has_value());
    bad = ok;
    bad.rate = std::numeric_limits<double>::quiet_NaN();
    EXPECT_TRUE(validate(bad).has_value());
    bad = ok;
    bad.spot = std::numeric_limits<double>::infinity();
    EXPECT_TRUE(validate(bad).has_value());
    bad = ok;
    bad.type = static_cast<OptionType>(7);
    EXPECT_TRUE(validate(bad).has_value());
}

TEST(NormCdf, KnownValuesAndSymmetry) {
    EXPECT_DOUBLE_EQ(norm_cdf(0.0), 0.5);
    EXPECT_NEAR(norm_cdf(1.959963984540054), 0.975, 1e-15);
    for (const double x : {0.1, 0.7, 1.3, 2.9, 5.0}) {
        EXPECT_NEAR(norm_cdf(x) + norm_cdf(-x), 1.0, 1e-15);
    }
    // Lower tail keeps relative precision (erfc, not 1 + erf): N(-10) ≈ 7.6199e-24.
    EXPECT_NEAR(norm_cdf(-10.0) / 7.619853024160527e-24, 1.0, 1e-12);
}

} // namespace
