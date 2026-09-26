#pragma once

#include <cstdint>
#include <span>

namespace qe::core {

/// Welford's online mean/variance with Chan et al. merge (order-independent within rounding).
struct Welford {
    std::int64_t n{0};
    double mean{0.0};
    double m2{0.0};

    void add(double x) noexcept;
    void merge(const Welford& other) noexcept;
    /// Unbiased sample variance (n - 1); 0 when n < 2.
    [[nodiscard]] double variance() const noexcept;
    /// Standard error of the mean.
    [[nodiscard]] double std_error() const noexcept;
};

/// Online co-moments of (x, y) with merge; used for control variates.
struct Welford2 {
    std::int64_t n{0};
    double mean_x{0.0};
    double mean_y{0.0};
    double m2_x{0.0};
    double m2_y{0.0};
    double c_xy{0.0};

    void add(double x, double y) noexcept;
    void merge(const Welford2& other) noexcept;
};

/// Neumaier's improved Kahan summation.
class NeumaierSum {
  public:
    void add(double x) noexcept;
    [[nodiscard]] double value() const noexcept { return sum_ + compensation_; }

  private:
    double sum_{0.0};
    double compensation_{0.0};
};

/// Quantile of sorted data with linear interpolation (numpy "linear", Hyndman-Fan type 7).
/// Throws InvalidArgument for empty data or level outside [0, 1].
[[nodiscard]] double quantile_linear_sorted(std::span<const double> sorted, double level);

} // namespace qe::core
