// Implementation of the exported C ABI (native/include/qe_api.h).
// Every export funnels through guarded(): no C++ exception crosses the boundary.

#include "qe_api.h"

#include "api/common.hpp"
#include "api/last_error.hpp"
#include "qe/pricing/black_scholes.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <exception>
#include <limits>
#include <memory>
#include <new>
#include <string_view>
#include <type_traits>

// ---- ABI layout pins (the C# side verifies the same numbers via qe_struct_layout) ----------
static_assert(std::is_standard_layout_v<qe_engine_config> && sizeof(qe_engine_config) == 16);
static_assert(std::is_standard_layout_v<qe_bs_input> && sizeof(qe_bs_input) == 56);
static_assert(std::is_standard_layout_v<qe_bs_output> && sizeof(qe_bs_output) == 16);
static_assert(std::is_standard_layout_v<qe_struct_layout_info> &&
              sizeof(qe_struct_layout_info) == 80);
static_assert(sizeof(double) == 8 && std::numeric_limits<double>::is_iec559);
// ABI 1.1
static_assert(sizeof(qe_bs_greeks) == 56 && sizeof(qe_iv_input) == 56 &&
              sizeof(qe_iv_output) == 16);
static_assert(sizeof(qe_lattice_input) == 64 && sizeof(qe_mc_config) == 32 &&
              sizeof(qe_mc_result) == 32);
static_assert(sizeof(qe_cov_config) == 16 && sizeof(qe_var_es) == 32);
static_assert(sizeof(qe_opt_config) == 32 && sizeof(qe_opt_result) == 16);
static_assert(sizeof(qe_rebalance_asset) == 32 && sizeof(qe_rebalance_config) == 48);
static_assert(sizeof(qe_rebalance_trade) == 40 && sizeof(qe_rebalance_summary) == 40);
// ABI 1.2
static_assert(sizeof(qe_bt_config) == 64 && sizeof(qe_bt_instrument) == 16 &&
              sizeof(qe_bt_bar) == 48);
static_assert(sizeof(qe_bt_order) == 32 && sizeof(qe_bt_fill) == 56 && sizeof(qe_bt_state) == 64);

namespace {

using qe::api::fail;
using qe::api::guarded;
using qe::api::is_live_engine;

template <typename T, std::size_t N>
qe_struct_layout_info make_layout(const std::array<std::size_t, N>& offsets) noexcept {
    static_assert(N <= QE_LAYOUT_MAX_FIELDS);
    qe_struct_layout_info info{};
    info.size = static_cast<std::int32_t>(sizeof(T));
    info.alignment = static_cast<std::int32_t>(alignof(T));
    info.field_count = static_cast<std::int32_t>(N);
    info.reserved = 0;
    std::fill(std::begin(info.offsets), std::end(info.offsets), -1);
    for (std::size_t i = 0; i < N; ++i) {
        info.offsets[i] = static_cast<std::int32_t>(offsets[i]);
    }
    return info;
}

using qe::api::kNaN;

} // namespace

