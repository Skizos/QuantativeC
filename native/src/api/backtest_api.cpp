// ABI 1.2 backtest exports: a handle around qe::backtest::Engine, one qe_bt_step call per bar.

#include "api/common.hpp"
#include "qe/backtest/engine.hpp"
#include "qe_api.h"

#include <algorithm>
#include <cstdint>
#include <memory>
#include <span>
#include <vector>

namespace bt = qe::backtest;

// Opaque handle type declared in qe_api.h. The scratch buffers convert between the C structs
// and the engine's types; they grow only when a call brings more orders than any before, so a
// steady-state step does not allocate.
struct qe_backtest {
    static constexpr std::uint64_t kMagic = 0x71652D6274657374ULL; // "qe-btest"

    qe_backtest(const bt::Config& config, std::span<const bt::Instrument> instruments)
        : engine(config, instruments), bars(instruments.size()) {
        orders.reserve(4 * instruments.size());
        fills.reserve(4 * instruments.size());
    }

    std::uint64_t magic{kMagic};
    bt::Engine engine;
    std::vector<bt::Bar> bars;
    std::vector<bt::Order> orders;
    std::vector<bt::Fill> fills;
};

namespace {

using namespace qe::api;

/// Upper bound on instruments and on orders per step (order_index is int32).
constexpr std::int64_t kMaxInstruments = 1'000'000;
constexpr std::int64_t kMaxOrders = 10'000'000;

bool is_live(const qe_backtest* backtest) noexcept {
    return backtest != nullptr && backtest->magic == qe_backtest::kMagic;
}

void require_backtest(const qe_backtest* backtest) {
    require(is_live(backtest), "backtest is NULL or not a live qe_backtest handle");
}

bool is_flag(std::int32_t value) noexcept {
    return value == 0 || value == 1;
}

} // namespace

