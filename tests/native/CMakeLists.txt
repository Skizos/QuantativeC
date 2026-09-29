find_package(GTest CONFIG REQUIRED)
include(GoogleTest)

# Unit tests for C++ internals (static qe_impl).
add_executable(qe_unit_tests
    backtest/engine_test.cpp
    core/math_test.cpp
    core/root_test.cpp
    core/rng_test.cpp
    core/sobol_test.cpp
    core/stats_test.cpp
    pricing/black_scholes_test.cpp
    pricing/greeks_iv_test.cpp
    pricing/lattice_mc_test.cpp
    portfolio/optimizers_test.cpp
    portfolio/rebalance_test.cpp
    risk/covariance_test.cpp
    risk/var_es_test.cpp
)
target_link_libraries(qe_unit_tests PRIVATE qe_impl GTest::gtest_main)
# Generated reference values (tools/reference/phase2_reference.py) live under tests/native/reference.
target_include_directories(qe_unit_tests PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
qe_set_warnings(qe_unit_tests)
gtest_discover_tests(qe_unit_tests DISCOVERY_MODE PRE_TEST DISCOVERY_TIMEOUT 120 TEST_PREFIX "unit.")

# Allocation test: replaces the global operator new, so it gets its own binary and is skipped
# under sanitizers (they intercept the allocator).
if(NOT QE_SANITIZE)
    add_executable(qe_alloc_tests backtest/engine_alloc_test.cpp)
    target_link_libraries(qe_alloc_tests PRIVATE qe_impl GTest::gtest_main)
    qe_set_warnings(qe_alloc_tests)
    gtest_discover_tests(qe_alloc_tests DISCOVERY_MODE PRE_TEST DISCOVERY_TIMEOUT 120 TEST_PREFIX "unit.")
endif()

# ABI tests: only qe_api.h + the shared library, exactly like the .NET side sees it.
# header_c_compat.c proves qe_api.h is valid C11.
add_executable(qe_abi_tests
    abi/abi_test.cpp
    abi/abi_v11_test.cpp
    abi/abi_v12_test.cpp
    abi/abi_v13_test.cpp
    abi/abi_v14_test.cpp
    abi/header_c_compat.c
)
target_link_libraries(qe_abi_tests PRIVATE qe GTest::gtest_main)
target_include_directories(qe_abi_tests PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
qe_set_warnings(qe_abi_tests)
gtest_discover_tests(qe_abi_tests DISCOVERY_MODE PRE_TEST DISCOVERY_TIMEOUT 120 TEST_PREFIX "abi.")

# Export surface check (ELF only): the dynamic symbol table must match qe_api.h exactly.
if(CMAKE_SYSTEM_NAME STREQUAL "Linux" AND CMAKE_NM)
    add_test(NAME abi.exported_symbols_match_header
        COMMAND "${CMAKE_COMMAND}"
            "-DNM=${CMAKE_NM}"
            "-DLIBRARY=$<TARGET_FILE:qe>"
            "-DHEADER=${PROJECT_SOURCE_DIR}/native/include/qe_api.h"
            -P "${CMAKE_CURRENT_SOURCE_DIR}/check_exports.cmake")
endif()
