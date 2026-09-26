#include "qe/core/stats.hpp"

#include "qe/core/errors.hpp"

#include <cmath>

namespace qe::core {

void Welford::add(double x) noexcept {
    ++n;
    const double delta = x - mean;
    mean += delta / static_cast<double>(n);
    m2 += delta * (x - mean);
}

void Welford::merge(const Welford& other) noexcept {
    if (other.n == 0) {
        return;
    }
    if (n == 0) {
        *this = other;
        return;
    }
    const auto na = static_cast<double>(n);
    const auto nb = static_cast<double>(other.n);
    const double total = na + nb;
    const double delta = other.mean - mean;
    mean += delta * nb / total;
    m2 += other.m2 + delta * delta * na * nb / total;
    n += other.n;
}

double Welford::variance() const noexcept {
    return n < 2 ? 0.0 : m2 / static_cast<double>(n - 1);
}

double Welford::std_error() const noexcept {
    return n < 2 ? 0.0 : std::sqrt(variance() / static_cast<double>(n));
}

void Welford2::add(double x, double y) noexcept {
    ++n;
    const auto nn = static_cast<double>(n);
    const double dx = x - mean_x;
    const double dy = y - mean_y;
    mean_x += dx / nn;
    mean_y += dy / nn;
    m2_x += dx * (x - mean_x);
    m2_y += dy * (y - mean_y);
    c_xy += dx * (y - mean_y);
}

void Welford2::merge(const Welford2& other) noexcept {
    if (other.n == 0) {
        return;
    }
    if (n == 0) {
        *this = other;
        return;
    }
    const auto na = static_cast<double>(n);
    const auto nb = static_cast<double>(other.n);
    const double total = na + nb;
    const double dx = other.mean_x - mean_x;
    const double dy = other.mean_y - mean_y;
    const double f = na * nb / total;
    mean_x += dx * nb / total;
    mean_y += dy * nb / total;
    m2_x += other.m2_x + dx * dx * f;
    m2_y += other.m2_y + dy * dy * f;
    c_xy += other.c_xy + dx * dy * f;
    n += other.n;
}

void NeumaierSum::add(double x) noexcept {
    const double t = sum_ + x;
    if (std::abs(sum_) >= std::abs(x)) {
        compensation_ += (sum_ - t) + x;
    } else {
        compensation_ += (x - t) + sum_;
    }
    sum_ = t;
}

double quantile_linear_sorted(std::span<const double> sorted, double level) {
    if (sorted.empty()) {
        throw InvalidArgument("quantile: data is empty");
    }
    if (!(level >= 0.0 && level <= 1.0)) {
        throw InvalidArgument("quantile: level must be in [0, 1]");
    }
    const double pos = level * static_cast<double>(sorted.size() - 1);
    const auto lo = static_cast<std::size_t>(std::floor(pos));
    const std::size_t hi = lo + 1 < sorted.size() ? lo + 1 : lo;
    const double frac = pos - static_cast<double>(lo);
    return sorted[lo] + frac * (sorted[hi] - sorted[lo]);
}

} // namespace qe::core
