// Phase 2 benchmarks: 1e5-path Monte Carlo (spec <verification_requirements>) plus risk/portfolio
// kernels sized for the later 300-instrument universe.
#include "qe/portfolio/hrp.hpp"
#include "qe/portfolio/optimizers.hpp"
#include "qe/portfolio/rebalance.hpp"
#include "qe/pricing/monte_carlo.hpp"
#include "qe/risk/covariance.hpp"
#include "qe/risk/var_es.hpp"

#include <benchmark/benchmark.h>
#include <random>
#include <vector>

namespace {

using qe::Matrix;
using qe::Vector;

// Two-factor synthetic daily returns, T x N, deterministic.
Matrix synthetic_returns(Eigen::Index t, Eigen::Index n) {
    std::mt19937_64 rng(20260925);
    std::normal_distribution<double> z(0.0, 1.0);
    Matrix f(t, 2);
    for (Eigen::Index i = 0; i < t; ++i) {
        f(i, 0) = 0.01 * z(rng);
        f(i, 1) = 0.006 * z(rng);
    }
    Matrix x(t, n);
    for (Eigen::Index j = 0; j < n; ++j) {
        const double b0 = 0.5 + 0.01 * static_cast<double>(j % 100);
        const double b1 = (j % 2 == 0) ? 0.8 : -0.3;
        for (Eigen::Index i = 0; i < t; ++i) {
            x(i, j) = b0 * f(i, 0) + b1 * f(i, 1) + 0.012 * z(rng);
        }
    }
    return x;
}

void BM_MonteCarlo1e5(benchmark::State& state) {
    const qe::pricing::BsParams p{.spot = 100,
                                  .strike = 100,
                                  .rate = 0.05,
                                  .dividend_yield = 0.0,
                                  .volatility = 0.2,
                                  .expiry_years = 1.0,
                                  .type = qe::pricing::OptionType::Call};
    qe::pricing::McConfig c;
    c.paths = 100'000;
    c.seed = 1;
    c.antithetic = state.range(0) == 1;
    c.control_variate = state.range(0) == 1;
    c.sobol = state.range(0) == 2;
    // DoNotOptimize on the whole result struct (memory constraint): with GCC and benchmark 1.8.3,
    // DoNotOptimize on a lone double lvalue was observed to clobber the reported value.
    qe::pricing::McResult last{};
    for (auto _ : state) {
        last = qe::pricing::mc_european(p, c);
        benchmark::DoNotOptimize(last);
    }
    state.counters["std_error"] = last.std_error;
    state.SetItemsProcessed(state.iterations() * c.paths);
    state.SetLabel(state.range(0) == 0   ? "plain"
                   : state.range(0) == 1 ? "antithetic+control"
                                         : "sobol-rqmc");
}

void BM_LedoitWolf(benchmark::State& state) {
    const Matrix x = synthetic_returns(2520, state.range(0)); // 10 years of daily returns
    for (auto _ : state) {
        auto r = qe::risk::ledoit_wolf(x);
        benchmark::DoNotOptimize(r.covariance.data());
    }
}

void BM_MinVariance(benchmark::State& state) {
    const Eigen::Index n = state.range(0);
    const Matrix cov = qe::risk::ledoit_wolf(synthetic_returns(2520, n)).covariance;
    const Vector lo = Vector::Zero(n);
    const Vector hi = Vector::Constant(n, 0.1);
    int iterations = 0;
    for (auto _ : state) {
        auto r = qe::portfolio::min_variance(cov, lo, hi,
                                             {.max_iterations = 100'000, .tolerance = 1e-10});
        iterations = r.iterations;
        benchmark::DoNotOptimize(r.weights.data());
    }
    state.counters["solver_iterations"] = iterations;
}

void BM_Hrp(benchmark::State& state) {
    const Matrix cov = qe::risk::ledoit_wolf(synthetic_returns(2520, state.range(0))).covariance;
    for (auto _ : state) {
        auto r = qe::portfolio::hrp(cov);
        benchmark::DoNotOptimize(r.weights.data());
    }
}

void BM_MonteCarloVar(benchmark::State& state) {
    const Eigen::Index n = 30;
    const Matrix cov = qe::risk::ledoit_wolf(synthetic_returns(2520, n)).covariance;
    const Vector w = Vector::Constant(n, 1.0 / static_cast<double>(n));
    const Vector mu = Vector::Zero(n);
    for (auto _ : state) {
        auto r = qe::risk::monte_carlo_var_es(w, mu, cov, 100'000, 1, 0.99);
        benchmark::DoNotOptimize(r);
    }
}

void BM_Rebalance(benchmark::State& state) {
    std::vector<qe::portfolio::RebalanceAsset> assets;
    for (int i = 0; i < 30; ++i) {
        assets.push_back({.price = 20.0 + 13.7 * i,
                          .target_weight = 1.0 / 30.0,
                          .current_quantity = i * 3,
                          .lot_size = 1});
    }
    const qe::portfolio::RebalanceConfig cfg{.cash = 250'000,
                                             .cash_buffer = 1'000,
                                             .min_trade_value = 1'000,
                                             .fee_min = 39,
                                             .fee_rate = 0.0015};
    for (auto _ : state) {
        auto r = qe::portfolio::rebalance(assets, cfg);
        benchmark::DoNotOptimize(r);
    }
}

} // namespace

BENCHMARK(BM_MonteCarlo1e5)->Arg(0)->Arg(1)->Arg(2)->Unit(benchmark::kMillisecond);
BENCHMARK(BM_LedoitWolf)->Arg(30)->Arg(300)->Unit(benchmark::kMillisecond);
BENCHMARK(BM_MinVariance)->Arg(30)->Arg(300)->Unit(benchmark::kMillisecond);
BENCHMARK(BM_Hrp)->Arg(30)->Arg(300)->Unit(benchmark::kMillisecond);
BENCHMARK(BM_MonteCarloVar)->Unit(benchmark::kMillisecond);
BENCHMARK(BM_Rebalance)->Unit(benchmark::kMicrosecond);
