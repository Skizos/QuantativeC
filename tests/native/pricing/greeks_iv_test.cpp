#include "qe/core/errors.hpp"
#include "qe/pricing/black_scholes.hpp"
#include "qe/pricing/implied_vol.hpp"

#include <cmath>
#include <gtest/gtest.h>

namespace {

using qe::pricing::bs_greeks;
using qe::pricing::bs_price;
using qe::pricing::BsParams;
using qe::pricing::implied_vol;
using qe::pricing::OptionType;
using qe::pricing::validate_for_greeks;

BsParams make(double s, double k, double r, double q, double vol, double t, OptionType type) {
    return {.spot = s,
            .strike = k,
            .rate = r,
            .dividend_yield = q,
            .volatility = vol,
            .expiry_years = t,
            .type = type};
}

double central(auto&& f, double x, double h) {
    return (f(x + h) - f(x - h)) / (2.0 * h);
}

// <verification_requirements>: Greeks vs finite differences.
TEST(Greeks, MatchCentralFiniteDifferences) {
    for (const OptionType type : {OptionType::Call, OptionType::Put}) {
        for (const double s : {80.0, 100.0, 125.0}) {
            for (const double t : {0.1, 1.0, 3.0}) {
                const BsParams p = make(s, 100, 0.03, 0.015, 0.3, t, type);
                const auto g = bs_greeks(p);
                auto price_at = [&](auto mutate) {
                    return [=](double x) {
                        BsParams q = p;
                        mutate(q, x);
                        return bs_price(q);
                    };
                };
                const auto by_spot = price_at([](BsParams& q, double x) { q.spot = x; });
                const auto by_vol = price_at([](BsParams& q, double x) { q.volatility = x; });
                const auto by_rate = price_at([](BsParams& q, double x) { q.rate = x; });
                const auto by_expiry = price_at([](BsParams& q, double x) { q.expiry_years = x; });

                const double hs = 1e-4 * s;
                EXPECT_NEAR(g.price, bs_price(p), 1e-12);
                EXPECT_NEAR(g.delta, central(by_spot, s, hs), 1e-7);
                EXPECT_NEAR(g.gamma,
                            (by_spot(s + hs) - 2.0 * bs_price(p) + by_spot(s - hs)) / (hs * hs),
                            1e-5);
                EXPECT_NEAR(g.vega, central(by_vol, 0.3, 1e-5), 1e-6 * std::max(1.0, g.vega));
                EXPECT_NEAR(g.rho, central(by_rate, 0.03, 1e-6),
                            1e-6 * std::max(1.0, std::abs(g.rho)));
                EXPECT_NEAR(g.theta, -central(by_expiry, t, 1e-6),
                            1e-5 * std::max(1.0, std::abs(g.theta)));
            }
        }
    }
}

TEST(Greeks, PutCallRelationships) {
    const auto c = bs_greeks(make(100, 95, 0.04, 0.02, 0.25, 0.75, OptionType::Call));
    const auto p = bs_greeks(make(100, 95, 0.04, 0.02, 0.25, 0.75, OptionType::Put));
    EXPECT_NEAR(c.delta - p.delta, std::exp(-0.02 * 0.75), 1e-14);
    EXPECT_NEAR(c.gamma, p.gamma, 1e-15);
    EXPECT_NEAR(c.vega, p.vega, 1e-12);
}

TEST(Greeks, RequirePositiveVolAndExpiry) {
    EXPECT_TRUE(
        validate_for_greeks(make(100, 100, 0.0, 0.0, 0.0, 1.0, OptionType::Call)).has_value());
    EXPECT_TRUE(
        validate_for_greeks(make(100, 100, 0.0, 0.0, 0.2, 0.0, OptionType::Call)).has_value());
    EXPECT_FALSE(
        validate_for_greeks(make(100, 100, 0.0, 0.0, 0.2, 1.0, OptionType::Call)).has_value());
}

// <verification_requirements>: implied-vol round trip.
TEST(ImpliedVol, RoundTripsAcrossGrid) {
    for (const OptionType type : {OptionType::Call, OptionType::Put}) {
        for (const double k : {70.0, 90.0, 100.0, 115.0, 130.0}) {
            for (const double t : {0.1, 0.5, 2.0}) {
                for (const double vol : {0.05, 0.2, 0.6, 1.5}) {
                    const BsParams p = make(100, k, 0.02, 0.01, vol, t, type);
                    const double price = bs_price(p);
                    if (bs_greeks(p).vega < 1e-3) {
                        continue; // price carries no information about vol here
                    }
                    const auto iv = implied_vol(p, price);
                    EXPECT_NEAR(iv.volatility, vol, 1e-9)
                        << "k=" << k << " t=" << t << " vol=" << vol;
                    EXPECT_LT(iv.iterations, 60);
                }
            }
        }
    }
}

TEST(ImpliedVol, RejectsPricesOutsideNoArbitrageBounds) {
    const BsParams p = make(100, 100, 0.05, 0.0, 0.0, 1.0, OptionType::Call);
    const double lower = 100.0 - 100.0 * std::exp(-0.05);
    EXPECT_THROW((void)implied_vol(p, lower - 0.01), qe::InvalidArgument);
    EXPECT_THROW((void)implied_vol(p, 100.0), qe::InvalidArgument); // = discounted spot
    EXPECT_THROW((void)implied_vol(p, std::nan("")), qe::InvalidArgument);
    BsParams expired = p;
    expired.expiry_years = 0.0;
    EXPECT_THROW((void)implied_vol(expired, 5.0), qe::InvalidArgument);
}

} // namespace
