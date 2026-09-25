# ADR 0001 — Native interop: flat C ABI + `[LibraryImport]`

- **Status:** Proposed (2026-09-25), awaiting approval
- **Deciders:** project owner
- **Related:** CLAUDE.md "Interop rules", `docs/research/versions.md`

## Context

The heavy numerics live in C++20 (`qe::`): Monte Carlo, optimizers, covariance, and the backtest inner loop. Everything else lives in .NET 10: HTTP/auth/streaming, persistence, DI, the OMS, and the CLI.

We need a boundary that:
1. works on **Windows** (primary, MSVC) and **Linux** (CI/cloud, GCC/Clang)
2. has near-zero per-call overhead and supports batch calls over large arrays
3. cannot corrupt the managed heap, leak native memory, or let a C++ exception unwind into the CLR
4. is testable on both sides and fails loudly on version mismatch

## Decision

Expose **one flat C ABI** (`native/include/qe_api.h`, implemented in `native/src/api/qe_api.cpp`) from a shared library **`qe`** (`qe.dll` / `libqe.so`). Bind it from `QuantAnalyst.Native` with **source-generated `[LibraryImport]`** P/Invokes.

### ABI rules
- **Types:** only blittable types cross the boundary:
  - `int32_t`, `int64_t`, `double`, pointers, and `#pragma pack`-free `struct`s built from those, with static-asserted size and offsets
  - flags are `int32_t`, never `bool`
  - strings are UTF-8 `const char*` + length, in only; out strings use a caller buffer + required-length protocol
- **Return and error protocol:**
  - every export is `extern "C"`, `noexcept`, returns `qe_status` (int32 enum: `QE_OK=0`, `QE_E_INVALID_ARG`, `QE_E_NUMERIC`, `QE_E_OOM`, `QE_E_INTERNAL`, …), and delivers results via out-pointers
  - a catch-all `try { … } catch (...)` wrapper macro at every export converts exceptions into status codes
  - `qe_last_error(char* buf, int32_t cap, int32_t* needed)` returns a **thread-local** message for the last failing call on that thread
- **Handles:** opaque `qe_engine*` created by `qe_engine_create(const qe_engine_config*, qe_engine**)` and freed by `qe_engine_destroy`. On the managed side it is wrapped in `QeEngineHandle : SafeHandle`, which releases via `ReleaseHandle`. There are **no finalizers** on our own types.
- **Memory ownership:**
  - The **caller allocates** all input and output arrays. Managed code passes pinned spans (`fixed` / `Span<T>` → pointer + length).
  - Native code never stores a managed pointer beyond the call.
  - Native-owned results, e.g. an HRP dendrogram, use a result handle + `qe_result_destroy`.
- **Batching:** APIs take arrays, e.g. `qe_bs_price_batch(engine, const qe_bs_input* in, int64_t n, qe_bs_output* out)`. There are no per-scalar calls on hot paths.
- **Callbacks:** progress and cancellation use `delegate* unmanaged[Cdecl]<…>` + `[UnmanagedCallersOnly]` static methods. Native code checks a cancellation flag (`volatile int32_t*`) between batches.
- **Threading:**
  - An engine handle is safe for concurrent **read-only** calls.
  - Stateful objects (RNG streams, backtest sessions) are per-handle and not shared.
  - Per-thread deterministic seeds are derived as `seed ⊕ splitmix64(thread_index)`, and the seed is returned with every result.
- **Versioning:**
  - `qe_abi_version(int32_t* major, int32_t* minor)`
  - The managed side checks `major == expected && minor >= expected` at startup and throws `NativeAbiMismatchException` otherwise.
  - `QE_ABI_MAJOR` bumps on any breaking change.
- **Calling convention:** `QE_CALL` = `__cdecl` on Windows, default elsewhere. It is declared explicitly with `[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]`.
- **Export visibility:**
  - `QE_API` = `__declspec(dllexport/dllimport)` on Windows, `__attribute__((visibility("default")))` elsewhere
  - `-fvisibility=hidden` by default
  - on Linux, a CI test checks the exported symbol list against `qe_api.h`

