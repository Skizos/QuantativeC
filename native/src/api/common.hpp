#pragma once
// Shared plumbing for the C ABI translation units (never included by qe_impl).

#include "api/last_error.hpp"
#include "qe/core/errors.hpp"
#include "qe_api.h"

#include <Eigen/Core>
#include <cstdint>
#include <exception>
#include <limits>
#include <new>
#include <string_view>

// Opaque handle type declared in qe_api.h.
struct qe_engine {
    static constexpr std::uint64_t kMagic = 0x71652D656E67696EULL; // "qe-engin"
    std::uint64_t magic{kMagic};
    std::uint64_t seed{0};
};

namespace qe::api {

inline constexpr double kNaN = std::numeric_limits<double>::quiet_NaN();
/// Upper bound for element counts and matrix sizes accepted at the boundary.
inline constexpr std::int64_t kMaxElements = 1'000'000'000;

using RowMajorMatrix = Eigen::Matrix<double, Eigen::Dynamic, Eigen::Dynamic, Eigen::RowMajor>;
using ConstRowMajorMap = Eigen::Map<const RowMajorMatrix>;
using RowMajorMap = Eigen::Map<RowMajorMatrix>;
using ConstVectorMap = Eigen::Map<const Eigen::VectorXd>;

/// Runs fn with the thread's last error cleared and converts every exception into a status.
template <typename Fn>
qe_status guarded(Fn&& fn) noexcept {
    clear_last_error();
    try {
        return fn();
    } catch (const qe::InvalidArgument& e) {
        set_last_error(e.what());
        return QE_E_INVALID_ARG;
    } catch (const qe::NumericError& e) {
        set_last_error(e.what());
        return QE_E_NUMERIC;
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

inline qe_status fail(qe_status status, std::string_view message) noexcept {
    set_last_error(message);
    return status;
}

/// Throws InvalidArgument(message) unless condition holds.
inline void require(bool condition, const char* message) {
    if (!condition) {
        throw qe::InvalidArgument(message);
    }
}

inline bool is_live_engine(const qe_engine* engine) noexcept {
    return engine != nullptr && engine->magic == qe_engine::kMagic;
}

inline void require_engine(const qe_engine* engine) {
    require(is_live_engine(engine), "engine is NULL or not a live qe_engine handle");
}

/// Validates the common shape of per-element batch calls and zeroes *failed_count.
inline void require_batch(const qe_engine* engine, const void* inputs, const void* outputs,
                          std::int64_t count, std::int64_t* failed_count) {
    require(failed_count != nullptr, "failed_count must not be NULL");
    *failed_count = 0;
    require_engine(engine);
    require(count >= 0 && count <= kMaxElements, "count must be in [0, 1e9]");
    require(count == 0 || (inputs != nullptr && outputs != nullptr),
            "inputs and outputs must not be NULL when count > 0");
}

/// Validates matrix dimensions against kMaxElements.
inline void require_dims(std::int64_t rows, std::int64_t cols, const char* message) {
    require(rows >= 1 && cols >= 1 && rows <= kMaxElements / cols, message);
}

} // namespace qe::api
