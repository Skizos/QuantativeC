cmake_minimum_required(VERSION 3.28)

# ---- Dependency source -----------------------------------------------------------------------
# Default: vcpkg manifest mode (vcpkg.json, pinned baseline). Cloud sessions whose network cannot
# download vcpkg sources export QE_USE_VCPKG=OFF and use system packages via the same
# find_package(... CONFIG) calls (see docs/setup.md).
# The QE_USE_VCPKG environment variable, when set, wins over the cached value.
option(QE_USE_VCPKG "Resolve C++ dependencies with vcpkg (requires VCPKG_ROOT)" ON)
if(DEFINED ENV{QE_USE_VCPKG})
    set(QE_USE_VCPKG "$ENV{QE_USE_VCPKG}" CACHE BOOL "Resolve C++ dependencies with vcpkg" FORCE)
endif()

if(QE_USE_VCPKG AND NOT CMAKE_TOOLCHAIN_FILE)
    if(NOT DEFINED ENV{VCPKG_ROOT})
        message(FATAL_ERROR
            "QE_USE_VCPKG=ON but VCPKG_ROOT is not set. Install vcpkg (docs/setup.md) or "
            "configure with -DQE_USE_VCPKG=OFF to use system packages.")
    endif()
    set(CMAKE_TOOLCHAIN_FILE "$ENV{VCPKG_ROOT}/scripts/buildsystems/vcpkg.cmake"
        CACHE FILEPATH "vcpkg toolchain")
endif()

project(QuantEngine VERSION 0.1.0 DESCRIPTION "QuantAnalyst native engine" LANGUAGES C CXX)

# ---- Language and visibility -----------------------------------------------------------------
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# Executables and DLLs side by side so tests find qe.dll on Windows.
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

# ---- Options -----------------------------------------------------------------------------------
option(QE_BUILD_TESTS "Build C++ unit and ABI tests" ON)
option(QE_BUILD_BENCHMARKS "Build Google Benchmark executables" ON)
option(QE_SANITIZE "Build with AddressSanitizer + UndefinedBehaviorSanitizer (clang/gcc)" OFF)
option(QE_WARNINGS_AS_ERRORS "Treat compiler warnings as errors" ON)
option(QE_STAGE_NATIVE "Copy the qe shared library to artifacts/native/<rid>/ for .NET" ON)
set(QE_NATIVE_STAGING_DIR "${PROJECT_SOURCE_DIR}/artifacts/native"
    CACHE PATH "Where the qe shared library is staged for the .NET projects")

list(APPEND CMAKE_MODULE_PATH "${PROJECT_SOURCE_DIR}/cmake")
include(QeWarnings)
include(QeSanitizers)
include(QeRid)

add_subdirectory(native)

if(QE_BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests/native)
endif()

if(QE_BUILD_BENCHMARKS)
    add_subdirectory(bench/native)
endif()
