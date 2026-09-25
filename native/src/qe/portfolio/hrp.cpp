#include "qe/portfolio/hrp.hpp"

#include "qe/core/errors.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>

namespace qe::portfolio {
namespace {

double cluster_variance(MatrixCRef cov, const std::vector<int>& items) {
    const auto k = static_cast<Eigen::Index>(items.size());
    Matrix sub(k, k);
    for (Eigen::Index a = 0; a < k; ++a) {
        for (Eigen::Index b = 0; b < k; ++b) {
            sub(a, b) = cov(items[static_cast<std::size_t>(a)], items[static_cast<std::size_t>(b)]);
        }
    }
    Vector ivp = sub.diagonal().cwiseInverse();
    ivp /= ivp.sum();
    return ivp.dot(sub * ivp);
}

} // namespace

HrpResult hrp(MatrixCRef cov) {
    const Eigen::Index n = cov.rows();
    if (n < 1 || cov.cols() != n || !cov.allFinite()) {
        throw InvalidArgument("cov must be a finite non-empty square matrix");
    }
    if ((cov.diagonal().array() <= 0.0).any()) {
        throw InvalidArgument("HRP needs strictly positive variances");
    }
    HrpResult result;
    if (n == 1) {
        result.weights = Vector::Ones(1);
        result.order = {0};
        return result;
    }

    // 1. Correlation distance and distance between distance columns.
    const Vector sd = cov.diagonal().cwiseSqrt();
    const Matrix corr = sd.cwiseInverse().asDiagonal() * cov * sd.cwiseInverse().asDiagonal();
    const Matrix dist = ((1.0 - corr.array()) / 2.0).max(0.0).sqrt().matrix();
    Matrix d(n, n);
    for (Eigen::Index i = 0; i < n; ++i) {
        for (Eigen::Index j = 0; j < n; ++j) {
            d(i, j) = (dist.col(i) - dist.col(j)).norm();
        }
    }

    // 2. Single-linkage agglomeration. Clusters 0..n-1 are leaves; merge s creates id n + s.
    const auto nn = static_cast<std::size_t>(n);
    std::vector<int> id(nn);
    std::vector<bool> active(nn, true);
    for (std::size_t i = 0; i < nn; ++i) {
        id[i] = static_cast<int>(i);
    }
    std::vector<std::array<int, 2>> merges;
    merges.reserve(nn - 1);
    Matrix work = d;
    for (std::size_t step = 0; step + 1 < nn; ++step) {
        double best = std::numeric_limits<double>::infinity();
        std::size_t ba = 0;
        std::size_t bb = 0;
        for (std::size_t a = 0; a < nn; ++a) {
            if (!active[a]) {
                continue;
            }
            for (std::size_t b = a + 1; b < nn; ++b) {
                const double dab = work(static_cast<Eigen::Index>(a), static_cast<Eigen::Index>(b));
                if (active[b] && dab < best) {
                    best = dab;
                    ba = a;
                    bb = b;
                }
            }
        }
        merges.push_back({std::min(id[ba], id[bb]), std::max(id[ba], id[bb])});
        // Slot ba holds the merged cluster; single linkage keeps the minimum distance.
        for (std::size_t k = 0; k < nn; ++k) {
            const auto ek = static_cast<Eigen::Index>(k);
            const auto ea = static_cast<Eigen::Index>(ba);
            const auto eb = static_cast<Eigen::Index>(bb);
            const double m = std::min(work(ea, ek), work(eb, ek));
            work(ea, ek) = m;
            work(ek, ea) = m;
        }
        active[bb] = false;
        id[ba] = static_cast<int>(nn + step);
    }

    // 3. Quasi-diagonalization: expand the root depth-first, left child first.
    std::vector<int> order{merges.back()[0], merges.back()[1]};
    while (*std::max_element(order.begin(), order.end()) >= static_cast<int>(n)) {
        std::vector<int> expanded;
        expanded.reserve(order.size() + 1);
        for (const int c : order) {
            if (c >= static_cast<int>(n)) {
                const auto& m = merges[static_cast<std::size_t>(c) - nn];
                expanded.push_back(m[0]);
                expanded.push_back(m[1]);
            } else {
                expanded.push_back(c);
            }
        }
        order = std::move(expanded);
    }

    // 4. Recursive bisection.
    Vector w = Vector::Ones(n);
    std::vector<std::vector<int>> clusters{order};
    while (!clusters.empty()) {
        std::vector<std::vector<int>> halves;
        for (const auto& c : clusters) {
            if (c.size() > 1) {
                const auto mid = static_cast<std::ptrdiff_t>(c.size() / 2);
                halves.emplace_back(c.begin(), c.begin() + mid);
                halves.emplace_back(c.begin() + mid, c.end());
            }
        }
        for (std::size_t i = 0; i + 1 < halves.size(); i += 2) {
            const double v0 = cluster_variance(cov, halves[i]);
            const double v1 = cluster_variance(cov, halves[i + 1]);
            const double alpha = 1.0 - v0 / (v0 + v1);
            for (const int k : halves[i]) {
                w(k) *= alpha;
            }
            for (const int k : halves[i + 1]) {
                w(k) *= 1.0 - alpha;
            }
        }
        clusters = std::move(halves);
    }
    result.weights = std::move(w);
    result.order = std::move(order);
    return result;
}

} // namespace qe::portfolio
