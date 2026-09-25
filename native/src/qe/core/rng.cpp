#include "qe/core/rng.hpp"

#include "qe/core/math.hpp"

namespace qe::core {

double Rng::normal() {
    return norm_inv(uniform_open());
}

} // namespace qe::core
