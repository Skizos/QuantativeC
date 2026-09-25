// Black-Scholes pricing throughput: scalar C++ loop vs the batched C ABI.
// Phase 1 gate: 1e6 prices. Results: bench/results/phase1-<machine>-native.json
#include "qe/pricing/black_scholes.hpp"
#include "qe_api.h"

#include <benchmark/benchmark.h>
#include <cstddef>
#include <cstdint>
#include <stdexcept>
#include <vector>

namespace {

std::vector<qe_bs_input> make_inputs(std::size_t n) {
    std::vector<qe_bs_input> in(n);
    for (std::size_t i = 0; i < n; ++i) {
        const double s = 50.0 + static_cast<double>(i % 1000) * 0.1;
        in[i] = qe_bs_input{
            s, 100.0, 0.03, 0.01, 0.25, 0.5, (i % 2 == 0) ? QE_OPTION_CALL : QE_OPTION_PUT, 0};
    }
    return in;
}

void BM_BsScalarCpp(benchmark::State& state) {
    const auto n = static_cast<std::size_t>(state.range(0));
    const auto in = make_inputs(n);
    std::vector<double> out(n);
    for (auto _ : state) {
        for (std::size_t i = 0; i < n; ++i) {
            const qe_bs_input& x = in[i];
            out[i] = qe::pricing::bs_price(
                {.spot = x.spot,
                 .strike = x.strike,
                 .rate = x.rate,
                 .dividend_yield = x.dividend_yield,
                 .volatility = x.volatility,
                 .expiry_years = x.expiry_years,
                 .type = static_cast<qe::pricing::OptionType>(x.option_type)});
        }
        benchmark::DoNotOptimize(out.data());
        benchmark::ClobberMemory();
    }
    state.SetItemsProcessed(state.iterations() * state.range(0));
}

void BM_BsBatchAbi(benchmark::State& state) {
    const auto n = static_cast<std::size_t>(state.range(0));
    const auto in = make_inputs(n);
    std::vector<qe_bs_output> out(n);
    qe_engine* engine = nullptr;
    if (qe_engine_create(nullptr, &engine) != QE_OK) {
        state.SkipWithError("qe_engine_create failed");
        return;
    }
    std::int64_t failed = 0;
    for (auto _ : state) {
        qe_status st =
            qe_bs_price_batch(engine, in.data(), out.data(), static_cast<std::int64_t>(n), &failed);
        benchmark::DoNotOptimize(st);
        benchmark::ClobberMemory();
    }
    qe_engine_destroy(engine);
    if (failed != 0) {
        state.SkipWithError("unexpected failed elements");
    }
    state.SetItemsProcessed(state.iterations() * state.range(0));
}

} // namespace

BENCHMARK(BM_BsScalarCpp)->Arg(1'000'000)->Unit(benchmark::kMillisecond);
BENCHMARK(BM_BsBatchAbi)->Arg(1)->Arg(1'000)->Arg(1'000'000)->Unit(benchmark::kMicrosecond);

BENCHMARK_MAIN();
