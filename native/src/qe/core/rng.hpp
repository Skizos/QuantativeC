#pragma once

#include <cstdint>
#include <random>

namespace qe::core {

/// SplitMix64 finalizer (Steele, Lea, Flood 2014); used to derive independent stream seeds.
[[nodiscard]] constexpr std::uint64_t splitmix64(std::uint64_t x) noexcept {
    x += 0x9E3779B97F4A7C15ULL;
    x = (x ^ (x >> 30U)) * 0xBF58476D1CE4E5B9ULL;
    x = (x ^ (x >> 27U)) * 0x94D049BB133111EBULL;
    return x ^ (x >> 31U);
}

/// Seed for stream `stream` of a computation seeded with `base` (blocks, threads, replications).
[[nodiscard]] constexpr std::uint64_t derive_seed(std::uint64_t base,
                                                  std::uint64_t stream) noexcept {
    return splitmix64(base ^ splitmix64(stream));
}

/// Deterministic generator: std::mt19937_64 (output sequence fixed by the C++ standard) with our
/// own uniform and normal transforms, so results do not depend on the standard library's
/// distribution implementations.
class Rng {
  public:
    explicit Rng(std::uint64_t seed) : engine_(seed) {}

    [[nodiscard]] std::uint64_t next_u64() { return engine_(); }

    /// Uniform on the open interval (0, 1) with 53 random bits.
    [[nodiscard]] double uniform_open() {
        return (static_cast<double>(engine_() >> 11U) + 0.5) * 0x1.0p-53;
    }

    /// Standard normal via the inverse CDF.
    [[nodiscard]] double normal();

  private:
    std::mt19937_64 engine_;
};

} // namespace qe::core
