// Implementation of the exported C ABI (native/include/qe_api.h).
// Every export funnels through guarded(): no C++ exception crosses the boundary.

#include "qe_api.h"

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

// Opaque handle type declared in qe_api.h.
struct qe_engine {
    static constexpr std::uint64_t kMagic = 0x71652D656E67696EULL; // "qe-engin"
    std::uint64_t magic{kMagic};
    std::uint64_t seed{0};
};

// ---- ABI layout pins (the C# side verifies the same numbers via qe_struct_layout) ----------
static_assert(std::is_standard_layout_v<qe_engine_config> && sizeof(qe_engine_config) == 16);
static_assert(std::is_standard_layout_v<qe_bs_input> && sizeof(qe_bs_input) == 56);
static_assert(std::is_standard_layout_v<qe_bs_output> && sizeof(qe_bs_output) == 16);
static_assert(std::is_standard_layout_v<qe_struct_layout_info> &&
              sizeof(qe_struct_layout_info) == 80);
static_assert(sizeof(double) == 8 && std::numeric_limits<double>::is_iec559);

namespace {

using qe::api::clear_last_error;
using qe::api::set_last_error;

template <typename Fn>
qe_status guarded(Fn&& fn) noexcept {
    clear_last_error();
    try {
        return fn();
    } catch (const std::bad_alloc&) {
        set_last_error("out of memory");
        return QE_E_OUT_OF_MEMORY;
    } catch (const std::exception& e) {
        set_last_error(e.what());
        return QE_E_INTERNAL;
    } catch (...) {
        set_last_error("unknown C++ exception");
        return QE_E_INTERNAL;
    }
}

qe_status fail(qe_status status, std::string_view message) noexcept {
    set_last_error(message);
    return status;
}

bool is_live_engine(const qe_engine* engine) noexcept {
    return engine != nullptr && engine->magic == qe_engine::kMagic;
}

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

constexpr double kNaN = std::numeric_limits<double>::quiet_NaN();

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
