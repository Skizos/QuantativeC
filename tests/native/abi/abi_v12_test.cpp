// ABI 1.2 (backtest) through the shared library only (qe_api.h), as the .NET side sees it.
#include "qe_api.h"

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <gtest/gtest.h>
#include <string>
#include <vector>

namespace {

qe_bt_config Config(double cash = 100'000.0) {
    qe_bt_config c{};
    c.struct_size = static_cast<std::int32_t>(sizeof(qe_bt_config));
    c.initial_cash = cash;
    c.courtage_min = 39.0;
    c.courtage_rate = 0.0015;
    c.fx_fee_rate = 0.0025;
    c.slippage_bps = 5.0;
    c.half_spread_bps = 5.0;
    c.participation_cap = 0.10;
    return c;
}

qe_bt_bar Bar(double open, double high, double low, double close, double volume = 1e6) {
    return qe_bt_bar{open, high, low, close, volume, 1, 0};
}

std::string LastError() {
    std::int32_t required = 0;
    qe_last_error(nullptr, 0, &required);
    std::string s(static_cast<std::size_t>(required), '\0');
    qe_last_error(s.data(), required, &required);
    s.resize(std::strlen(s.c_str()));
    return s;
}

class Abi12 : public ::testing::Test {
  protected:
    void SetUp() override {
        const std::vector<qe_bt_instrument> instruments{{1, 0, 0}, {10, 1, 0}};
        const qe_bt_config cfg = Config();
        ASSERT_EQ(qe_bt_create(&cfg, instruments.data(), 2, &bt_), QE_OK) << LastError();
    }
    void TearDown() override { EXPECT_EQ(qe_bt_destroy(bt_), QE_OK); }

    qe_status Step(const std::vector<qe_bt_bar>& bars, const std::vector<qe_bt_order>& orders) {
        fills_.assign(orders.size(), qe_bt_fill{});
        return qe_bt_step(bt_, bars.data(), static_cast<std::int64_t>(bars.size()),
                          orders.empty() ? nullptr : orders.data(),
                          static_cast<std::int64_t>(orders.size()),
                          fills_.empty() ? nullptr : fills_.data(),
                          static_cast<std::int64_t>(fills_.size()), &fill_count_, &state_);
    }

