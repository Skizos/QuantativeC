# 01 — Phase 1: skeleton + interop spine

- **Status:** in progress (2026-09-25)
- **Scope:** master plan §4 Phase 1 and ADR 0001.

## Deliverables

| Area | Files |
|---|---|
| Native build | `CMakeLists.txt`, `CMakePresets.json` (dev, release, asan, msvc-dev, msvc-release), `vcpkg.json` (baseline `10541e31…`), `cmake/*.cmake` |
| C ABI | `native/include/qe_api.h`, `native/src/api/*` |
| C++ core | `native/src/qe/pricing/black_scholes.{hpp,cpp}` |
| C++ tests | `tests/native/`: `qe_unit_tests` (links the static `qe_impl`), `qe_abi_tests` (links the **shared** `qe` through `qe_api.h` only, plus a C11 compile of the header), and an export-list check on Linux |
| C++ bench | `bench/native/bs_bench.cpp` (Google Benchmark: 1e6 BS prices, scalar and through the ABI) |
| .NET | `QuantAnalyst.sln`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `src/QuantAnalyst.Native`, `tests/QuantAnalyst.Native.Tests`, `bench/QuantAnalyst.Bench` (BenchmarkDotNet interop overhead) |
| Tooling | `build.sh`, `build.ps1`, `.claude/hooks/session-start.sh`, `.github/workflows/ci.yml`, `docs/setup.md` |

## ABI v1.0 surface

```
qe_abi_version(int32* major, int32* minor)
qe_last_error(char* buf, int32 capacity, int32* required)          // thread-local, UTF-8, NUL-terminated
qe_struct_layout(int32 struct_id, qe_struct_layout_info* out)      // size/alignment/offsets for layout tests
qe_engine_create(const qe_engine_config* cfg /*nullable*/, qe_engine** out)
qe_engine_destroy(qe_engine* e /*nullable*/)
qe_bs_price_batch(const qe_engine* e, const qe_bs_input* in, qe_bs_output* out,
                  int64 count, int64* failed_count)                // per-element status; call status = call validity
```

## How the native library reaches .NET
1. CMake's post-build step copies `qe` into `artifacts/native/<rid>/`. The directory is git-ignored, and the ASan preset skips this step.
2. `QuantAnalyst.Native.csproj` links `artifacts/native/**` as `runtimes/<rid>/native/*` content, so every referencing project gets it in its output folder.
3. `QeNativeLibraryResolver` loads the library in this order: the `QE_NATIVE_PATH` override, then `runtimes/<rid>/native/`, then the app base directory, then default probing. If all fail, it throws `DllNotFoundException` listing every path it tried.

## Dependency sources
- **Your machine and CI:** vcpkg manifest mode. This is canonical; versions are pinned by the baseline.
- **Cloud sessions:** the egress proxy blocks GitHub archive and release downloads, so vcpkg cannot fetch sources there. The session-start hook installs Ubuntu's `libgtest-dev` (1.14.0) and `libbenchmark-dev` (1.8.3) and exports `QE_USE_VCPKG=OFF`. The same `find_package(... CONFIG)` calls work either way.

## Gate (commands whose output goes in the session)
```
cmake --preset dev && cmake --build --preset dev && ctest --preset dev --output-on-failure
cmake --preset asan && cmake --build --preset asan && ctest --preset asan --output-on-failure
dotnet build QuantAnalyst.sln && dotnet test QuantAnalyst.sln
bash tests/hooks/block-live-trading.test.sh
```
The gate also requires the BS reference (call ≈ 10.4506, put ≈ 5.5735, tol 1e-4) to pass through the C# binding, and benchmark results to be saved in `bench/results/`.
