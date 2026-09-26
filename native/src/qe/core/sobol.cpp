#include "qe/core/sobol.hpp"

#include "qe/core/errors.hpp"
#include "qe/core/sobol_joe_kuo.inc"

#include <bit>
#include <string>

namespace qe::core {

int Sobol::max_dims() noexcept {
    return sobol_data::kDims;
}

Sobol::Sobol(int dims) : dims_(dims) {
    if (dims < 1 || dims > sobol_data::kDims) {
        throw InvalidArgument("Sobol: dims must be in [1, " + std::to_string(sobol_data::kDims) +
                              "]");
    }
    directions_.resize(static_cast<std::size_t>(dims));
    state_.assign(static_cast<std::size_t>(dims), 0U);

    for (int d = 0; d < dims; ++d) {
        std::array<std::uint32_t, kBits> m{};
        const auto du = static_cast<std::size_t>(d);
        if (d == 0) {
            m.fill(1U); // van der Corput
        } else {
            const std::uint32_t poly = sobol_data::kPoly[du];
            const int degree = static_cast<int>(std::bit_width(poly)) - 1;
            for (int j = 0; j < degree; ++j) {
                m[static_cast<std::size_t>(j)] = sobol_data::kInitialM[du * sobol_data::kMaxDegree +
                                                                       static_cast<std::size_t>(j)];
            }
            // Bratley & Fox (1988) recurrence, as in scipy's _sobol.pyx.
            for (int j = degree; j < kBits; ++j) {
                std::uint32_t next = m[static_cast<std::size_t>(j - degree)];
                std::uint32_t pow2 = 1U;
                for (int k = 0; k < degree; ++k) {
                    pow2 <<= 1U;
                    if (((poly >> static_cast<unsigned>(degree - 1 - k)) & 1U) != 0U) {
                        next ^= pow2 * m[static_cast<std::size_t>(j - k - 1)];
                    }
                }
                m[static_cast<std::size_t>(j)] = next;
            }
        }
        for (int j = 0; j < kBits; ++j) {
            directions_[du][static_cast<std::size_t>(j)] = m[static_cast<std::size_t>(j)]
                                                           << static_cast<unsigned>(kBits - 1 - j);
        }
    }
}

void Sobol::next(std::span<std::uint32_t> out) {
    if (out.size() != state_.size()) {
        throw InvalidArgument("Sobol::next: output span size must equal dims");
    }
    if (index_ >= (std::uint64_t{1} << kBits)) {
        throw NumericError("Sobol: sequence exhausted (2^32 points)");
    }
    for (std::size_t d = 0; d < state_.size(); ++d) {
        out[d] = state_[d];
    }
    // Gray code: flip the direction number of the lowest zero bit of the current index.
    const auto c = static_cast<std::size_t>(std::countr_one(index_));
    if (c < static_cast<std::size_t>(kBits)) {
        for (std::size_t d = 0; d < state_.size(); ++d) {
            state_[d] ^= directions_[d][c];
        }
    }
    ++index_;
}

} // namespace qe::core
