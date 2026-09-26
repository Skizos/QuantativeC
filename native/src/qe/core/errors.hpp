#pragma once

#include <stdexcept>

namespace qe {

/// Input outside a function's domain. Mapped to QE_E_INVALID_ARG at the C ABI.
class InvalidArgument : public std::invalid_argument {
  public:
    using std::invalid_argument::invalid_argument;
};

/// Numerical failure from valid inputs (no convergence, loss of definiteness, non-finite result).
/// Mapped to QE_E_NUMERIC at the C ABI.
class NumericError : public std::runtime_error {
  public:
    using std::runtime_error::runtime_error;
};

} // namespace qe
