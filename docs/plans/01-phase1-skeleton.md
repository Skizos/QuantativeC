# 01 — Phase 1: skeleton + interop spine

- **Status:** done in the cloud container (2026-09-25); the Windows leg is proven by CI (`build.ps1` on windows-2025)
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
dotnet build QuantAnalyst.sln && dotnet test --solution QuantAnalyst.sln
bash tests/hooks/block-live-trading.test.sh
```
The gate also requires the BS reference (call ≈ 10.4506, put ≈ 5.5735, tol 1e-4) to pass through the C# binding, and benchmark results to be saved in `bench/results/`.

## Results (cloud container, clean tree, 2026-09-25)

| Gate | Result |
|---|---|
| `ctest --preset dev` | 22/22 passed: 10 unit, 11 ABI, export-list check |
| `ctest --preset asan` (clang 18, ASan + UBSan, leak detection on) | 22/22 passed. LeakSanitizer was confirmed working with a deliberate leak. |
| `dotnet build` / `dotnet test --solution` | 0 warnings; 27/27 passed, including the BS reference through the C# binding (call 10.450583572185565, put 5.573526022256971) |
| Guardrail self-test | 23/23. The live session also refused an `echo` containing a live-mode flag, and blocked one of my own commits whose text quoted it. |
| `dotnet format --verify-no-changes`, `clang-format --dry-run -Werror` | clean |
| Benchmarks | `bench/results/` (1e6 BS: 74 ms scalar, 81 ms via ABI, 78.5 ms from C#; bare P/Invoke 10.8 ns) |
| CI [run 36168605334](https://github.com/Skizos/QuantativeC/actions/runs/36168605334) on `2228d5c` | All 4 jobs green: format + guardrails; ubuntu-24.04 (vcpkg gtest 1.18 / benchmark 1.9.5); **windows-2025 via `build.ps1`** (MSVC `/W4 /WX`, 0 warnings, 21/21 native, 27/27 managed); ASan + UBSan |

## Deviations from the master plan
- **`dotnet test` needs `--solution`.** xunit.v3 4.x runs on Microsoft.Testing.Platform, which the .NET 10 SDK requires opting into via `global.json`. In that mode `dotnet test` takes the solution through `--solution`. CLAUDE.md's command was updated.
- **The solution stays `QuantAnalyst.sln`.** The .NET 10 SDK defaults to `.slnx` (verified); I created the `.sln` explicitly with `--format sln`.
- **ELF exports need a version script.** On Linux, `libqe.so` uses a linker version script (`native/qe.map`), because libstdc++ template instantiations otherwise leak into the dynamic symbol table under clang Debug builds.
- **Native tests and benchmarks live in `tests/native` and `bench/native`**, following CLAUDE.md's top-level `tests/` and `bench/` layout.
- **The guardrail is literal:** any Bash command whose text contains a live-mode flag is blocked, including doc edits made through the shell. Edit docs that quote those flags with the file tools.
