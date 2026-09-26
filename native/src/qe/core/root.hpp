#pragma once

#include "qe/core/errors.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

namespace qe::core {

struct RootResult {
    double x{};
    int iterations{};
    bool converged{};
};

/// Brent's method (zeroin as in Numerical Recipes "zbrent") for f(x) = 0 on [a, b].
/// Requires f(a) and f(b) of opposite sign (or one of them zero); throws InvalidArgument otherwise.
/// Stops when the bracket is below 2*eps*|x| + xtol/2 or f(x) == 0.
template <typename F>
[[nodiscard]] RootResult brent(F&& f, double a, double b, double xtol = 1e-14, int max_iter = 200) {
    constexpr double kEps = std::numeric_limits<double>::epsilon();
    double fa = f(a);
    double fb = f(b);
    if (!std::isfinite(fa) || !std::isfinite(fb)) {
        throw InvalidArgument("brent: f is not finite at the bracket ends");
    }
    if ((fa > 0.0 && fb > 0.0) || (fa < 0.0 && fb < 0.0)) {
        throw InvalidArgument("brent: root is not bracketed");
    }
    if (fa == 0.0) {
        return {a, 0, true};
    }
    double c = b;
    double fc = fb;
    double d = b - a;
    double e = d;
    for (int iter = 1; iter <= max_iter; ++iter) {
        if ((fb > 0.0 && fc > 0.0) || (fb < 0.0 && fc < 0.0)) {
            c = a;
            fc = fa;
            d = b - a;
            e = d;
        }
        if (std::abs(fc) < std::abs(fb)) {
            a = b;
            b = c;
            c = a;
            fa = fb;
            fb = fc;
            fc = fa;
        }
        const double tol1 = 2.0 * kEps * std::abs(b) + 0.5 * xtol;
        const double xm = 0.5 * (c - b);
        if (std::abs(xm) <= tol1 || fb == 0.0) {
            return {b, iter, true};
        }
        if (std::abs(e) >= tol1 && std::abs(fa) > std::abs(fb)) {
            const double s = fb / fa;
            double p = 0.0;
            double q = 0.0;
            if (a == c) {
                p = 2.0 * xm * s; // secant
                q = 1.0 - s;
            } else {
                const double qq = fa / fc; // inverse quadratic interpolation
                const double r = fb / fc;
                p = s * (2.0 * xm * qq * (qq - r) - (b - a) * (r - 1.0));
                q = (qq - 1.0) * (r - 1.0) * (s - 1.0);
            }
            if (p > 0.0) {
                q = -q;
            }
            p = std::abs(p);
            const double min1 = 3.0 * xm * q - std::abs(tol1 * q);
            const double min2 = std::abs(e * q);
            if (2.0 * p < std::min(min1, min2)) {
                e = d;
                d = p / q;
            } else {
                d = xm;
                e = d;
            }
        } else {
            d = xm; // bisection
            e = d;
        }
        a = b;
        fa = fb;
        b += std::abs(d) > tol1 ? d : std::copysign(tol1, xm);
        fb = f(b);
    }
    return {b, max_iter, false};
}

} // namespace qe::core