extern "C" {

QE_API qe_status QE_CALL qe_bt_create(const qe_bt_config* config,
                                      const qe_bt_instrument* instruments, std::int64_t count,
                                      qe_backtest** out) noexcept {
    return guarded([&]() -> qe_status {
        require(out != nullptr, "out must not be NULL");
        *out = nullptr;
        require(config != nullptr && instruments != nullptr,
                "config and instruments must not be NULL");
        require(config->struct_size == static_cast<std::int32_t>(sizeof(qe_bt_config)),
                "config->struct_size must equal sizeof(qe_bt_config) (64)");
        require(config->reserved == 0, "config->reserved must be 0");
        require(count >= 1 && count <= kMaxInstruments, "count must be in [1, 1000000]");

        std::vector<bt::Instrument> in(static_cast<std::size_t>(count));
        for (std::size_t i = 0; i < in.size(); ++i) {
            const qe_bt_instrument& x = instruments[i];
            require(is_flag(x.foreign_currency) && x.reserved == 0,
                    "instrument foreign_currency must be 0 or 1 and reserved 0");
            in[i] = bt::Instrument{x.lot_size, x.foreign_currency == 1};
        }
        const bt::Config cfg{.initial_cash = config->initial_cash,
                             .courtage_min = config->courtage_min,
                             .courtage_rate = config->courtage_rate,
                             .fx_fee_rate = config->fx_fee_rate,
                             .slippage_bps = config->slippage_bps,
                             .half_spread_bps = config->half_spread_bps,
                             .participation_cap = config->participation_cap};
        auto backtest = std::make_unique<qe_backtest>(cfg, in); // the engine validates cfg
        *out = backtest.release(); // ownership passes to the caller's handle
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_bt_set_courtage(qe_backtest* backtest, std::int64_t instrument,
                                            double courtage_min, double courtage_rate) noexcept {
    return guarded([&]() -> qe_status {
        require_backtest(backtest);
        require(instrument >= 0 &&
                    instrument < static_cast<std::int64_t>(backtest->engine.instrument_count()),
                "instrument out of range");
        backtest->engine.set_courtage(static_cast<std::size_t>(instrument), courtage_min,
                                      courtage_rate); // validates; unchanged on failure
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_bt_set_fill_mode(qe_backtest* backtest, std::int32_t mode) noexcept {
    return guarded([&]() -> qe_status {
        require_backtest(backtest);
        require(mode == QE_BT_FILL_DAILY || mode == QE_BT_FILL_INTRADAY,
                "mode must be QE_BT_FILL_DAILY or QE_BT_FILL_INTRADAY");
        backtest->engine.set_fill_mode(
            static_cast<qe::backtest::FillMode>(mode)); // before the first step
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_bt_set_half_spread(qe_backtest* backtest, std::int64_t instrument,
                                               double half_spread_bps) noexcept {
    return guarded([&]() -> qe_status {
        require_backtest(backtest);
        require(instrument >= 0 &&
                    instrument < static_cast<std::int64_t>(backtest->engine.instrument_count()),
                "instrument out of range");
        backtest->engine.set_half_spread(static_cast<std::size_t>(instrument),
                                         half_spread_bps); // validates; unchanged on failure
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_bt_destroy(qe_backtest* backtest) noexcept {
    return guarded([&]() -> qe_status {
        if (backtest == nullptr) {
            return QE_OK;
        }
        require_backtest(backtest);
        const std::unique_ptr<qe_backtest> owned(backtest); // reclaims ownership from the caller
        owned->magic = 0;
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_bt_step(qe_backtest* backtest, const qe_bt_bar* bars,
                                    std::int64_t bar_count, const qe_bt_order* orders,
                                    std::int64_t order_count, qe_bt_fill* fills,
                                    std::int64_t fill_capacity, std::int64_t* fill_count,
                                    qe_bt_state* out_state) noexcept {
    return guarded([&]() -> qe_status {
        require(fill_count != nullptr && out_state != nullptr,
                "fill_count and out_state must not be NULL");
        *fill_count = 0;
        require_backtest(backtest);
        const auto n = static_cast<std::int64_t>(backtest->engine.instrument_count());
        require(bars != nullptr && bar_count == n,
                "bars must not be NULL and bar_count must equal the instrument count");
        require(order_count >= 0 && order_count <= kMaxOrders,
                "order_count must be in [0, 10000000]");
        require(order_count == 0 || (orders != nullptr && fills != nullptr),
                "orders and fills must not be NULL when order_count > 0");
        require(fill_capacity >= 0, "fill_capacity must be >= 0");
        if (fill_capacity < order_count) {
            *fill_count = order_count;
            return fail(QE_E_BUFFER_TOO_SMALL,
                        "fill_capacity must be >= order_count (required size in *fill_count)");
        }

        for (std::size_t i = 0; i < backtest->bars.size(); ++i) {
            const qe_bt_bar& b = bars[i];
            require(is_flag(b.valid) && b.reserved == 0, "bar valid must be 0 or 1 and reserved 0");
            backtest->bars[i] = bt::Bar{b.open, b.high, b.low, b.close, b.volume, b.valid == 1};
        }
        const auto count = static_cast<std::size_t>(order_count);
        backtest->orders.resize(count);
        for (std::size_t k = 0; k < count; ++k) {
            const qe_bt_order& o = orders[k];
            require(o.reserved == 0, "order reserved must be 0");
            require(o.type == QE_BT_LIMIT || o.type == QE_BT_MARKET_ON_OPEN ||
                        o.type == QE_BT_MARKET_ON_CLOSE,
                    "order type must be one of QE_BT_*");
            backtest->orders[k] =
                bt::Order{o.instrument, o.side, static_cast<bt::OrderType>(o.type), o.quantity,
                          o.limit_price};
        }
        backtest->fills.resize(count);

        bt::State state;
        const std::size_t filled =
            backtest->engine.step(backtest->bars, backtest->orders, backtest->fills, state);
        for (std::size_t k = 0; k < filled; ++k) {
            const bt::Fill& f = backtest->fills[k];
            fills[k] = qe_bt_fill{f.instrument,  f.side,     static_cast<std::int32_t>(f.type),
                                  f.order_index, f.quantity, f.price,
                                  f.courtage,    f.fx_fee,   f.spread_slippage_cost};
        }
        *fill_count = static_cast<std::int64_t>(filled);
        *out_state =
            qe_bt_state{state.cash,    state.equity,          state.gross_exposure, state.courtage,
                        state.fx_fees, state.spread_slippage, state.fills,          state.orders};
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_bt_positions(const qe_backtest* backtest, std::int64_t* out,
                                         std::int64_t count) noexcept {
    return guarded([&]() -> qe_status {
        require_backtest(backtest);
        const std::span<const std::int64_t> positions = backtest->engine.positions();
        require(out != nullptr && count == static_cast<std::int64_t>(positions.size()),
                "out must not be NULL and count must equal the instrument count");
        std::copy(positions.begin(), positions.end(), out);
        return QE_OK;
    });
}

} // extern "C"
