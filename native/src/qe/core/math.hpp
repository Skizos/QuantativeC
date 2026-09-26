#pragma once

namespace qe::core {

/// Standard normal density.
[[nodiscard]] double norm_pdf(double x) noexcept;

/// Standard normal CDF via erfc (full relative precision in the lower tail).
[[nodiscard]] double norm_cdf(double x) noexcept;

/// Inverse standard normal CDF for p in (0, 1): Acklam's rational approximation refined by one
/// Halley step on norm_cdf. Returns -inf/+inf at 0/1 and NaN outside [0, 1].
[[nodiscard]] double norm_inv(double p) noexcept;

} // namespace qe::core
