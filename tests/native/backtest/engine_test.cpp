// qe::backtest::Engine fill model (docs/plans/05-phase5-backtesting.md "Engine").

#include "qe/backtest/engine.hpp"
#include "qe/core/errors.hpp"

#include <array>
#include <gtest/gtest.h>
#include <vector>

namespace {

using namespace qe::backtest;

constexpr int kBuy = 1;
constexpr int kSell = -1;

Config Costs(double cash = 1'000'000.0) {
    Config c;
    c.initial_cash = cash;
    c.courtage_min = 39.0;
    c.courtage_rate = 0.0015;
    c.fx_fee_rate = 0.0025;
    c.slippage_bps = 5.0;
    c.half_spread_bps = 5.0;
    c.participation_cap = 0.10;
    return c;
}

Config NoCosts(double cash = 1'000'000.0) {
    Config c;
    c.initial_cash = cash;
    c.participation_cap = 1.0;
    return c;
}

Bar B(double open, double high, double low, double close, double volume = 1'000'000.0) {
    return Bar{open, high, low, close, volume, true};
}

Order Limit(int side, std::int64_t qty, double limit, int instrument = 0) {
    return Order{instrument, side, OrderType::Limit, qty, limit};
}

struct Rig {
    explicit Rig(Config c, std::vector<Instrument> instruments = {Instrument{1, false}})
        : engine(c, instruments), fills(64) {}

    std::vector<Fill> Step(std::vector<Bar> bars, std::vector<Order> orders) {
        std::size_t n = engine.step(bars, orders, fills, state);
        return {fills.begin(), fills.begin() + static_cast<std::ptrdiff_t>(n)};
    }

