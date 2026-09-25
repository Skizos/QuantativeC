#include "qe/risk/stress.hpp"

#include "qe/core/errors.hpp"

namespace qe::risk {

Vector betas(MatrixCRef returns, std::span<const double> index) {
    const Eigen::Index t = returns.rows();
    if (t < 2 || returns.cols() < 1 || static_cast<std::size_t>(t) != index.size()) {
        throw InvalidArgument("betas: need T >= 2 rows and an index series of length T");
    }
    const Eigen::Map<const Vector> idx(index.data(), t);
    if (!returns.allFinite() || !idx.allFinite()) {
        throw InvalidArgument("betas: inputs must be finite");
    }
    const Vector idx_c = idx.array() - idx.mean();
    const double var_idx = idx_c.squaredNorm();
    if (var_idx <= 0.0) {
        throw InvalidArgument("betas: index has zero variance");
    }
    const Matrix x = returns.rowwise() - returns.colwise().mean();
    return (x.transpose() * idx_c) / var_idx;
}

Vector stress_pnl(VectorCRef values, MatrixCRef shocks) {
    if (shocks.cols() != values.size() || values.size() < 1 || shocks.rows() < 1) {
        throw InvalidArgument("stress: shocks must be S x N with N = number of positions");
    }
    if (!values.allFinite() || !shocks.allFinite()) {
        throw InvalidArgument("stress: inputs must be finite");
    }
    return shocks * values;
}

} // namespace qe::risk
