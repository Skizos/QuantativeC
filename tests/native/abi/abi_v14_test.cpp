// ABI 1.4 (intraday fills and per-instrument spread, plan 17) through the shared library only
// (qe_api.h).
#include "qe_api.h"

#include <cmath>
#include <cstdint>
#include <gtest/gtest.h>
#include <limits>
#include <vector>

namespace {

qe_bt_config Config() {
    qe_bt_config c{};
    c.struct_size = static_cast<std::int32_t>(sizeof(qe_bt_config));
    c.initial_cash = 100'000.0;
    c.half_spread_bps = 5.0;
    c.slippage_bps = 2.0;
    c.participation_cap = 1.0;
    return c;
}

qe_bt_bar Bar(double open, double high, double low, double close) {
    return qe_bt_bar{open, high, low, close, 1e6, 1, 0};
}

class Abi14 : public ::testing::Test {
  protected:
    void SetUp() override {
        const std::vector<qe_bt_instrument> instruments{{1, 0, 0}, {1, 0, 0}};
        const qe_bt_config cfg = Config();
        ASSERT_EQ(qe_bt_create(&cfg, instruments.data(), 2, &bt_), QE_OK);
    }
    void TearDown() override { EXPECT_EQ(qe_bt_destroy(bt_), QE_OK); }

    qe_status Step(const std::vector<qe_bt_bar>& bars, const std::vector<qe_bt_order>& orders) {
        fills_.assign(orders.size() + 1, qe_bt_fill{});
        return qe_bt_step(bt_, bars.data(), static_cast<std::int64_t>(bars.size()), orders.data(),
                          static_cast<std::int64_t>(orders.size()), fills_.data(),
                          static_cast<std::int64_t>(fills_.size()), &fill_count_, &state_);
    }

    qe_backtest* bt_{nullptr};
    std::vector<qe_bt_fill> fills_;
    std::int64_t fill_count_{-1};
    qe_bt_state state_{};
};

qe_bt_order Limit(std::int32_t instrument, std::int32_t side, std::int64_t quantity, double limit) {
    return qe_bt_order{instrument, side, QE_BT_LIMIT, 0, quantity, limit};
}

} // namespace

TEST(Abi14Version, MinorIsFour) {
    std::int32_t major = 0;
    std::int32_t minor = 0;
    ASSERT_EQ(qe_abi_version(&major, &minor), QE_OK);
    EXPECT_EQ(major, 1);
    EXPECT_EQ(minor, 4);
}

TEST_F(Abi14, Daily_ALimitMarketableAtTheOpen_FillsAtTheOpen) {
    // Unchanged behaviour: the open is the opening auction.
    ASSERT_EQ(Step({Bar(99.0, 101.0, 98.0, 100.0), Bar(50, 50, 50, 50)},
                   {Limit(0, QE_BT_BUY, 10, 100.0)}),
              QE_OK);
    ASSERT_EQ(fill_count_, 1);
    EXPECT_DOUBLE_EQ(fills_[0].price, 99.0);
}

TEST_F(Abi14, Intraday_ALimitFillsOnlyAtItsLimit_OnATradeThrough_NeverAtABetterOpen) {
    ASSERT_EQ(qe_bt_set_fill_mode(bt_, QE_BT_FILL_INTRADAY), QE_OK);
    // Buy 100.00: the bar opens at 99.00 (below the limit) and trades down to 98.00: filled at
    // 100.00, not 99.00. Sell 50.00 on the other share: its bar trades up to 50.50: filled
    // at 50.00.
    ASSERT_EQ(Step({Bar(99.0, 101.0, 98.0, 100.0), Bar(49.8, 50.5, 49.7, 50.2)},
                   {Limit(0, QE_BT_BUY, 10, 100.0), Limit(1, QE_BT_SELL, 5, 50.0)}),
              QE_OK);
    ASSERT_EQ(fill_count_, 1); // the sell has nothing to sell yet
    EXPECT_DOUBLE_EQ(fills_[0].price, 100.0);
    EXPECT_DOUBLE_EQ(fills_[0].spread_slippage_cost, 0.0);
}

