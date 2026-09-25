#include "qe/core/errors.hpp"
#include "qe/pricing/binomial.hpp"
#include "qe/pricing/black_scholes.hpp"
#include "qe/pricing/monte_carlo.hpp"
#include "reference/phase2_reference.inc"

#include <cmath>
#include <gtest/gtest.h>

namespace {

using qe::pricing::bs_price;
using qe::pricing::BsParams;
using qe::pricing::crr_price;
using qe::pricing::Exercise;
using qe::pricing::mc_european;
using qe::pricing::McConfig;
using qe::pricing::OptionType;

BsParams make(double s, double k, double r, double q, double vol, double t, OptionType type) {
    return {.spot = s,
            .strike = k,
            .rate = r,
            .dividend_yield = q,
            .volatility = vol,
            .expiry_years = t,
            .type = type};
}

// Hull, Example 21.1: 5-step CRR American put, S=K=50, r=10%, vol=40%, T=5 months -> 4.49.
TEST(Crr, HullFiveStepAmericanPut) {
    EXPECT_NEAR(crr_price(make(50, 50, 0.10, 0.0, 0.40, 5.0 / 12.0, OptionType::Put), 5,
                          Exercise::American),
                4.49, 5e-3);
}

TEST(Crr, MatchesIndependentNumpyLattice) {
    const BsParams put = make(100, 110, 0.03, 0.01, 0.25, 1.5, OptionType::Put);
    const BsParams call = make(100, 110, 0.03, 0.01, 0.25, 1.5, OptionType::Call);
    EXPECT_NEAR(crr_price(put, 500, Exercise::American), qe::testref::kCrrAmericanPut500, 1e-10);
    EXPECT_NEAR(crr_price(put, 500, Exercise::European), qe::testref::kCrrEuropeanPut500, 1e-10);
    EXPECT_NEAR(crr_price(call, 500, Exercise::American), qe::testref::kCrrAmericanCall500, 1e-10);
}

// <verification_requirements>: American >= European.
TEST(Crr, AmericanIsAtLeastEuropean) {
    for (const OptionType type : {OptionType::Call, OptionType::Put}) {
        for (const double k : {80.0, 100.0, 120.0}) {
            for (const double q : {0.0, 0.04}) {
                const BsParams p = make(100, k, 0.05, q, 0.3, 1.0, type);
                const double american = crr_price(p, 400, Exercise::American);
                const double european = crr_price(p, 400, Exercise::European);
                EXPECT_GE(american, european - 1e-12);
                EXPECT_GE(american + 0.01, bs_price(p)); // lattice error bound at 400 steps
            }
        }
    }
    // Without dividends an American call is never exercised early.
    const BsParams call = make(100, 100, 0.05, 0.0, 0.3, 1.0, OptionType::Call);
    EXPECT_NEAR(crr_price(call, 400, Exercise::American), crr_price(call, 400, Exercise::European),
                1e-12);
}

TEST(Crr, EuropeanConvergesToBlackScholes) {
    const BsParams p = make(100, 100, 0.05, 0.0, 0.2, 1.0, OptionType::Call);
    EXPECT_NEAR(crr_price(p, 2000, Exercise::European), bs_price(p), 2e-3);
}

TEST(Crr, RejectsInvalidInputs) {
    const BsParams p = make(100, 100, 0.05, 0.0, 0.2, 1.0, OptionType::Call);
    EXPECT_THROW((void)crr_price(p, 0, Exercise::European), qe::InvalidArgument);
    EXPECT_THROW((void)crr_price(p, 10, static_cast<Exercise>(7)), qe::InvalidArgument);
    EXPECT_THROW((void)crr_price(make(100, 100, 0.05, 0.0, 0.0, 1.0, OptionType::Call), 10,
                                 Exercise::European),
                 qe::InvalidArgument);
    // Large drift with tiny volatility and one step pushes p outside (0, 1).
    EXPECT_THROW((void)crr_price(make(100, 100, 0.9, 0.0, 0.01, 1.0, OptionType::Call), 1,
                                 Exercise::European),
                 qe::InvalidArgument);
}

struct McCase {
    const char* name;
    bool antithetic;
    bool control;
    bool sobol;
};

class McWithinThreeSe : public ::testing::TestWithParam<McCase> {};

// <verification_requirements>: MC within 3 SE; MC results carry SE + path count.
TEST_P(McWithinThreeSe, AgainstBlackScholes) {
    const McCase c = GetParam();
    for (const OptionType type : {OptionType::Call, OptionType::Put}) {
        for (const double k : {85.0, 100.0, 115.0}) {
            const BsParams p = make(100, k, 0.04, 0.01, 0.25, 1.0, type);
            McConfig cfg;
            cfg.paths = 100'000;
            cfg.seed = 20260925;
            cfg.antithetic = c.antithetic;
            cfg.control_variate = c.control;
            cfg.sobol = c.sobol;
            cfg.replications = 20;
            const auto r = mc_european(p, cfg);
            const double exact = bs_price(p);
            EXPECT_GT(r.std_error, 0.0);
            EXPECT_EQ(r.paths, cfg.paths);
            EXPECT_EQ(r.seed, cfg.seed);
            EXPECT_LE(std::abs(r.price - exact), 3.0 * r.std_error)
                << c.name << " k=" << k << " price=" << r.price << " exact=" << exact
                << " se=" << r.std_error;
        }
    }
}

INSTANTIATE_TEST_SUITE_P(Estimators, McWithinThreeSe,
                         ::testing::Values(McCase{"plain", false, false, false},
                                           McCase{"antithetic", true, false, false},
                                           McCase{"control", false, true, false},
                                           McCase{"antithetic_control", true, true, false},
                                           McCase{"sobol", false, false, true},
                                           McCase{"sobol_antithetic", true, false, true},
                                           McCase{"sobol_control", false, true, true}),
                         [](const auto& test_info) { return std::string(test_info.param.name); });

TEST(MonteCarlo, VarianceReductionShrinksStandardError) {
    const BsParams p = make(100, 100, 0.04, 0.0, 0.25, 1.0, OptionType::Call);
    McConfig cfg;
    cfg.paths = 100'000;
    cfg.seed = 11;
    const double plain = mc_european(p, cfg).std_error;
    cfg.control_variate = true;
    const double control = mc_european(p, cfg).std_error;
    cfg.control_variate = false;
    cfg.sobol = true;
    const double sobol = mc_european(p, cfg).std_error;
    EXPECT_LT(control, 0.5 * plain);
    EXPECT_LT(sobol, 0.2 * plain);
}

// <verification_requirements>: seed determinism.
TEST(MonteCarlo, SeedDeterminism) {
    const BsParams p = make(100, 105, 0.03, 0.0, 0.3, 0.5, OptionType::Put);
    McConfig cfg;
    cfg.paths = 50'000;
    cfg.seed = 99;
    for (const bool sobol : {false, true}) {
        cfg.sobol = sobol;
        const auto a = mc_european(p, cfg);
        const auto b = mc_european(p, cfg);
        EXPECT_EQ(a.price, b.price);
        EXPECT_EQ(a.std_error, b.std_error);
        McConfig other = cfg;
        other.seed = 100;
        EXPECT_NE(mc_european(p, other).price, a.price);
    }
}

TEST(MonteCarlo, MultiStepPathsGiveTheSameEuropeanPrice) {
    const BsParams p = make(100, 100, 0.04, 0.0, 0.25, 1.0, OptionType::Call);
    McConfig cfg;
    cfg.paths = 40'000;
    cfg.seed = 5;
    cfg.steps = 12;
    cfg.antithetic = true;
    const auto r = mc_european(p, cfg);
    EXPECT_LE(std::abs(r.price - bs_price(p)), 3.0 * r.std_error);
}

TEST(MonteCarlo, RejectsInvalidConfiguration) {
    const BsParams p = make(100, 100, 0.04, 0.0, 0.25, 1.0, OptionType::Call);
    McConfig cfg;
    cfg.paths = 1;
    EXPECT_THROW((void)mc_european(p, cfg), qe::InvalidArgument);
    cfg.paths = 1001;
    cfg.antithetic = true; // odd path count with antithetic pairs
    EXPECT_THROW((void)mc_european(p, cfg), qe::InvalidArgument);
    cfg = McConfig{};
    cfg.sobol = true;
    cfg.replications = 1;
    EXPECT_THROW((void)mc_european(p, cfg), qe::InvalidArgument);
    cfg.replications = 16;
    cfg.steps = 65;
    EXPECT_THROW((void)mc_european(p, cfg), qe::InvalidArgument);
    cfg.steps = 0;
    EXPECT_THROW((void)mc_european(p, cfg), qe::InvalidArgument);
}

} // namespace
