#pragma once

#include <array>
#include <cstdint>
#include <span>
#include <vector>

namespace qe::core {

/// Sobol low-discrepancy sequence (Joe & Kuo 2008 direction numbers, 32-bit, Gray-code order).
/// Point 0 is the origin, matching scipy.stats.qmc.Sobol(scramble=False).
class Sobol {
  public:
    static constexpr int kBits = 32;

    /// Highest supported dimension count (size of the embedded direction-number table).
    [[nodiscard]] static int max_dims() noexcept;

    /// Throws InvalidArgument unless 1 <= dims <= max_dims().
    explicit Sobol(int dims);

    [[nodiscard]] int dims() const noexcept { return dims_; }
    [[nodiscard]] std::uint64_t index() const noexcept { return index_; }

    /// Writes the next point as 32-bit integers (u = x * 2^-32) and advances.
    /// Throws NumericError after 2^32 points.
    void next(std::span<std::uint32_t> out);

    /// Maps an integer coordinate (optionally digitally shifted) into the open interval (0, 1).
    [[nodiscard]] static double to_open_unit(std::uint32_t x) noexcept {
        return (static_cast<double>(x) + 0.5) * 0x1.0p-32;
    }

  private:
    int dims_;
    std::uint64_t index_{0};
    std::vector<std::array<std::uint32_t, kBits>> directions_;
    std::vector<std::uint32_t> state_;
};

} // namespace qe::core
