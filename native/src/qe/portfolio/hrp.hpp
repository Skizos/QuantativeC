#pragma once

#include "qe/core/linalg.hpp"

#include <vector>

namespace qe::portfolio {

struct HrpResult {
    Vector weights;
    std::vector<int> order; ///< quasi-diagonal leaf order from the single-linkage tree
};

/// Hierarchical Risk Parity (Lopez de Prado 2016): correlation distance d = sqrt((1 - rho) / 2),
/// Euclidean distance between columns of d, single-linkage clustering (merged pair recorded as
/// (smaller id, larger id) like scipy), quasi-diagonalization, recursive bisection with
/// inverse-variance weights. Long-only, fully invested. Requires strictly positive variances.
[[nodiscard]] HrpResult hrp(MatrixCRef cov);

} // namespace qe::portfolio