### Managed side
- `[LibraryImport("qe", StringMarshalling = StringMarshalling.Utf8)]` on `static partial` methods, in a single `internal static partial class QeNative`.
- **SYSLIB1054 = error** in `.editorconfig`, so any `[DllImport]` fails the build.
- **`NativeLibrary.SetDllImportResolver`** loads from `runtimes/<rid>/native/`, with RIDs `win-x64` and `linux-x64`. The native build output is copied there by an MSBuild target, so `dotnet test` works without extra PATH setup.
- A `QeStatus → exception` mapper. Every wrapper checks the status and pulls `qe_last_error` on failure.
- **Layout tests** on both sides:
  - C++: `static_assert(sizeof/offsetof)`, also exported via `qe_layout_info` for runtime comparison
  - C#: `Unsafe.SizeOf<T>()` / `Marshal.OffsetOf` must match `qe_layout_info`

### Build
- CMake ≥ 3.28, presets `dev`, `release`, `asan` (clang + ASan/UBSan, Linux), `msvc-dev`, `msvc-release`.
- vcpkg manifest mode with `builtin-baseline 10541e317a660f4165ba4ac2851ab54a8d4577b1`.
- Warnings: `-Wall -Wextra -Wpedantic -Werror` (`/W4 /WX /permissive-` on MSVC); clang-tidy in CI.

## Alternatives considered

| Option | Why not |
|---|---|
| **C++/CLI** | Windows-only. It would split the codebase from Linux CI and cloud sessions, and it is not AOT-friendly. Rejected, as the spec requires. |
| `[DllImport]` (runtime IL stubs) | Works, but generates stubs at runtime and is not trim/AOT-friendly. SYSLIB1054 flags it. `[LibraryImport]` is the supported path since .NET 7. |
| COM / WinRT | Windows-centric, heavy, and brings its own lifetime rules. |
| Out-of-process (gRPC / named pipes) | Strong isolation but serialization overhead per batch. That is overkill for a single-user desktop tool, though it stays a fallback if native crashes ever become a problem. |
| SWIG / ClangSharp-generated bindings | Generated surface area we don't control; a hand-written flat API is small enough. |
| Pure C# numerics (MathNet, System.Numerics.Tensors) | Viable for much of this. The spec chooses C++ for the MC/backtest inner loops, and Eigen/QuantLib availability tips it. We keep the ABI narrow so any single function could move to C# later. |

## Consequences

- **Positive:** cross-platform; near-zero call overhead (measured in Phase 1: per-call transition cost and 1e6-element batch throughput); failures surface as typed .NET exceptions; ASan/UBSan cover the native side.
- **Negative:** every new native feature needs C header, C++ implementation, C# binding and layout test. That is deliberate friction, reduced with a small code template.
- **Operational:** `qe.dll` must ship next to the app for its RID. The startup ABI check makes mismatches obvious.

## Open items

1. **Eigen version:** the vcpkg baseline resolves `eigen3` to **5.0.1**, a major bump from 3.4.x.
   - Plan: use 5.0.1 and run the full numeric suite in Phase 2.
   - If anything regresses, pin `eigen3` 3.4.0 via manifest `overrides` and record it here.
2. **QuantLib 1.42.1:** an optional vcpkg feature, off by default. It is only needed if we want cross-checks against QuantLib pricers in tests. Decide in Phase 2.
3. **Solution file name:** `QuantAnalyst.sln` vs `.slnx`; see the master plan, Phase 1 note.

## References

- P/Invoke source generation: <https://github.com/dotnet/docs/blob/9cb96f815fa5a76a5f884a3062a8bd558526ca20/docs/standard/native-interop/pinvoke-source-generation.md> (mirror of learn.microsoft.com "Source generation for platform invokes")
- SYSLIB1050–1069 diagnostics (SYSLIB1054): <https://github.com/dotnet/docs/blob/9cb96f815fa5a76a5f884a3062a8bd558526ca20/docs/fundamentals/syslib-diagnostics/syslib1050-1069.md>
- .NET release/support data: <https://github.com/dotnet/core/blob/3860c130e11c7feb24511e6ac3dd1694b25efda1/release-notes/releases-index.json>
- vcpkg ports at baseline: <https://github.com/microsoft/vcpkg/tree/10541e317a660f4165ba4ac2851ab54a8d4577b1/ports>