TEST_F(Abi14, Intraday_ATouchIsNoFill) {
    ASSERT_EQ(qe_bt_set_fill_mode(bt_, QE_BT_FILL_INTRADAY), QE_OK);
    ASSERT_EQ(Step({Bar(100.5, 101.0, 100.0, 100.5), Bar(50, 50, 50, 50)},
                   {Limit(0, QE_BT_BUY, 10, 100.0)}),
              QE_OK);
    EXPECT_EQ(fill_count_, 0); // the low only touched 100.00

    ASSERT_EQ(Step({Bar(100.5, 101.0, 99.9, 100.5), Bar(50, 50, 50, 50)},
                   {Limit(0, QE_BT_BUY, 10, 100.0)}),
              QE_OK);
    ASSERT_EQ(fill_count_, 1); // one tick through
    EXPECT_DOUBLE_EQ(fills_[0].price, 100.0);

    // Sell side: a touch at the high is no fill, a print above is.
    ASSERT_EQ(Step({Bar(100.0, 100.5, 99.5, 100.0), Bar(50, 50, 50, 50)},
                   {Limit(0, QE_BT_SELL, 10, 100.5)}),
              QE_OK);
    EXPECT_EQ(fill_count_, 0);
    ASSERT_EQ(Step({Bar(100.0, 100.6, 99.5, 100.0), Bar(50, 50, 50, 50)},
                   {Limit(0, QE_BT_SELL, 10, 100.5)}),
              QE_OK);
    ASSERT_EQ(fill_count_, 1);
    EXPECT_DOUBLE_EQ(fills_[0].price, 100.5);
}

TEST_F(Abi14, Intraday_MarketOrdersStillFillAtTheOpenAndClose_WithSpreadAndSlippage) {
    ASSERT_EQ(qe_bt_set_fill_mode(bt_, QE_BT_FILL_INTRADAY), QE_OK);
    ASSERT_EQ(Step({Bar(100.0, 101.0, 99.0, 100.0), Bar(50, 50, 50, 50)},
                   {{0, QE_BT_BUY, QE_BT_MARKET_ON_OPEN, 0, 10, 0.0}}),
              QE_OK);
    ASSERT_EQ(fill_count_, 1);
    EXPECT_NEAR(fills_[0].price, 100.0 * (1 + 7e-4), 1e-9); // 5 bps half-spread + 2 bps slippage
}

TEST_F(Abi14, AnInstrumentsOwnHalfSpread_ReplacesTheConfigs) {
    ASSERT_EQ(qe_bt_set_half_spread(bt_, 1, 20.0), QE_OK);
    ASSERT_EQ(Step({Bar(100.0, 101.0, 99.0, 100.0), Bar(50, 51, 49, 50)},
                   {{0, QE_BT_BUY, QE_BT_MARKET_ON_OPEN, 0, 10, 0.0},
                    {1, QE_BT_BUY, QE_BT_MARKET_ON_OPEN, 0, 10, 0.0}}),
              QE_OK);
    ASSERT_EQ(fill_count_, 2);
    EXPECT_NEAR(fills_[0].price, 100.0 * (1 + 7e-4), 1e-9); // the config's 5 + 2 bps
    EXPECT_NEAR(fills_[1].price, 50.0 * (1 + 22e-4), 1e-9); // its own 20 + 2 bps
    EXPECT_NEAR(fills_[1].spread_slippage_cost, 10 * 50.0 * 22e-4, 1e-9);
}

TEST_F(Abi14, BadInputIsRefused_AndOnlyBeforeTheFirstStep) {
    const double nan = std::numeric_limits<double>::quiet_NaN();
    EXPECT_EQ(qe_bt_set_fill_mode(nullptr, QE_BT_FILL_INTRADAY), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_fill_mode(bt_, 2), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_fill_mode(bt_, -1), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_half_spread(nullptr, 0, 5.0), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_half_spread(bt_, 2, 5.0), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_half_spread(bt_, -1, 5.0), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_half_spread(bt_, 0, -0.1), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_half_spread(bt_, 0, 1000.0), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_half_spread(bt_, 0, nan), QE_E_INVALID_ARG);

    // Nothing changed: still daily fills (at the better open) and the config's spread.
    ASSERT_EQ(
        Step({Bar(99.0, 101.0, 98.0, 100.0), Bar(50, 51, 49, 50)},
             {Limit(0, QE_BT_BUY, 10, 100.0), {1, QE_BT_BUY, QE_BT_MARKET_ON_OPEN, 0, 10, 0.0}}),
        QE_OK);
    ASSERT_EQ(fill_count_, 2);
    EXPECT_DOUBLE_EQ(fills_[0].price, 99.0);
    EXPECT_NEAR(fills_[1].price, 50.0 * (1 + 7e-4), 1e-9);

    EXPECT_EQ(qe_bt_set_fill_mode(bt_, QE_BT_FILL_INTRADAY), QE_E_INVALID_ARG); // too late
    EXPECT_EQ(qe_bt_set_half_spread(bt_, 0, 5.0), QE_E_INVALID_ARG);
}
