#include "api/last_error.hpp"

#include <algorithm>
#include <array>

namespace qe::api {
namespace {

struct ErrorSlot {
    std::array<char, kMaxErrorLength + 1> text{};
    std::size_t length{0};
};

thread_local ErrorSlot t_error;

} // namespace

void clear_last_error() noexcept {
    t_error.length = 0;
    t_error.text[0] = '\0';
}

void set_last_error(std::string_view message) noexcept {
    const std::size_t n = std::min(message.size(), kMaxErrorLength);
    std::copy_n(message.data(), n, t_error.text.data());
    t_error.text[n] = '\0';
    t_error.length = n;
}

std::string_view last_error() noexcept {
    return {t_error.text.data(), t_error.length};
}

} // namespace qe::api
