// Proves qe::backtest::Engine::step does not allocate. This binary replaces the global operator
// new with a counting one, so it is separate from qe_unit_tests and not built with sanitizers
// (ASan intercepts the allocator itself).

#include "qe/backtest/engine.hpp"

#include <atomic>
#include <cstdlib>
#include <gtest/gtest.h>
#include <new>
#include <vector>

namespace {
std::atomic<long> g_allocations{0};
thread_local bool g_counting = false;
} // namespace

void* operator new(std::size_t size) {
    if (g_counting) {
        g_allocations.fetch_add(1, std::memory_order_relaxed);
    }
    if (void* p = std::malloc(size == 0 ? 1 : size)) {
        return p;
    }
    throw std::bad_alloc();
}
void* operator new[](std::size_t size) {
    return ::operator new(size);
}
void* operator new(std::size_t size, const std::nothrow_t&) noexcept {
    try {
        return ::operator new(size);
    } catch (...) {
        return nullptr;
    }
}
void* operator new[](std::size_t size, const std::nothrow_t&) noexcept {
    return ::operator new(size, std::nothrow);
}
void operator delete(void* p) noexcept {
    std::free(p);
}
void operator delete[](void* p) noexcept {
    std::free(p);
}
void operator delete(void* p, std::size_t) noexcept {
    std::free(p);
}
void operator delete[](void* p, std::size_t) noexcept {
    std::free(p);
}
void operator delete(void* p, const std::nothrow_t&) noexcept {
    std::free(p);
}
void operator delete[](void* p, const std::nothrow_t&) noexcept {
    std::free(p);
}

namespace {

using namespace qe::backtest;

TEST(BacktestEngineAllocations, StepDoesNotAllocate) {
    Config config;
    config.initial_cash = 1e12; // enough cash that every order fills
    config.courtage_min = 39.0;
    config.courtage_rate = 0.0015;
    config.fx_fee_rate = 0.0025;
    config.slippage_bps = 5.0;
    config.half_spread_bps = 5.0;
    config.participation_cap = 0.10;
    std::vector<Instrument> instruments(300, Instrument{1, false});
    Engine engine(config, instruments);
    std::vector<Bar> bars(300, Bar{100, 102, 98, 101, 1'000'000.0, true});
    std::vector<Order> orders;
    for (int i = 0; i < 300; ++i) {
        orders.push_back(
            Order{i, 1, i % 2 == 0 ? OrderType::MarketOnOpen : OrderType::Limit, 10, 99.5});
    }
    std::vector<Fill> fills(orders.size());
    State state;
    g_allocations = 0;
    g_counting = true;
    for (int t = 0; t < 50; ++t) {
        engine.step(bars, orders, fills, state);
    }
    g_counting = false;
    EXPECT_EQ(g_allocations.load(), 0);
    EXPECT_EQ(state.fills, 50 * 300);
}

} // namespace
