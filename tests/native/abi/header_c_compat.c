/* Compiled as C11: proves qe_api.h is plain C and its structs have the pinned sizes in C too. */
#include "qe_api.h"

#include <stddef.h>

_Static_assert(sizeof(qe_engine_config) == 16, "qe_engine_config size");
_Static_assert(sizeof(qe_bs_input) == 56, "qe_bs_input size");
_Static_assert(sizeof(qe_bs_output) == 16, "qe_bs_output size");
_Static_assert(sizeof(qe_struct_layout_info) == 80, "qe_struct_layout_info size");
_Static_assert(offsetof(qe_bs_input, option_type) == 48, "qe_bs_input.option_type offset");

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
