// ABI 1.3 (per-instrument courtage, ADR 0005) through the shared library only (qe_api.h).
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
    c.courtage_min = 0.0; // Avanza Start on Nasdaq Stockholm: free
    c.courtage_rate = 0.0;
    c.fx_fee_rate = 0.0;
    c.participation_cap = 1.0;
    return c;
}

qe_bt_bar Bar(double open, double high, double low, double close) {
    return qe_bt_bar{open, high, low, close, 1e6, 1, 0};
}

class Abi13 : public ::testing::Test {
  protected:
    void SetUp() override {
        const std::vector<qe_bt_instrument> instruments{{1, 0, 0}, {1, 1, 0}};
        const qe_bt_config cfg = Config();
        ASSERT_EQ(qe_bt_create(&cfg, instruments.data(), 2, &bt_), QE_OK);
    }
    void TearDown() override { EXPECT_EQ(qe_bt_destroy(bt_), QE_OK); }

    qe_status Step(const std::vector<qe_bt_order>& orders) {
        const std::vector<qe_bt_bar> bars{Bar(100, 102, 98, 101), Bar(50, 51, 49, 50)};
        fills_.assign(orders.size(), qe_bt_fill{});
        return qe_bt_step(bt_, bars.data(), 2, orders.data(), static_cast<std::int64_t>(orders.size()), fills_.data(),
                          static_cast<std::int64_t>(fills_.size()), &fill_count_, &state_);
    }

    qe_backtest* bt_{nullptr};
    std::vector<qe_bt_fill> fills_;
    std::int64_t fill_count_{-1};
    qe_bt_state state_{};
};

} // namespace

TEST(Abi13Version, MinorIsAtLeastThree) {
    std::int32_t major = 0;
    std::int32_t minor = 0;
    ASSERT_EQ(qe_abi_version(&major, &minor), QE_OK);
    EXPECT_EQ(major, 1);
    EXPECT_GE(minor, 3);
}

TEST_F(Abi13, AForeignShareWithItsOwnCourtage_PaysIt_TheOthersPayTheConfigs) {
    // Instrument 1 (a US share, prices in SEK): 0.25 %, minimum 9.40 SEK (1 USD at 9.40).
    ASSERT_EQ(qe_bt_set_courtage(bt_, 1, 9.40, 0.0025), QE_OK);
    ASSERT_EQ(Step({{0, QE_BT_BUY, QE_BT_MARKET_ON_OPEN, 0, 10, 0.0},
                    {1, QE_BT_BUY, QE_BT_MARKET_ON_OPEN, 0, 10, 0.0},
                    {1, QE_BT_BUY, QE_BT_MARKET_ON_OPEN, 0, 100, 0.0}}),
              QE_OK);
    ASSERT_EQ(fill_count_, 3);
    EXPECT_DOUBLE_EQ(fills_[0].courtage, 0.0);                   // the config's: free
    EXPECT_DOUBLE_EQ(fills_[1].courtage, 9.40);                  // 500 SEK: the minimum
    EXPECT_DOUBLE_EQ(fills_[2].courtage, 0.0025 * 100 * 50.0);   // 5 000 SEK: 0.25 % = 12.50
    EXPECT_NEAR(state_.courtage, 9.40 + 12.50, 1e-9);
}

TEST_F(Abi13, TheCashCheckCountsTheInstrumentsOwnCourtage) {
    qe_bt_destroy(bt_);
    qe_bt_config cfg = Config();
    cfg.initial_cash = 1'000.0;
    const std::vector<qe_bt_instrument> one{{1, 1, 0}};
    ASSERT_EQ(qe_bt_create(&cfg, one.data(), 1, &bt_), QE_OK);
    ASSERT_EQ(qe_bt_set_courtage(bt_, 0, 100.0, 0.0), QE_OK); // a 100 SEK minimum
    const std::vector<qe_bt_bar> bars{Bar(50, 51, 49, 50)};
    const std::vector<qe_bt_order> orders{{0, QE_BT_BUY, QE_BT_MARKET_ON_OPEN, 0, 20, 0.0}};
    fills_.assign(1, qe_bt_fill{});
    ASSERT_EQ(qe_bt_step(bt_, bars.data(), 1, orders.data(), 1, fills_.data(), 1, &fill_count_, &state_), QE_OK);
    ASSERT_EQ(fill_count_, 1);
    EXPECT_EQ(fills_[0].quantity, 18); // 18 * 50 + 100 = 1 000; 19 would not fit
    EXPECT_GE(state_.cash, 0.0);
}

TEST_F(Abi13, BadInputIsRefused_AndOnlyBeforeTheFirstStep) {
    const double nan = std::numeric_limits<double>::quiet_NaN();
    EXPECT_EQ(qe_bt_set_courtage(nullptr, 0, 1.0, 0.001), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_courtage(bt_, 2, 1.0, 0.001), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_courtage(bt_, -1, 1.0, 0.001), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_courtage(bt_, 1, -1.0, 0.001), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_courtage(bt_, 1, nan, 0.001), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_courtage(bt_, 1, 1.0, 0.1), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_set_courtage(bt_, 1, 1.0, -0.001), QE_E_INVALID_ARG);

    // A refused call changed nothing: the foreign share still pays the config's (free) courtage.
    ASSERT_EQ(Step({{1, QE_BT_BUY, QE_BT_MARKET_ON_OPEN, 0, 10, 0.0}}), QE_OK);
    ASSERT_EQ(fill_count_, 1);
    EXPECT_DOUBLE_EQ(fills_[0].courtage, 0.0);

    EXPECT_EQ(qe_bt_set_courtage(bt_, 1, 1.0, 0.001), QE_E_INVALID_ARG); // too late
}
