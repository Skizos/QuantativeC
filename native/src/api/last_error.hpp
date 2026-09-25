#pragma once

#include <cstddef>
#include <string_view>

namespace qe::api {

// Thread-local last-error storage behind qe_last_error(). Fixed capacity so that recording an
// error never allocates (it is called from catch handlers inside noexcept exports).
inline constexpr std::size_t kMaxErrorLength = 1023;

void clear_last_error() noexcept;
void set_last_error(std::string_view message) noexcept;
[[nodiscard]] std::string_view last_error() noexcept;

} // namespace qe::api
