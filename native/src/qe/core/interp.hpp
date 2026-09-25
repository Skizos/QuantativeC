#pragma once

#include <span>
#include <vector>

namespace qe::core {

/// Piecewise-linear interpolation on strictly increasing knots with flat extrapolation.
class LinearInterpolator {
  public:
    /// Throws InvalidArgument unless sizes match, there are >= 2 knots, x is strictly increasing
    /// and all values are finite.
    LinearInterpolator(std::span<const double> x, std::span<const double> y);

    [[nodiscard]] double operator()(double x) const noexcept;

  private:
    std::vector<double> x_;
    std::vector<double> y_;
};

} // namespace qe::core
