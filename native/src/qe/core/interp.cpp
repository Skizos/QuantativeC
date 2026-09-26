#include "qe/core/interp.hpp"

#include "qe/core/errors.hpp"

#include <algorithm>
#include <cmath>

namespace qe::core {

LinearInterpolator::LinearInterpolator(std::span<const double> x, std::span<const double> y)
    : x_(x.begin(), x.end()), y_(y.begin(), y.end()) {
    if (x_.size() != y_.size() || x_.size() < 2) {
        throw InvalidArgument("LinearInterpolator: need >= 2 knots and equal-sized x and y");
    }
    for (std::size_t i = 0; i < x_.size(); ++i) {
        if (!std::isfinite(x_[i]) || !std::isfinite(y_[i])) {
            throw InvalidArgument("LinearInterpolator: knots must be finite");
        }
        if (i > 0 && !(x_[i] > x_[i - 1])) {
            throw InvalidArgument("LinearInterpolator: x must be strictly increasing");
        }
    }
}

double LinearInterpolator::operator()(double x) const noexcept {
    if (x <= x_.front()) {
        return y_.front();
    }
    if (x >= x_.back()) {
        return y_.back();
    }
    const auto it = std::upper_bound(x_.begin(), x_.end(), x);
    const auto i = static_cast<std::size_t>(it - x_.begin());
    const double t = (x - x_[i - 1]) / (x_[i] - x_[i - 1]);
    return y_[i - 1] + t * (y_[i] - y_[i - 1]);
}

} // namespace qe::core
