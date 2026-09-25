#include "qe/core/errors.hpp"
#include "qe/portfolio/rebalance.hpp"

#include <cmath>
#include <gtest/gtest.h>
#include <vector>

namespace {

using namespace qe::portfolio;

// Avanza "Small" courtage shape (UNVERIFIED figures; docs/research/market-rules.md): max(39, 0.15
// %).
RebalanceConfig small_class(double cash) {
    return {.cash = cash,
            .cash_buffer = 0.0,
            .min_trade_value = 1'000.0,
            .fee_min = 39.0,
            .fee_rate = 0.0015};
}

void expect_invariants(const std::vector<RebalanceAsset>& assets, const RebalanceConfig& cfg,
                       const RebalanceResult& r) {
    ASSERT_EQ(r.trades.size(), assets.size());
    double cash = cfg.cash;
    double fees = 0.0;
    for (std::size_t i = 0; i < assets.size(); ++i) {
        const auto& t = r.trades[i];
        // <verification_requirements>: rebalance solver respects integer lots.
        EXPECT_EQ(t.trade_quantity % assets[i].lot_size, 0) << i;
        EXPECT_GE(t.target_quantity, 0) << i;
        EXPECT_EQ(t.target_quantity, assets[i].current_quantity + t.trade_quantity);
        EXPECT_DOUBLE_EQ(t.trade_value, assets[i].price * static_cast<double>(t.trade_quantity));
        if (t.trade_quantity != 0) {
            EXPECT_GE(std::abs(t.trade_value), cfg.min_trade_value) << i;
            EXPECT_DOUBLE_EQ(t.fee, std::max(cfg.fee_min, cfg.fee_rate * std::abs(t.trade_value)));
        } else {
            EXPECT_EQ(t.fee, 0.0);
        }
        cash -= t.trade_value + t.fee;
        fees += t.fee;
    }
    EXPECT_NEAR(r.summary.cash_after, cash, 1e-6);
    EXPECT_NEAR(r.summary.total_fees, fees, 1e-9);
    if (r.summary.feasible) {
        EXPECT_GE(r.summary.cash_after, cfg.cash_buffer);
    }
}

TEST(Rebalance, InvestsCashCloseToTargetsWithoutOverspending) {
    const std::vector<RebalanceAsset> assets{
        {.price = 101.3, .target_weight = 0.5, .current_quantity = 0, .lot_size = 1},
        {.price = 57.9, .target_weight = 0.3, .current_quantity = 0, .lot_size = 1},
        {.price = 233.1, .target_weight = 0.2, .current_quantity = 0, .lot_size = 1},
    };
    const auto cfg = small_class(100'000.0);
    const auto r = rebalance(assets, cfg);
    expect_invariants(assets, cfg, r);
    EXPECT_TRUE(r.summary.feasible);
    EXPECT_EQ(r.summary.trades, 3);
    EXPECT_LT(r.summary.tracking_error, 0.005);
    for (std::size_t i = 0; i < assets.size(); ++i) {
        EXPECT_NEAR(r.trades[i].final_weight, assets[i].target_weight, 0.005) << i;
    }
}

TEST(Rebalance, TradesAreMultiplesOfLargeLots) {
    const std::vector<RebalanceAsset> assets{
        {.price = 12.37, .target_weight = 0.6, .current_quantity = 300, .lot_size = 100},
        {.price = 48.10, .target_weight = 0.4, .current_quantity = 0, .lot_size = 50},
    };
    const auto cfg = small_class(50'000.0);
    const auto r = rebalance(assets, cfg);
    expect_invariants(assets, cfg, r);
    EXPECT_EQ(r.trades[0].target_quantity % 100, 0);
    EXPECT_EQ(r.trades[1].target_quantity % 50, 0);
}

TEST(Rebalance, SellsFundBuys) {
    const std::vector<RebalanceAsset> assets{
        {.price = 200.0,
         .target_weight = 0.2,
         .current_quantity = 400,
         .lot_size = 1}, // 80k, target 20 %
        {.price = 50.0, .target_weight = 0.8, .current_quantity = 0, .lot_size = 1},
    };
    const auto cfg = small_class(0.0);
    const auto r = rebalance(assets, cfg);
    expect_invariants(assets, cfg, r);
    EXPECT_LT(r.trades[0].trade_quantity, 0);
    EXPECT_GT(r.trades[1].trade_quantity, 0);
    EXPECT_TRUE(r.summary.feasible);
    EXPECT_GE(r.summary.cash_after, 0.0);
    EXPECT_NEAR(r.trades[1].final_weight, 0.8, 0.01);
}

TEST(Rebalance, NoTradesWhenAlreadyOnTargetOrBelowThreshold) {
    const std::vector<RebalanceAsset> assets{
        {.price = 100.0, .target_weight = 0.5, .current_quantity = 500, .lot_size = 1},
        {.price = 100.0,
         .target_weight = 0.5,
         .current_quantity = 495,
         .lot_size = 1}, // 500 SEK below target
    };
    const auto cfg = small_class(500.0);
    const auto r = rebalance(assets, cfg);
    expect_invariants(assets, cfg, r);
    EXPECT_EQ(r.summary.trades, 0); // the 500 SEK buy is under the 1,000 SEK minimum
}

TEST(Rebalance, KeepsTheCashBufferOrReportsInfeasible) {
    const std::vector<RebalanceAsset> assets{
        {.price = 100.0, .target_weight = 1.0, .current_quantity = 0, .lot_size = 1},
    };
    RebalanceConfig cfg = small_class(20'000.0);
    cfg.cash_buffer = 5'000.0;
    const auto r = rebalance(assets, cfg);
    expect_invariants(assets, cfg, r);
    EXPECT_TRUE(r.summary.feasible);
    EXPECT_GE(r.summary.cash_after, 5'000.0);

    cfg.cash = 100.0; // below the buffer with nothing to sell
    const auto infeasible = rebalance(assets, cfg);
    EXPECT_FALSE(infeasible.summary.feasible);
    EXPECT_EQ(infeasible.summary.trades, 0);
}

TEST(Rebalance, IsDeterministic) {
    const std::vector<RebalanceAsset> assets{
        {.price = 33.3, .target_weight = 0.25, .current_quantity = 10, .lot_size = 1},
        {.price = 66.6, .target_weight = 0.25, .current_quantity = 0, .lot_size = 1},
        {.price = 99.9, .target_weight = 0.25, .current_quantity = 5, .lot_size = 1},
        {.price = 133.2, .target_weight = 0.25, .current_quantity = 0, .lot_size = 1},
    };
    const auto cfg = small_class(40'000.0);
    const auto a = rebalance(assets, cfg);
    const auto b = rebalance(assets, cfg);
    for (std::size_t i = 0; i < assets.size(); ++i) {
        EXPECT_EQ(a.trades[i].trade_quantity, b.trades[i].trade_quantity);
    }
}

TEST(Rebalance, RejectsInvalidInput) {
    std::vector<RebalanceAsset> assets{
        {.price = 10.0, .target_weight = 0.7, .current_quantity = 0, .lot_size = 1},
        {.price = 10.0, .target_weight = 0.7, .current_quantity = 0, .lot_size = 1}};
    EXPECT_THROW((void)rebalance(assets, small_class(1000.0)), qe::InvalidArgument); // weights > 1
    assets[1].target_weight = 0.3;
    assets[0].lot_size = 0;
    EXPECT_THROW((void)rebalance(assets, small_class(1000.0)), qe::InvalidArgument);
    assets[0].lot_size = 1;
    assets[0].price = -1.0;
    EXPECT_THROW((void)rebalance(assets, small_class(1000.0)), qe::InvalidArgument);
    EXPECT_THROW((void)rebalance({}, small_class(1000.0)), qe::InvalidArgument);
}

} // namespace