    Engine engine;
    std::vector<Fill> fills;
    State state;
};

TEST(BacktestEngine, UntouchedLimitsDoNotFill) {
    Rig r(NoCosts());
    const Bar bar = B(100, 104, 98, 101);
    EXPECT_TRUE(r.Step({bar}, {Limit(kBuy, 10, 98.0)}).empty())
        << "a buy limit equal to the low was only touched";
    EXPECT_TRUE(r.Step({bar}, {Limit(kBuy, 10, 97.5)}).empty())
        << "a buy limit below the low was never reached";
    r.Step({bar}, {Order{0, kBuy, OrderType::MarketOnOpen, 50, 0}});
    EXPECT_TRUE(r.Step({bar}, {Limit(kSell, 10, 104.0)}).empty())
        << "a sell limit equal to the high was only touched";
    EXPECT_TRUE(r.Step({bar}, {Limit(kSell, 10, 104.5)}).empty())
        << "a sell limit above the high was never reached";
}

TEST(BacktestEngine, TradeThroughFillsAtTheLimit_AGapFillsAtTheOpen) {
    Rig r(NoCosts());
    auto f = r.Step({B(100, 104, 98, 101)}, {Limit(kBuy, 10, 99.0)});
    ASSERT_EQ(f.size(), 1u);
    EXPECT_DOUBLE_EQ(f[0].price, 99.0);
    EXPECT_EQ(f[0].quantity, 10);

    f = r.Step({B(95, 97, 94, 96)}, {Limit(kBuy, 10, 99.0)});
    ASSERT_EQ(f.size(), 1u);
    EXPECT_DOUBLE_EQ(f[0].price, 95.0)
        << "marketable at the open: the opening auction price, not the limit";

    f = r.Step({B(110, 112, 109, 111)}, {Limit(kSell, 5, 108.0)});
    ASSERT_EQ(f.size(), 1u);
    EXPECT_DOUBLE_EQ(f[0].price, 110.0);
    f = r.Step({B(100, 103, 99, 101)}, {Limit(kSell, 5, 102.0)});
    ASSERT_EQ(f.size(), 1u);
    EXPECT_DOUBLE_EQ(f[0].price, 102.0);
    EXPECT_EQ(r.engine.positions()[0], 10);
}

TEST(BacktestEngine, MarketOrdersPaySpreadAndSlippage) {
    Rig r(Costs());
    auto f = r.Step({B(100, 104, 98, 101)}, {Order{0, kBuy, OrderType::MarketOnOpen, 100, 0}});
    ASSERT_EQ(f.size(), 1u);
    EXPECT_DOUBLE_EQ(f[0].price, 100.0 * 1.001);
    EXPECT_NEAR(f[0].spread_slippage_cost, 0.1 * 100, 1e-9);
    f = r.Step({B(100, 104, 98, 101)}, {Order{0, kSell, OrderType::MarketOnClose, 100, 0}});
    ASSERT_EQ(f.size(), 1u);
    EXPECT_DOUBLE_EQ(f[0].price, 101.0 * 0.999);
}

TEST(BacktestEngine, CourtageMinimumAndRate_FxFee) {
    Rig r(Costs(), {Instrument{1, false}, Instrument{1, true}});
    auto f = r.Step({B(100, 101, 99, 100), B(100, 101, 99, 100)},
                    {Order{0, kBuy, OrderType::MarketOnOpen, 100, 0},
                     Order{1, kBuy, OrderType::MarketOnOpen, 1000, 0}});
    ASSERT_EQ(f.size(), 2u);
    EXPECT_DOUBLE_EQ(f[0].courtage, 39.0) << "10,010 SEK is below 26,000: the minimum applies";
    EXPECT_DOUBLE_EQ(f[0].fx_fee, 0.0);
    EXPECT_NEAR(f[1].courtage, 0.0015 * 100100.0, 1e-9);
    EXPECT_NEAR(f[1].fx_fee, 0.0025 * 100100.0, 1e-9);
    EXPECT_NEAR(r.state.cash, 1'000'000.0 - 10010.0 - 39.0 - 100100.0 - 150.15 - 250.25, 1e-6);
}

TEST(BacktestEngine, ParticipationCapIsSharedPerInstrument_AndTheRestExpires) {
    Rig r(Costs(), {Instrument{10, false}});
    auto f =
        r.Step({B(100, 101, 99, 100, 1234)}, {Order{0, kBuy, OrderType::MarketOnOpen, 100, 0},
                                              Order{0, kBuy, OrderType::MarketOnClose, 100, 0}});
    ASSERT_EQ(f.size(), 2u);
    EXPECT_EQ(f[0].quantity + f[1].quantity, 120) << "10 % of 1234 = 123.4, in lots of 10";
    EXPECT_EQ(f[0].quantity, 100);
    EXPECT_EQ(f[1].quantity, 20);
    EXPECT_TRUE(
        r.Step({B(100, 101, 99, 100, 0)}, {Order{0, kBuy, OrderType::MarketOnOpen, 10, 0}}).empty())
        << "no volume, no fill";
}

TEST(BacktestEngine, LongOnlyAndCashCaps) {
    Rig r(Costs(20'000.0));
    EXPECT_TRUE(
        r.Step({B(100, 101, 99, 100)}, {Order{0, kSell, OrderType::MarketOnOpen, 10, 0}}).empty())
        << "no short selling (ISK)";
    auto f = r.Step({B(100, 101, 99, 100)}, {Order{0, kBuy, OrderType::MarketOnOpen, 1000, 0}});
    ASSERT_EQ(f.size(), 1u);
    const double cost = static_cast<double>(f[0].quantity) * f[0].price + f[0].courtage;
    EXPECT_LE(cost, 20'000.0);
    EXPECT_GT(cost + 100.1 + 39.0, 20'000.0) << "one more share would not be affordable";
    EXPECT_GE(r.state.cash, 0.0);

    f = r.Step({B(100, 101, 99, 100)}, {Order{0, kSell, OrderType::MarketOnClose, 100000, 0}});
    ASSERT_EQ(f.size(), 1u);
    EXPECT_EQ(r.engine.positions()[0], 0) << "a sell is capped at the position";
}

TEST(BacktestEngine, AffordableQuantityIsExactInBothCourtageRegimes) {
    // Rate regime: 1,000,000 / (100.1 * 1.0015) = 9975.1 -> 9975 shares (courtage 0.15 % > 39).
    Rig big(Costs(1'000'000.0));
    auto f =
        big.Step({B(100, 101, 99, 100, 1e9)}, {Order{0, kBuy, OrderType::MarketOnOpen, 20000, 0}});
    ASSERT_EQ(f.size(), 1u);
    EXPECT_EQ(f[0].quantity, 9975);
    EXPECT_GE(big.state.cash, 0.0);
    EXPECT_LT(big.state.cash, 100.1 * 1.0015) << "one more share would not have been affordable";

    // Minimum regime: 5,000 SEK: (5000 - 39) / 100.1 = 49.56 -> 49 shares, courtage 39.
    Rig small(Costs(5'000.0));
    f = small.Step({B(100, 101, 99, 100, 1e9)},
                   {Order{0, kBuy, OrderType::MarketOnOpen, 20000, 0}});
    ASSERT_EQ(f.size(), 1u);
    EXPECT_EQ(f[0].quantity, 49);
    EXPECT_DOUBLE_EQ(f[0].courtage, 39.0);

    // Huge cash stays O(1): no search loop (this used to hang).
    Rig huge(Costs(1e15));
    f = huge.Step({B(100, 101, 99, 100, 1e12)},
                  {Order{0, kBuy, OrderType::MarketOnOpen, 1'000'000'000, 0}});
    ASSERT_EQ(f.size(), 1u);
    EXPECT_EQ(f[0].quantity, 1'000'000'000);
}

TEST(BacktestEngine, SellsFundBuysInTheSamePhase) {
    Rig r(NoCosts(0.0), {Instrument{1, false}, Instrument{1, false}});
    Rig seed(NoCosts(10'000.0), {Instrument{1, false}, Instrument{1, false}});
    seed.Step({B(100, 100, 100, 100), B(50, 50, 50, 50)},
              {Order{0, kBuy, OrderType::MarketOnOpen, 100, 0}});
    EXPECT_DOUBLE_EQ(seed.state.cash, 0.0);
    // Buy is listed first, but the sell of instrument 0 runs first and pays for it.
    auto f = seed.Step({B(100, 100, 100, 100), B(50, 50, 50, 50)},
                       {Order{1, kBuy, OrderType::MarketOnOpen, 200, 0},
                        Order{0, kSell, OrderType::MarketOnOpen, 100, 0}});
    ASSERT_EQ(f.size(), 2u);
    EXPECT_EQ(f[0].side, kSell);
    EXPECT_EQ(seed.engine.positions()[1], 200);
}

TEST(BacktestEngine, InvalidBarsFillNothing_AndEquityUsesTheLastClose) {
    Rig r(NoCosts());
    r.Step({B(100, 100, 100, 100)}, {Order{0, kBuy, OrderType::MarketOnOpen, 10, 0}});
    Bar halted{};
    EXPECT_TRUE(r.Step({halted}, {Order{0, kSell, OrderType::MarketOnOpen, 10, 0}}).empty());
    EXPECT_DOUBLE_EQ(r.state.equity, 1'000'000.0);
    EXPECT_DOUBLE_EQ(r.state.gross_exposure, 1000.0);
    r.Step({B(110, 110, 110, 110)}, {});
    EXPECT_DOUBLE_EQ(r.state.equity, 1'000'000.0 + 100.0);
}

TEST(BacktestEngine, OrdersFillOnTheBarTheyAreSubmittedWith_NeverTheOneTheyWereDecidedOn) {
    // The runner decides at the close of bar t and submits with bar t+1: the fill uses t+1 prices.
    Rig r(NoCosts());
    const std::array<Bar, 2> path{B(100, 100, 100, 100), B(120, 121, 119, 120)};
    r.Step({path[0]}, {});
    auto f = r.Step({path[1]},
                    {Order{0, kBuy, OrderType::MarketOnOpen, 10, 0}}); // decided after path[0]
    ASSERT_EQ(f.size(), 1u);
    EXPECT_DOUBLE_EQ(f[0].price, 120.0);
}

TEST(BacktestEngine, BadInputThrows_AndLeavesTheStateUnchanged) {
    Rig r(NoCosts(), {Instrument{10, false}});
    r.Step({B(100, 100, 100, 100)}, {Order{0, kBuy, OrderType::MarketOnOpen, 10, 0}});
    const State before = r.state;
    EXPECT_THROW(r.Step({B(100, 100, 100, 100)}, {Order{0, kBuy, OrderType::MarketOnOpen, 15, 0}}),
                 qe::InvalidArgument);
    EXPECT_THROW(r.Step({B(100, 100, 100, 100)}, {Order{1, kBuy, OrderType::MarketOnOpen, 10, 0}}),
                 qe::InvalidArgument);
    EXPECT_THROW(r.Step({B(100, 100, 100, 100)}, {Order{0, 2, OrderType::MarketOnOpen, 10, 0}}),
                 qe::InvalidArgument);
    EXPECT_THROW(r.Step({B(100, 100, 100, 100)}, {Limit(kBuy, 10, 0.0)}), qe::InvalidArgument);
    EXPECT_THROW(r.Step({B(100, 99, 101, 100)}, {}), qe::InvalidArgument); // low > high
    EXPECT_THROW(r.Step({B(100, 100, 100, 100), B(1, 1, 1, 1)}, {}), qe::InvalidArgument);
    std::vector<Fill> small(0);
    const std::vector<Bar> bars{B(100, 100, 100, 100)};
    const std::vector<Order> one{Order{0, kBuy, OrderType::MarketOnOpen, 10, 0}};
    State s;
    EXPECT_THROW(r.engine.step(bars, one, small, s), qe::InvalidArgument);
    EXPECT_EQ(r.engine.positions()[0], 10);
    EXPECT_DOUBLE_EQ(r.engine.state().cash, before.cash);
    EXPECT_EQ(r.engine.state().orders, before.orders);
    EXPECT_THROW(Engine(Config{-1}, std::vector<Instrument>{Instrument{}}), qe::InvalidArgument);
    EXPECT_THROW(Engine(NoCosts(), std::vector<Instrument>{}), qe::InvalidArgument);
}

} // namespace
