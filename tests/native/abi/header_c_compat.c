/* Compiled as C11: proves qe_api.h is plain C and its structs have the pinned sizes in C too. */
#include "qe_api.h"

#include <stddef.h>

_Static_assert(sizeof(qe_engine_config) == 16, "qe_engine_config size");
_Static_assert(sizeof(qe_bs_input) == 56, "qe_bs_input size");
_Static_assert(sizeof(qe_bs_output) == 16, "qe_bs_output size");
_Static_assert(sizeof(qe_struct_layout_info) == 80, "qe_struct_layout_info size");
_Static_assert(offsetof(qe_bs_input, option_type) == 48, "qe_bs_input.option_type offset");
/* ABI 1.1 */
_Static_assert(sizeof(qe_bs_greeks) == 56, "qe_bs_greeks size");
_Static_assert(sizeof(qe_iv_input) == 56 && sizeof(qe_iv_output) == 16, "iv sizes");
_Static_assert(sizeof(qe_lattice_input) == 64 && offsetof(qe_lattice_input, steps) == 56,
               "lattice");
_Static_assert(sizeof(qe_mc_config) == 32 && sizeof(qe_mc_result) == 32, "mc sizes");
_Static_assert(sizeof(qe_cov_config) == 16 && sizeof(qe_var_es) == 32, "risk sizes");
_Static_assert(sizeof(qe_opt_config) == 32 && sizeof(qe_opt_result) == 16, "opt sizes");
_Static_assert(sizeof(qe_rebalance_asset) == 32 && sizeof(qe_rebalance_config) == 48,
               "rebalance in");
_Static_assert(sizeof(qe_rebalance_trade) == 40 && sizeof(qe_rebalance_summary) == 40,
               "rebalance out");

int qe_c_compat_abi_major(void);

/* Called from abi_test.cpp so the C translation unit is linked and exercised. */
int qe_c_compat_abi_major(void) {
    int32_t major = -1;
    int32_t minor = -1;
    if (qe_abi_version(&major, &minor) != QE_OK) {
        return -1;
    }
    return (int)major;
}
