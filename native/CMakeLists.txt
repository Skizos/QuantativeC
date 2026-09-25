# qe_impl: all C++ engine code as a static library (unit tests link it directly).
add_library(qe_impl STATIC
    src/qe/core/interp.cpp
    src/qe/core/math.cpp
    src/qe/core/rng.cpp
    src/qe/core/sobol.cpp
    src/qe/core/stats.cpp
    src/qe/pricing/binomial.cpp
    src/qe/pricing/black_scholes.cpp
    src/qe/pricing/implied_vol.cpp
    src/qe/portfolio/hrp.cpp
    src/qe/portfolio/optimizers.cpp
    src/qe/portfolio/rebalance.cpp
    src/qe/pricing/monte_carlo.cpp
    src/qe/risk/covariance.cpp
    src/qe/risk/stress.cpp
    src/qe/risk/var_es.cpp
)
target_include_directories(qe_impl PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/src")
# Eigen: vcpkg baseline gives 5.0.1; Ubuntu packages give 3.4.0. Code must build with both (ADR 0001).
find_package(Eigen3 CONFIG REQUIRED)
target_link_libraries(qe_impl PUBLIC Eigen3::Eigen)
target_compile_definitions(qe_impl PUBLIC EIGEN_MPL2_ONLY)
target_compile_features(qe_impl PUBLIC cxx_std_20)
qe_set_warnings(qe_impl)

# qe: the shared library whose ONLY exports are declared in include/qe_api.h.
add_library(qe SHARED
    src/api/qe_api.cpp
    src/api/last_error.cpp
)
target_include_directories(qe
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
    PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")
target_compile_definitions(qe PRIVATE QE_BUILDING_DLL)
target_link_libraries(qe PRIVATE qe_impl)
qe_set_warnings(qe)

# On ELF, visibility attributes alone are not enough: libstdc++ declares namespace std with default
# visibility, so template instantiations would leak into the dynamic symbol table.
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    target_link_options(qe PRIVATE "LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/qe.map")
    set_property(TARGET qe APPEND PROPERTY LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/qe.map")
endif()

if(QE_STAGE_NATIVE)
    qe_detect_rid(QE_RID)
    set(_qe_stage "${QE_NATIVE_STAGING_DIR}/${QE_RID}")
    add_custom_command(TARGET qe POST_BUILD
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${_qe_stage}"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "$<TARGET_FILE:qe>" "${_qe_stage}/"
        COMMENT "Staging $<TARGET_FILE_NAME:qe> for .NET in ${_qe_stage}"
        VERBATIM)
endif()