extern "C" {

QE_API qe_status QE_CALL qe_abi_version(std::int32_t* major, std::int32_t* minor) noexcept {
    return guarded([&]() -> qe_status {
        if (major == nullptr || minor == nullptr) {
            return fail(QE_E_INVALID_ARG, "major and minor must not be NULL");
        }
        *major = QE_ABI_MAJOR;
        *minor = QE_ABI_MINOR;
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_last_error(char* buffer, std::int32_t capacity,
                                       std::int32_t* required) noexcept {
    // Deliberately not guarded(): reading the error must not clear it.
    if (required == nullptr) {
        return QE_E_INVALID_ARG;
    }
    const std::string_view message = qe::api::last_error();
    const auto needed = static_cast<std::int32_t>(message.size() + 1);
    *required = needed;
    if (buffer == nullptr || capacity < needed) {
        return QE_E_BUFFER_TOO_SMALL;
    }
    std::memcpy(buffer, message.data(), message.size());
    buffer[message.size()] = '\0';
    return QE_OK;
}

QE_API qe_status QE_CALL qe_struct_layout(std::int32_t struct_id,
                                          qe_struct_layout_info* out) noexcept {
    return guarded([&]() -> qe_status {
        if (out == nullptr) {
            return fail(QE_E_INVALID_ARG, "out must not be NULL");
        }
        switch (struct_id) {
        case QE_STRUCT_ENGINE_CONFIG:
            *out = make_layout<qe_engine_config>(std::array<std::size_t, 3>{
                offsetof(qe_engine_config, struct_size), offsetof(qe_engine_config, flags),
                offsetof(qe_engine_config, seed)});
            return QE_OK;
        case QE_STRUCT_BS_INPUT:
            *out = make_layout<qe_bs_input>(std::array<std::size_t, 8>{
                offsetof(qe_bs_input, spot), offsetof(qe_bs_input, strike),
                offsetof(qe_bs_input, rate), offsetof(qe_bs_input, dividend_yield),
                offsetof(qe_bs_input, volatility), offsetof(qe_bs_input, expiry_years),
                offsetof(qe_bs_input, option_type), offsetof(qe_bs_input, reserved)});
            return QE_OK;
        case QE_STRUCT_BS_OUTPUT:
            *out = make_layout<qe_bs_output>(std::array<std::size_t, 3>{
                offsetof(qe_bs_output, price), offsetof(qe_bs_output, status),
                offsetof(qe_bs_output, reserved)});
            return QE_OK;
        case QE_STRUCT_LAYOUT_INFO:
            *out = make_layout<qe_struct_layout_info>(std::array<std::size_t, 5>{
                offsetof(qe_struct_layout_info, size), offsetof(qe_struct_layout_info, alignment),
                offsetof(qe_struct_layout_info, field_count),
                offsetof(qe_struct_layout_info, reserved),
                offsetof(qe_struct_layout_info, offsets)});
            return QE_OK;
        case QE_STRUCT_BS_GREEKS:
            *out = make_layout<qe_bs_greeks>(std::array<std::size_t, 8>{
                offsetof(qe_bs_greeks, price), offsetof(qe_bs_greeks, delta),
                offsetof(qe_bs_greeks, gamma), offsetof(qe_bs_greeks, vega),
                offsetof(qe_bs_greeks, theta), offsetof(qe_bs_greeks, rho),
                offsetof(qe_bs_greeks, status), offsetof(qe_bs_greeks, reserved)});
            return QE_OK;
        case QE_STRUCT_IV_INPUT:
            *out = make_layout<qe_iv_input>(std::array<std::size_t, 8>{
                offsetof(qe_iv_input, spot), offsetof(qe_iv_input, strike),
                offsetof(qe_iv_input, rate), offsetof(qe_iv_input, dividend_yield),
                offsetof(qe_iv_input, expiry_years), offsetof(qe_iv_input, price),
                offsetof(qe_iv_input, option_type), offsetof(qe_iv_input, reserved)});
            return QE_OK;
        case QE_STRUCT_IV_OUTPUT:
            *out = make_layout<qe_iv_output>(std::array<std::size_t, 3>{
                offsetof(qe_iv_output, volatility), offsetof(qe_iv_output, status),
                offsetof(qe_iv_output, iterations)});
            return QE_OK;
        case QE_STRUCT_LATTICE_INPUT:
            *out = make_layout<qe_lattice_input>(std::array<std::size_t, 3>{
                offsetof(qe_lattice_input, option), offsetof(qe_lattice_input, steps),
                offsetof(qe_lattice_input, exercise)});
            return QE_OK;
        case QE_STRUCT_MC_CONFIG:
            *out = make_layout<qe_mc_config>(std::array<std::size_t, 6>{
                offsetof(qe_mc_config, struct_size), offsetof(qe_mc_config, flags),
                offsetof(qe_mc_config, paths), offsetof(qe_mc_config, seed),
                offsetof(qe_mc_config, replications), offsetof(qe_mc_config, steps)});
            return QE_OK;
        case QE_STRUCT_MC_RESULT:
            *out = make_layout<qe_mc_result>(std::array<std::size_t, 4>{
                offsetof(qe_mc_result, price), offsetof(qe_mc_result, std_error),
                offsetof(qe_mc_result, paths), offsetof(qe_mc_result, seed)});
            return QE_OK;
        case QE_STRUCT_COV_CONFIG:
            *out = make_layout<qe_cov_config>(std::array<std::size_t, 3>{
                offsetof(qe_cov_config, struct_size), offsetof(qe_cov_config, method),
                offsetof(qe_cov_config, ewma_lambda)});
            return QE_OK;
        case QE_STRUCT_VAR_ES:
            *out = make_layout<qe_var_es>(std::array<std::size_t, 4>{
                offsetof(qe_var_es, var), offsetof(qe_var_es, es),
                offsetof(qe_var_es, observations), offsetof(qe_var_es, seed)});
            return QE_OK;
        case QE_STRUCT_OPT_CONFIG:
            *out = make_layout<qe_opt_config>(std::array<std::size_t, 6>{
                offsetof(qe_opt_config, struct_size), offsetof(qe_opt_config, method),
                offsetof(qe_opt_config, risk_aversion), offsetof(qe_opt_config, tolerance),
                offsetof(qe_opt_config, max_iterations), offsetof(qe_opt_config, reserved)});
            return QE_OK;
        case QE_STRUCT_OPT_RESULT:
            *out = make_layout<qe_opt_result>(std::array<std::size_t, 3>{
                offsetof(qe_opt_result, objective), offsetof(qe_opt_result, iterations),
                offsetof(qe_opt_result, converged)});
            return QE_OK;
        case QE_STRUCT_REBALANCE_ASSET:
            *out = make_layout<qe_rebalance_asset>(std::array<std::size_t, 4>{
                offsetof(qe_rebalance_asset, price), offsetof(qe_rebalance_asset, target_weight),
                offsetof(qe_rebalance_asset, current_quantity),
                offsetof(qe_rebalance_asset, lot_size)});
            return QE_OK;
        case QE_STRUCT_REBALANCE_CONFIG:
            *out = make_layout<qe_rebalance_config>(std::array<std::size_t, 7>{
                offsetof(qe_rebalance_config, struct_size), offsetof(qe_rebalance_config, reserved),
                offsetof(qe_rebalance_config, cash), offsetof(qe_rebalance_config, cash_buffer),
                offsetof(qe_rebalance_config, min_trade_value),
                offsetof(qe_rebalance_config, fee_min), offsetof(qe_rebalance_config, fee_rate)});
            return QE_OK;
        case QE_STRUCT_REBALANCE_TRADE:
            *out = make_layout<qe_rebalance_trade>(std::array<std::size_t, 5>{
                offsetof(qe_rebalance_trade, target_quantity),
                offsetof(qe_rebalance_trade, trade_quantity),
                offsetof(qe_rebalance_trade, trade_value), offsetof(qe_rebalance_trade, fee),
                offsetof(qe_rebalance_trade, final_weight)});
            return QE_OK;
        case QE_STRUCT_REBALANCE_SUMMARY:
            *out = make_layout<qe_rebalance_summary>(std::array<std::size_t, 6>{
                offsetof(qe_rebalance_summary, portfolio_value),
                offsetof(qe_rebalance_summary, cash_after),
                offsetof(qe_rebalance_summary, total_fees),
                offsetof(qe_rebalance_summary, tracking_error),
                offsetof(qe_rebalance_summary, trades), offsetof(qe_rebalance_summary, feasible)});
            return QE_OK;
        case QE_STRUCT_BT_CONFIG:
            *out = make_layout<qe_bt_config>(std::array<std::size_t, 9>{
                offsetof(qe_bt_config, struct_size), offsetof(qe_bt_config, reserved),
                offsetof(qe_bt_config, initial_cash), offsetof(qe_bt_config, courtage_min),
                offsetof(qe_bt_config, courtage_rate), offsetof(qe_bt_config, fx_fee_rate),
                offsetof(qe_bt_config, slippage_bps), offsetof(qe_bt_config, half_spread_bps),
                offsetof(qe_bt_config, participation_cap)});
            return QE_OK;
        case QE_STRUCT_BT_INSTRUMENT:
            *out = make_layout<qe_bt_instrument>(std::array<std::size_t, 3>{
                offsetof(qe_bt_instrument, lot_size), offsetof(qe_bt_instrument, foreign_currency),
                offsetof(qe_bt_instrument, reserved)});
            return QE_OK;
        case QE_STRUCT_BT_BAR:
            *out = make_layout<qe_bt_bar>(std::array<std::size_t, 7>{
                offsetof(qe_bt_bar, open), offsetof(qe_bt_bar, high), offsetof(qe_bt_bar, low),
                offsetof(qe_bt_bar, close), offsetof(qe_bt_bar, volume), offsetof(qe_bt_bar, valid),
                offsetof(qe_bt_bar, reserved)});
            return QE_OK;
        case QE_STRUCT_BT_ORDER:
            *out = make_layout<qe_bt_order>(std::array<std::size_t, 6>{
                offsetof(qe_bt_order, instrument), offsetof(qe_bt_order, side),
                offsetof(qe_bt_order, type), offsetof(qe_bt_order, reserved),
                offsetof(qe_bt_order, quantity), offsetof(qe_bt_order, limit_price)});
            return QE_OK;
        case QE_STRUCT_BT_FILL:
            *out = make_layout<qe_bt_fill>(std::array<std::size_t, 9>{
                offsetof(qe_bt_fill, instrument), offsetof(qe_bt_fill, side),
                offsetof(qe_bt_fill, type), offsetof(qe_bt_fill, order_index),
                offsetof(qe_bt_fill, quantity), offsetof(qe_bt_fill, price),
                offsetof(qe_bt_fill, courtage), offsetof(qe_bt_fill, fx_fee),
                offsetof(qe_bt_fill, spread_slippage_cost)});
            return QE_OK;
        case QE_STRUCT_BT_STATE:
            *out = make_layout<qe_bt_state>(std::array<std::size_t, 8>{
                offsetof(qe_bt_state, cash), offsetof(qe_bt_state, equity),
                offsetof(qe_bt_state, gross_exposure), offsetof(qe_bt_state, courtage),
                offsetof(qe_bt_state, fx_fees), offsetof(qe_bt_state, spread_slippage),
                offsetof(qe_bt_state, fills), offsetof(qe_bt_state, orders)});
            return QE_OK;
        default:
            return fail(QE_E_INVALID_ARG, "unknown struct_id");
        }
    });
}

QE_API qe_status QE_CALL qe_engine_create(const qe_engine_config* config,
                                          qe_engine** out_engine) noexcept {
    return guarded([&]() -> qe_status {
        if (out_engine == nullptr) {
            return fail(QE_E_INVALID_ARG, "out_engine must not be NULL");
        }
        *out_engine = nullptr;
        std::uint64_t seed = 0;
        if (config != nullptr) {
            if (config->struct_size != static_cast<std::int32_t>(sizeof(qe_engine_config))) {
                return fail(QE_E_INVALID_ARG,
                            "config->struct_size must equal sizeof(qe_engine_config) (16)");
            }
            if (config->flags != 0) {
                return fail(QE_E_INVALID_ARG, "config->flags must be 0 in ABI 1.0");
            }
            seed = config->seed;
        }
        auto engine = std::make_unique<qe_engine>();
        engine->seed = seed;
        *out_engine = engine.release(); // ownership passes to the caller's handle
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_engine_destroy(qe_engine* engine) noexcept {
    return guarded([&]() -> qe_status {
        if (engine == nullptr) {
            return QE_OK;
        }
        if (engine->magic != qe_engine::kMagic) {
            return fail(QE_E_INVALID_ARG, "engine is not a live qe_engine handle");
        }
        const std::unique_ptr<qe_engine> owned(engine); // reclaims ownership from the caller
        owned->magic = 0;
        return QE_OK;
    });
}

QE_API qe_status QE_CALL qe_bs_price_batch(const qe_engine* engine, const qe_bs_input* inputs,
                                           qe_bs_output* outputs, std::int64_t count,
                                           std::int64_t* failed_count) noexcept {
    return guarded([&]() -> qe_status {
        if (failed_count == nullptr) {
            return fail(QE_E_INVALID_ARG, "failed_count must not be NULL");
        }
        *failed_count = 0;
        if (!is_live_engine(engine)) {
            return fail(QE_E_INVALID_ARG, "engine is NULL or not a live qe_engine handle");
        }
        if (count < 0) {
            return fail(QE_E_INVALID_ARG, "count must be >= 0");
        }
        if (count > 0 && (inputs == nullptr || outputs == nullptr)) {
            return fail(QE_E_INVALID_ARG, "inputs and outputs must not be NULL when count > 0");
        }

        std::int64_t failed = 0;
        for (std::int64_t i = 0; i < count; ++i) {
            const qe_bs_input& in = inputs[i];
            qe_bs_output& out = outputs[i];
            out.reserved = 0;

            const qe::pricing::BsParams params{
                .spot = in.spot,
                .strike = in.strike,
                .rate = in.rate,
                .dividend_yield = in.dividend_yield,
                .volatility = in.volatility,
                .expiry_years = in.expiry_years,
                .type = static_cast<qe::pricing::OptionType>(in.option_type),
            };
            if (in.reserved != 0 || qe::pricing::validate(params).has_value()) {
                out.price = kNaN;
                out.status = QE_E_INVALID_ARG;
                ++failed;
                continue;
            }
            const double price = qe::pricing::bs_price(params);
            if (!std::isfinite(price)) {
                out.price = kNaN;
                out.status = QE_E_NUMERIC;
                ++failed;
                continue;
            }
            out.price = price;
            out.status = QE_OK;
        }
        *failed_count = failed;
        return QE_OK;
    });
}

} // extern "C"