    qe_backtest* bt_{nullptr};
    std::vector<qe_bt_fill> fills_;
    std::int64_t fill_count_{-1};
    qe_bt_state state_{};
};

TEST(Abi12Version, MinorIsAtLeastTwo) {
    std::int32_t major = 0;
    std::int32_t minor = 0;
    ASSERT_EQ(qe_abi_version(&major, &minor), QE_OK);
    EXPECT_EQ(major, 1);
    EXPECT_GE(minor, 2); // 1.3 added per-instrument courtage without changing 1.2
}

TEST(Abi12Layout, StructLayoutsMatchTheHeader) {
    qe_struct_layout_info info{};
    ASSERT_EQ(qe_struct_layout(QE_STRUCT_BT_CONFIG, &info), QE_OK);
    EXPECT_EQ(info.size, 64);
    EXPECT_EQ(info.field_count, 9);
    EXPECT_EQ(info.offsets[8],
              static_cast<std::int32_t>(offsetof(qe_bt_config, participation_cap)));
    ASSERT_EQ(qe_struct_layout(QE_STRUCT_BT_INSTRUMENT, &info), QE_OK);
    EXPECT_EQ(info.size, 16);
    EXPECT_EQ(info.offsets[1], 8);
    ASSERT_EQ(qe_struct_layout(QE_STRUCT_BT_BAR, &info), QE_OK);
    EXPECT_EQ(info.size, 48);
    EXPECT_EQ(info.offsets[5], 40);
    ASSERT_EQ(qe_struct_layout(QE_STRUCT_BT_ORDER, &info), QE_OK);
    EXPECT_EQ(info.size, 32);
    EXPECT_EQ(info.offsets[4], 16);
    EXPECT_EQ(info.offsets[5], 24);
    ASSERT_EQ(qe_struct_layout(QE_STRUCT_BT_FILL, &info), QE_OK);
    EXPECT_EQ(info.size, 56);
    EXPECT_EQ(info.offsets[3], 12);
    EXPECT_EQ(info.offsets[8], 48);
    ASSERT_EQ(qe_struct_layout(QE_STRUCT_BT_STATE, &info), QE_OK);
    EXPECT_EQ(info.size, 64);
    EXPECT_EQ(info.offsets[7], 56);
    for (std::int32_t id = QE_STRUCT_BT_CONFIG; id <= QE_STRUCT_BT_STATE; ++id) {
        ASSERT_EQ(qe_struct_layout(id, &info), QE_OK) << id;
        EXPECT_EQ(info.alignment, 8) << id;
    }
}

TEST_F(Abi12, FillsCostsAndStateCrossTheBoundary) {
    // Instrument 0: SEK, lot 1. Instrument 1: foreign currency, lot 10.
    ASSERT_EQ(Step({Bar(100, 102, 98, 101), Bar(50, 51, 49, 50)},
                   {{0, QE_BT_BUY, QE_BT_MARKET_ON_OPEN, 0, 100, 0.0},
                    {1, QE_BT_BUY, QE_BT_LIMIT, 0, 20, 49.5},
                    {0, QE_BT_BUY, QE_BT_LIMIT, 0, 10, 98.0}}),
              QE_OK)
        << LastError();
    ASSERT_EQ(fill_count_, 2) << "the limit at the low was only touched";

    const qe_bt_fill& moo = fills_[0];
    EXPECT_EQ(moo.type, QE_BT_MARKET_ON_OPEN);
    EXPECT_EQ(moo.order_index, 0);
    EXPECT_EQ(moo.quantity, 100);
    EXPECT_DOUBLE_EQ(moo.price, 100.0 * 1.001);
    EXPECT_DOUBLE_EQ(moo.courtage, 39.0);
    EXPECT_DOUBLE_EQ(moo.fx_fee, 0.0);
    EXPECT_NEAR(moo.spread_slippage_cost, 10.0, 1e-9);

    const qe_bt_fill& limit = fills_[1];
    EXPECT_EQ(limit.instrument, 1);
    EXPECT_EQ(limit.type, QE_BT_LIMIT);
    EXPECT_EQ(limit.order_index, 1);
    EXPECT_DOUBLE_EQ(limit.price, 49.5);
    EXPECT_DOUBLE_EQ(limit.fx_fee, 0.0025 * 20 * 49.5);

    const double spent = 100 * 100.1 + 39.0 + 20 * 49.5 + 39.0 + 0.0025 * 20 * 49.5;
    EXPECT_NEAR(state_.cash, 100'000.0 - spent, 1e-9);
    EXPECT_NEAR(state_.equity, state_.cash + 100 * 101.0 + 20 * 50.0, 1e-9);
    EXPECT_EQ(state_.fills, 2);
    EXPECT_EQ(state_.orders, 3);

    std::vector<std::int64_t> positions(2, -1);
    ASSERT_EQ(qe_bt_positions(bt_, positions.data(), 2), QE_OK);
    EXPECT_EQ(positions, (std::vector<std::int64_t>{100, 20}));
}

TEST_F(Abi12, NoOrdersIsAValidStep) {
    ASSERT_EQ(Step({Bar(100, 102, 98, 101), Bar(50, 51, 49, 50)}, {}), QE_OK) << LastError();
    EXPECT_EQ(fill_count_, 0);
    EXPECT_DOUBLE_EQ(state_.equity, 100'000.0);
}

TEST_F(Abi12, SmallFillBufferReportsTheRequiredSize) {
    const std::vector<qe_bt_bar> bars{Bar(100, 102, 98, 101), Bar(50, 51, 49, 50)};
    const std::vector<qe_bt_order> orders{{0, QE_BT_BUY, QE_BT_MARKET_ON_OPEN, 0, 1, 0.0},
                                          {0, QE_BT_BUY, QE_BT_MARKET_ON_CLOSE, 0, 1, 0.0}};
    qe_bt_fill one{};
    std::int64_t count = -1;
    qe_bt_state state{};
    EXPECT_EQ(qe_bt_step(bt_, bars.data(), 2, orders.data(), 2, &one, 1, &count, &state),
              QE_E_BUFFER_TOO_SMALL);
    EXPECT_EQ(count, 2);
    std::vector<std::int64_t> positions(2, -1);
    ASSERT_EQ(qe_bt_positions(bt_, positions.data(), 2), QE_OK);
    EXPECT_EQ(positions, (std::vector<std::int64_t>{0, 0})) << "a rejected step changes nothing";
}

TEST_F(Abi12, BadInputIsRejected_AndLeavesTheBacktestUnchanged) {
    const std::vector<qe_bt_bar> bars{Bar(100, 102, 98, 101), Bar(50, 51, 49, 50)};
    auto rejected = [&](std::vector<qe_bt_bar> b, std::vector<qe_bt_order> o) {
        const qe_status s = Step(b, o);
        return s == QE_E_INVALID_ARG && !LastError().empty();
    };
    EXPECT_TRUE(rejected({bars[0]}, {})) << "one bar per instrument";
    EXPECT_TRUE(rejected(bars, {{2, QE_BT_BUY, QE_BT_LIMIT, 0, 1, 100.0}})) << "instrument";
    EXPECT_TRUE(rejected(bars, {{0, 0, QE_BT_LIMIT, 0, 1, 100.0}})) << "side";
    EXPECT_TRUE(rejected(bars, {{0, QE_BT_BUY, 7, 0, 1, 100.0}})) << "type";
    EXPECT_TRUE(rejected(bars, {{0, QE_BT_BUY, QE_BT_LIMIT, 1, 1, 100.0}})) << "reserved";
    EXPECT_TRUE(rejected(bars, {{1, QE_BT_BUY, QE_BT_LIMIT, 0, 15, 50.0}})) << "not whole lots";
    EXPECT_TRUE(rejected(bars, {{0, QE_BT_BUY, QE_BT_LIMIT, 0, 1, 0.0}})) << "limit price";
    std::vector<qe_bt_bar> broken = bars;
    broken[0].high = 90.0; // high below the open
    EXPECT_TRUE(rejected(broken, {}));
    broken = bars;
    broken[1].valid = 2;
    EXPECT_TRUE(rejected(broken, {}));

    std::int64_t count = -1;
    qe_bt_state state{};
    EXPECT_EQ(qe_bt_step(bt_, bars.data(), 2, nullptr, 1, nullptr, 1, &count, &state),
              QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_step(bt_, bars.data(), 2, nullptr, 0, nullptr, 0, nullptr, &state),
              QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_step(nullptr, bars.data(), 2, nullptr, 0, nullptr, 0, &count, &state),
              QE_E_INVALID_ARG);

    ASSERT_EQ(Step(bars, {}), QE_OK);
    EXPECT_EQ(state_.orders, 0) << "rejected calls counted no orders";
    EXPECT_DOUBLE_EQ(state_.cash, 100'000.0);
}

TEST(Abi12Create, ValidatesConfigAndInstruments) {
    const std::vector<qe_bt_instrument> ok{{1, 0, 0}};
    qe_backtest* bt = reinterpret_cast<qe_backtest*>(0x1);
    qe_bt_config cfg = Config();
    cfg.struct_size = 48;
    EXPECT_EQ(qe_bt_create(&cfg, ok.data(), 1, &bt), QE_E_INVALID_ARG);
    EXPECT_EQ(bt, nullptr) << "*out is NULL on failure";
    cfg = Config();
    cfg.participation_cap = 0.0;
    EXPECT_EQ(qe_bt_create(&cfg, ok.data(), 1, &bt), QE_E_INVALID_ARG);
    EXPECT_NE(LastError().find("participation_cap"), std::string::npos);
    cfg = Config(-1.0);
    EXPECT_EQ(qe_bt_create(&cfg, ok.data(), 1, &bt), QE_E_INVALID_ARG);
    cfg = Config();
    const std::vector<qe_bt_instrument> zero_lot{{0, 0, 0}};
    EXPECT_EQ(qe_bt_create(&cfg, zero_lot.data(), 1, &bt), QE_E_INVALID_ARG);
    const std::vector<qe_bt_instrument> bad_flag{{1, 3, 0}};
    EXPECT_EQ(qe_bt_create(&cfg, bad_flag.data(), 1, &bt), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_create(&cfg, ok.data(), 0, &bt), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_create(&cfg, ok.data(), 1, nullptr), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_create(nullptr, ok.data(), 1, &bt), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_destroy(nullptr), QE_OK);

    // A qe_engine is not a qe_backtest.
    qe_engine* engine = nullptr;
    ASSERT_EQ(qe_engine_create(nullptr, &engine), QE_OK);
    EXPECT_EQ(qe_bt_destroy(reinterpret_cast<qe_backtest*>(engine)), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_engine_destroy(engine), QE_OK);
}

TEST_F(Abi12, PositionsNeedTheExactCount) {
    std::vector<std::int64_t> positions(3);
    EXPECT_EQ(qe_bt_positions(bt_, positions.data(), 3), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_positions(bt_, nullptr, 2), QE_E_INVALID_ARG);
    EXPECT_EQ(qe_bt_positions(nullptr, positions.data(), 2), QE_E_INVALID_ARG);
}

} // namespace
