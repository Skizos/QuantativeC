#pragma once

#include <Eigen/Core>

namespace qe {

/// Dense column-major matrix/vector types used by the risk and portfolio modules.
/// The C ABI passes row-major arrays; api/ maps them into these types.
using Matrix = Eigen::MatrixXd;
using Vector = Eigen::VectorXd;
using MatrixCRef = Eigen::Ref<const Eigen::MatrixXd>;
using VectorCRef = Eigen::Ref<const Eigen::VectorXd>;

} // namespace qe
