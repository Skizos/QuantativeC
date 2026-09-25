#include "qe/core/errors.hpp"
#include "qe/core/rng.hpp"
#include "qe/risk/stress.hpp"
#include "qe/risk/var_es.hpp"
#include "reference/phase2_reference.inc"

#include <cmath>
#include <gtest/gtest.h>
#include <vector>

namespace {

using namespace qe;
using namespace qe::risk;

// <verification_requirements>: hand-computed quantile test.
TEST(HistoricalVarEs, HandComputedHundredLosses) {
    std::vector<double> returns;
    for (int i = 1; i <= 100; ++i) {
        returns.push_back(-i / 100.0); // losses 0.01 .. 1.00
    }
    const VarEs a = historical_var_es(returns, 0.95);
    EXPECT_DOUBLE_EQ(a.var, 0.95);
    EXPECT_NEAR(a.es, 0.975, 1e-15); // mean(0.95 .. 1.00)
    EXPECT_EQ(a.observations, 100);
    const VarEs b = historical_var_es(returns, 0.99);
    EXPECT_DOUBLE_EQ(b.var, 0.99);
    EXPECT_NEAR(b.es, 0.995, 1e-15);
}

TEST(HistoricalVarEs, HandComputedFiveReturns) {
    // Losses sorted: -0.02, -0.01, 0.01, 0.03, 0.05; k = ceil(0.8 * 5) = 4.
    const std::vector<double> returns{0.02, -0.01, -0.03, 0.01, -0.05};
    const VarEs r = historical_var_es(returns, 0.8);
    EXPECT_DOUBLE_EQ(r.var, 0.03);
    EXPECT_DOUBLE_EQ(r.es, 0.04);
}

// <verification_requirements>: ES >= VaR.
TEST(HistoricalVarEs, EsIsNeverBelowVar) {
    core::Rng rng(3);
    for (int trial = 0; trial < 50; ++trial) {
        std::vector<double> returns(250);
        for (double& r : returns) {
            r = 0.01 * rng.normal() * (1.0 + 2.0 * rng.uniform_open());
        }
        for (const double c : {0.9, 0.95, 0.975, 0.99}) {
            const VarEs v = historical_var_es(returns, c);
            EXPECT_GE(v.es, v.var);
        }
    }
}

TEST(ParametricVarEs, SingleAssetClosedForm) {
    const Vector w = Vector::Ones(1);
    const Vector mu = Vector::Constant(1, 0.001);
    const Matrix cov = Matrix::Constant(1, 1, 0.02 * 0.02);
    const VarEs v = parametric_var_es(w, mu, cov, 0.99);
    EXPECT_NEAR(v.var, -0.001 + 0.02 * qe::testref::kZ99, 1e-15);
    EXPECT_NEAR(v.es, -0.001 + 0.02 * qe::testref::kEsFactor99, 1e-15);
    EXPECT_GE(v.es, v.var);
}

TEST(MonteCarloVarEs, ConvergesToParametricAndIsDeterministic) {
    Vector w(3);
    w << 0.5, 0.3, 0.2;
    Vector mu(3);
    mu << 0.0005, 0.0002, 0.0;
    Matrix cov(3, 3);
    cov << 4e-4, 1e-4, 5e-5, 1e-4, 2.25e-4, 3e-5, 5e-5, 3e-5, 1e-4;
    const VarEs exact = parametric_var_es(w, mu, cov, 0.99);
    const VarEs mc = monte_carlo_var_es(w, mu, cov, 400'000, 20260925, 0.99);
    EXPECT_NEAR(mc.var / exact.var, 1.0, 0.015);
    EXPECT_NEAR(mc.es / exact.es, 1.0, 0.02);
    EXPECT_GE(mc.es, mc.var);
    EXPECT_EQ(mc.observations, 400'000);
    EXPECT_EQ(mc.seed, 20260925U);
    const VarEs again = monte_carlo_var_es(w, mu, cov, 400'000, 20260925, 0.99);
    EXPECT_EQ(again.var, mc.var);
    EXPECT_EQ(again.es, mc.es);
}

TEST(MonteCarloVarEs, AcceptsSingularCovariance) {
    Vector w(2);
    w << 0.5, 0.5;
    const Vector mu = Vector::Zero(2);
    Matrix cov(2, 2);
    cov << 1e-4, 1e-4, 1e-4, 1e-4; // perfectly correlated
    const VarEs v = monte_carlo_var_es(w, mu, cov, 100'000, 1, 0.95);
    EXPECT_NEAR(v.var, 0.01 * 1.6448536269514722, 0.0005);
}

TEST(VarEs, RejectsBadInputs) {
    const std::vector<double> r{0.01, -0.02};
    EXPECT_THROW((void)historical_var_es(r, 0.5), InvalidArgument);
    EXPECT_THROW((void)historical_var_es(r, 1.0), InvalidArgument);
    EXPECT_THROW((void)historical_var_es({}, 0.95), InvalidArgument);
    const Vector w = Vector::Ones(2);
    Matrix not_psd(2, 2);
    not_psd << 1e-4, 2e-4, 2e-4, 1e-4;
    EXPECT_THROW((void)monte_carlo_var_es(w, Vector::Zero(2), not_psd, 1000, 1, 0.95),
                 NumericError);
    EXPECT_THROW((void)parametric_var_es(w, Vector::Zero(3), not_psd, 0.95), InvalidArgument);
}

TEST(Stress, BetasRecoverExactLinearRelationship) {
    std::vector<double> index{0.01, -0.02, 0.015, -0.005, 0.03};
    Matrix returns(5, 2);
    for (Eigen::Index t = 0; t < 5; ++t) {
        returns(t, 0) = 2.0 * index[static_cast<std::size_t>(t)] + 0.001;
        returns(t, 1) = -0.5 * index[static_cast<std::size_t>(t)];
    }
    const Vector b = betas(returns, index);
    EXPECT_NEAR(b(0), 2.0, 1e-12);
    EXPECT_NEAR(b(1), -0.5, 1e-12);
    const std::vector<double> flat(5, 0.01);
    EXPECT_THROW((void)betas(returns, flat), InvalidArgument);
}

TEST(Stress, ScenarioPnlIsValueWeightedShock) {
    Vector values(3);
    values << 100'000.0, 50'000.0, 25'000.0;
    Matrix shocks(2, 3);
    shocks << -0.10, -0.12, -0.08, // equity -10 % via betas 1.0 / 1.2 / 0.8
        0.0, 0.05, 0.0;            // SEK -5 %: the non-SEK position gains 5 % in SEK
    const Vector pnl = stress_pnl(values, shocks);
    EXPECT_DOUBLE_EQ(pnl(0), -10'000.0 - 6'000.0 - 2'000.0);
    EXPECT_DOUBLE_EQ(pnl(1), 2'500.0);
}

} // namespace
