// ABI 1.1 through the shared library only (qe_api.h), as the .NET side sees it.
#include "qe_api.h"
#include "reference/phase2_reference.inc"

#include <cmath>
#include <cstdint>
#include <gtest/gtest.h>
#include <vector>

namespace {

namespace ref = qe::testref;

class Abi11 : public ::testing::Test {
  protected:
    void SetUp() override {
        const qe_engine_config cfg{static_cast<std::int32_t>(sizeof(qe_engine_config)), 0, 777};
        ASSERT_EQ(qe_engine_create(&cfg, &engine_), QE_OK);
    }
    void TearDown() override { EXPECT_EQ(qe_engine_destroy(engine_), QE_OK); }
    qe_engine* engine_{nullptr};
};

qe_bs_input option(double k, std::int32_t type) {
    return qe_bs_input{100, k, 0.05, 0.0, 0.2, 1.0, type, 0};
}

TEST(Abi11Version, MinorIsAtLeastOne) {
    std::int32_t major = 0;
    std::int32_t minor = 0;
    ASSERT_EQ(qe_abi_version(&major, &minor), QE_OK);
    EXPECT_EQ(major, 1);
    EXPECT_GE(minor, 1); // later minors keep every 1.1 export
}

TEST_F(Abi11, GreeksBatchPricesAndFlagsDegenerateElements) {
    std::vector<qe_bs_input> in{option(100, QE_OPTION_CALL), option(100, QE_OPTION_PUT),
                                option(100, QE_OPTION_CALL)};
    in[2].volatility = 0.0;
    std::vector<qe_bs_greeks> out(in.size());
    std::int64_t failed = -1;
    ASSERT_EQ(qe_bs_greeks_batch(engine_, in.data(), out.data(), 3, &failed), QE_OK);
    EXPECT_EQ(failed, 1);
    EXPECT_NEAR(out[0].price, 10.450583572185565, 1e-12);
    EXPECT_NEAR(out[0].delta - out[1].delta, 1.0, 1e-14);
    EXPECT_EQ(out[2].status, QE_E_INVALID_ARG);
    EXPECT_TRUE(std::isnan(out[2].vega));
}

TEST_F(Abi11, ImpliedVolRoundTripAndBounds) {
    std::vector<qe_iv_input> in{{100, 100, 0.05, 0.0, 1.0, 10.450583572185565, QE_OPTION_CALL, 0},
                                {100, 100, 0.05, 0.0, 1.0, 200.0, QE_OPTION_CALL, 0}};
    std::vector<qe_iv_output> out(in.size());
    std::int64_t failed = -1;
    ASSERT_EQ(qe_implied_vol_batch(engine_, in.data(), out.data(), 2, &failed), QE_OK);
    EXPECT_EQ(failed, 1);
    EXPECT_NEAR(out[0].volatility, 0.2, 1e-12);
    EXPECT_GT(out[0].iterations, 0);
    EXPECT_EQ(out[1].status, QE_E_INVALID_ARG);
}

TEST_F(Abi11, LatticeAmericanAtLeastEuropean) {
    const qe_bs_input put{50, 50, 0.10, 0.0, 0.40, 5.0 / 12.0, QE_OPTION_PUT, 0};
    std::vector<qe_lattice_input> in{
        {put, 5, QE_EXERCISE_AMERICAN}, {put, 5, QE_EXERCISE_EUROPEAN}, {put, 0, 0}};
    std::vector<qe_bs_output> out(in.size());
    std::int64_t failed = -1;
    ASSERT_EQ(qe_lattice_batch(engine_, in.data(), out.data(), 3, &failed), QE_OK);
    EXPECT_EQ(failed, 1);
    EXPECT_NEAR(out[0].price, 4.49, 5e-3); // Hull Example 21.1
    EXPECT_GE(out[0].price, out[1].price);
    EXPECT_EQ(out[2].status, QE_E_INVALID_ARG);
}

TEST_F(Abi11, MonteCarloUsesEngineSeedByDefaultAndReportsIt) {
    const qe_bs_input opt = option(100, QE_OPTION_CALL);
    qe_mc_config cfg{static_cast<std::int32_t>(sizeof(qe_mc_config)),
                     QE_MC_ANTITHETIC | QE_MC_CONTROL_VARIATE,
                     100'000,
                     0,
                     16,
                     1};
    qe_mc_result r{};
    ASSERT_EQ(qe_mc_european(engine_, &cfg, &opt, &r), QE_OK);
    EXPECT_EQ(r.seed, 777U);
    EXPECT_EQ(r.paths, 100'000);
    EXPECT_LE(std::abs(r.price - 10.450583572185565), 3.0 * r.std_error);
    cfg.flags = 64;
    EXPECT_EQ(qe_mc_european(engine_, &cfg, &opt, &r), QE_E_INVALID_ARG);
    cfg.flags = 0;
    cfg.struct_size = 8;
    EXPECT_EQ(qe_mc_european(engine_, &cfg, &opt, &r), QE_E_INVALID_ARG);
}

TEST_F(Abi11, CovarianceMatchesReferenceRowMajor) {
    const qe_cov_config lw{static_cast<std::int32_t>(sizeof(qe_cov_config)), QE_COV_LEDOIT_WOLF,
                           0.0};
    std::vector<double> cov(ref::kCovN * ref::kCovN);
    double shrinkage = -1.0;
    ASSERT_EQ(qe_covariance(engine_, &lw, ref::kCovReturns, ref::kCovT, ref::kCovN, cov.data(),
                            &shrinkage),
              QE_OK);
    EXPECT_NEAR(shrinkage, ref::kLedoitWolfShrinkage, 1e-10);
    for (std::size_t i = 0; i < cov.size(); ++i) {
        EXPECT_NEAR(cov[i], ref::kCovLedoitWolf[i], 1e-10 * std::abs(ref::kCovLedoitWolf[i]));
    }
    const qe_cov_config bad{static_cast<std::int32_t>(sizeof(qe_cov_config)), 9, 0.0};
    EXPECT_EQ(
        qe_covariance(engine_, &bad, ref::kCovReturns, ref::kCovT, ref::kCovN, cov.data(), nullptr),
        QE_E_INVALID_ARG);
}

TEST(Abi11Risk, VarEsHistoricalParametricAndErrors) {
    std::vector<double> r;
    for (int i = 1; i <= 100; ++i) {
        r.push_back(-i / 100.0);
    }
    qe_var_es v{};
    ASSERT_EQ(qe_var_es_historical(r.data(), 100, 0.95, &v), QE_OK);
    EXPECT_DOUBLE_EQ(v.var, 0.95);
    EXPECT_NEAR(v.es, 0.975, 1e-15);
    EXPECT_EQ(qe_var_es_historical(r.data(), 100, 1.0, &v), QE_E_INVALID_ARG);

    const double w[] = {1.0};
    const double c[] = {0.0004};
    ASSERT_EQ(qe_var_es_parametric(w, nullptr, c, 1, 0.99, &v), QE_OK);
    EXPECT_NEAR(v.var, 0.02 * ref::kZ99, 1e-15);
    EXPECT_NEAR(v.es, 0.02 * ref::kEsFactor99, 1e-15);
}

TEST_F(Abi11, MonteCarloVarIsSeededAndNotPsdIsNumeric) {
    const double w[] = {0.6, 0.4};
    const double c[] = {4e-4, 1e-4, 1e-4, 2.25e-4};
    qe_var_es a{};
    qe_var_es b{};
    ASSERT_EQ(qe_var_es_monte_carlo(engine_, w, nullptr, c, 2, 50'000, 0, 0.99, &a), QE_OK);
    ASSERT_EQ(qe_var_es_monte_carlo(engine_, w, nullptr, c, 2, 50'000, 0, 0.99, &b), QE_OK);
    EXPECT_EQ(a.seed, 777U);
    EXPECT_EQ(a.var, b.var);
    const double not_psd[] = {1e-4, 2e-4, 2e-4, 1e-4};
    EXPECT_EQ(qe_var_es_monte_carlo(engine_, w, nullptr, not_psd, 2, 1000, 1, 0.99, &a),
              QE_E_NUMERIC);
}

TEST(Abi11Risk, BetasAndStress) {
    const double index[] = {0.01, -0.02, 0.015, -0.005};
    const double returns[] = {0.02, 0.01,  -0.04, -0.02,
                              0.03, 0.015, -0.01, -0.005}; // 4 x 2, betas 2 and 1
    double betas[2] = {};
    ASSERT_EQ(qe_betas(returns, index, 4, 2, betas), QE_OK);
    EXPECT_NEAR(betas[0], 2.0, 1e-12);
    EXPECT_NEAR(betas[1], 1.0, 1e-12);
    const double values[] = {1000.0, 2000.0};
    const double shocks[] = {-0.1, -0.05, 0.0, 0.05}; // 2 scenarios x 2 assets
    double pnl[2] = {};
    ASSERT_EQ(qe_stress_pnl(values, shocks, 2, 2, pnl), QE_OK);
    EXPECT_DOUBLE_EQ(pnl[0], -200.0);
    EXPECT_DOUBLE_EQ(pnl[1], 100.0);
    EXPECT_EQ(qe_stress_pnl(values, shocks, 0, 2, pnl), QE_E_INVALID_ARG);
}

TEST_F(Abi11, OptimizeAllMethodsFullyInvested) {
    const double cov[] = {0.040, 0.006, 0.004, 0.006, 0.025, 0.005, 0.004, 0.005, 0.090};
    const double mu[] = {0.08, 0.05, 0.12};
    const double lower[] = {0.1, 0.1, 0.1};
    const double upper[] = {0.6, 0.6, 0.6};
    for (const std::int32_t method :
         {QE_OPT_MIN_VARIANCE, QE_OPT_MEAN_VARIANCE, QE_OPT_RISK_PARITY, QE_OPT_HRP}) {
        const qe_opt_config cfg{
            static_cast<std::int32_t>(sizeof(qe_opt_config)), method, 3.0, 0.0, 0, 0};
        const bool bounded = method == QE_OPT_MIN_VARIANCE || method == QE_OPT_MEAN_VARIANCE;
        double w[3] = {};
        qe_opt_result r{};
        ASSERT_EQ(qe_optimize(engine_, &cfg, mu, cov, bounded ? lower : nullptr,
                              bounded ? upper : nullptr, 3, w, &r),
                  QE_OK)
            << method;
        EXPECT_NEAR(w[0] + w[1] + w[2], 1.0, 1e-12) << method;
        EXPECT_EQ(r.converged, 1) << method;
        for (const double x : w) {
            EXPECT_GE(x, bounded ? 0.1 - 1e-15 : 0.0);
            EXPECT_LE(x, bounded ? 0.6 + 1e-15 : 1.0);
        }
    }
    const qe_opt_config rp{
        static_cast<std::int32_t>(sizeof(qe_opt_config)), QE_OPT_RISK_PARITY, 1.0, 0.0, 0, 0};
    double w[3] = {};
    qe_opt_result r{};
    EXPECT_EQ(qe_optimize(engine_, &rp, nullptr, cov, lower, upper, 3, w, &r), QE_E_INVALID_ARG);
}

TEST(Abi11Portfolio, RebalanceRespectsLots) {
    const qe_rebalance_config cfg{static_cast<std::int32_t>(sizeof(qe_rebalance_config)),
                                  0,
                                  100'000.0,
                                  0.0,
                                  1'000.0,
                                  39.0,
                                  0.0015};
    const qe_rebalance_asset assets[] = {
        {101.3, 0.5, 0, 10}, {57.9, 0.3, 0, 25}, {233.1, 0.2, 0, 1}};
    qe_rebalance_trade trades[3] = {};
    qe_rebalance_summary summary{};
    ASSERT_EQ(qe_rebalance(&cfg, assets, 3, trades, &summary), QE_OK);
    EXPECT_EQ(trades[0].trade_quantity % 10, 0);
    EXPECT_EQ(trades[1].trade_quantity % 25, 0);
    EXPECT_EQ(summary.feasible, 1);
    EXPECT_GE(summary.cash_after, 0.0);
    EXPECT_EQ(summary.trades, 3);
    qe_rebalance_config bad = cfg;
    bad.struct_size = 40;
    EXPECT_EQ(qe_rebalance(&bad, assets, 3, trades, &summary), QE_E_INVALID_ARG);
}

TEST(Abi11Layout, NewStructsAreIntrospectable) {
    qe_struct_layout_info info{};
    for (std::int32_t id = QE_STRUCT_BS_GREEKS; id <= QE_STRUCT_REBALANCE_SUMMARY; ++id) {
        ASSERT_EQ(qe_struct_layout(id, &info), QE_OK) << id;
        EXPECT_GT(info.size, 0);
        EXPECT_GT(info.field_count, 0);
    }
    ASSERT_EQ(qe_struct_layout(QE_STRUCT_LATTICE_INPUT, &info), QE_OK);
    EXPECT_EQ(info.offsets[1], static_cast<std::int32_t>(offsetof(qe_lattice_input, steps)));
}

} // namespace
