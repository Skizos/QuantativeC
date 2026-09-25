# Toolchain and dependency versions

- **Researched:** 2026-09-25.
- **Sources:** GitHub-hosted release metadata, which this container can reach. `learn.microsoft.com` and `vcpkg.io` are blocked by the egress proxy, so the Microsoft docs were read from the [dotnet/docs](https://github.com/dotnet/docs) repo at commit `9cb96f815fa5a76a5f884a3062a8bd558526ca20`.

## .NET

Source: [`dotnet/core` releases-index.json](https://github.com/dotnet/core/blob/3860c130e11c7feb24511e6ac3dd1694b25efda1/release-notes/releases-index.json), commit `3860c130e11c7feb24511e6ac3dd1694b25efda1`.

| Channel | Type | Phase | Latest runtime / SDK | Released | EOL |
|---|---|---|---|---|---|
| **10.0** | **LTS** | active | 10.0.12 / **10.0.401** | 2026-09-08 | **2028-11-14** |
| 11.0 | STS | go-live (RC1) | SDK 11.0.100-rc.1.26425.128 | 2026-09-08 | GA expected Nov 2026 |
| 9.0 | STS | maintenance | 9.0.318 SDK | 2026-09-08 | **2026-11-10** |
| 8.0 | LTS | maintenance | 8.0.425 SDK | 2026-09-08 | **2026-11-10** |

**Decision input:**
- Target `net10.0`.
- Pin the SDK with `global.json` → `"version": "10.0.100", "rollForward": "latestFeature"`. That accepts any installed 10.0.x feature band, always the newest, and never 11. Developer machines should install 10.0.401.
- Do **not** adopt .NET 11: it is an STS release and still at RC.
- This confirms Part A1 (.NET 8/9 end of support 2026-11-10).

**Availability in this cloud container:** there is no `dotnet` on PATH. Ubuntu 24.04 `noble-updates` has `dotnet-sdk-10.0` **10.0.104** (`apt-cache policy`). That is older than 10.0.401 but satisfies the `global.json` floor of 10.0.100 above. `rollForward` never goes *backward*, so pinning 10.0.401 would reject it. `dot.net/v1/dotnet-install.sh` returned a redirect, and `builds.dotnet.microsoft.com` is blocked. A SessionStart hook for cloud sessions is planned for Phase 1.

### P/Invoke source generation (`[LibraryImport]`)
- Docs: [`docs/standard/native-interop/pinvoke-source-generation.md`](https://github.com/dotnet/docs/blob/9cb96f815fa5a76a5f884a3062a8bd558526ca20/docs/standard/native-interop/pinvoke-source-generation.md).
  - `[LibraryImport]` on `static partial` methods generates the marshalling code at compile time: no runtime IL stub, AOT/trim-friendly.
  - Calling convention via `[UnmanagedCallConv]`.
  - Strings via `StringMarshalling.Utf8` (our rule: UTF-8 only).
  - Requires `AllowUnsafeBlocks=true` in the project.
- Analyzer **SYSLIB1054**: "Use `LibraryImportAttribute` instead of `DllImportAttribute` to generate p/invoke marshalling code at compile time" ([`syslib1050-1069.md`](https://github.com/dotnet/docs/blob/9cb96f815fa5a76a5f884a3062a8bd558526ca20/docs/fundamentals/syslib-diagnostics/syslib1050-1069.md)). We raise it to **error** in `.editorconfig`. With `TreatWarningsAsErrors` any `[DllImport]` already fails the build; the explicit severity documents intent.
- With only blittable types crossing, the generator emits a direct call, so the per-call overhead is just the managed↔native transition. Phase 1 benchmarks this.

### NuGet packages (latest stable on nuget.org flat container, 2026-09-25)

| Package | Version | Use |
|---|---|---|
| Microsoft.Extensions.Http.Resilience | 10.10.0 | retry, circuit breaker, timeout for **read** calls only |
| xunit.v3 | 4.0.1 | tests |
| Microsoft.NET.Test.Sdk | 18.10.1 | tests |
| AwesomeAssertions | 9.6.0 | assertions (Apache-2.0 fork of FluentAssertions, whose v8+ changed licence) |
| NetArchTest.Rules | 1.3.2 | architecture test: only `OrderGateway` touches order methods |
| TngTech.ArchUnitNET.xUnit | 0.13.4 | alternative to NetArchTest, richer rules. Pick one in Phase 6. |
| DuckDB.NET.Data.Full | 1.5.5 | history store (bundles native DuckDB) |
| Parquet.Net | 6.1.0 | Parquet import/export without DuckDB if needed |
| System.CommandLine | 2.0.12 | CLI |
| BenchmarkDotNet | 0.15.8 | managed/interop benchmarks |
| Serilog | 4.4.0 | optional, behind `ILogger`. A redaction enricher is needed either way. |

TOTP: implement RFC 6238 ourselves (~40 lines, HMAC-SHA1) and test against the RFC Appendix B vectors. That avoids a dependency in the credential path.

## C++ toolchain

| Tool | Required (Part A1) | Installed in this container | Note |
|---|---|---|---|
| CMake | ≥ 3.28 | **3.28.3** | ok |
| Ninja | any | **1.11.1** | ok |
| GCC | ≥ 13 | **13.3.0** | ok |
| Clang | ≥ 17 | **18.1.3** | ok. ASan/UBSan preset uses clang. |
| clang-format / clang-tidy | – | **18.1.3** | `.clang-format` is written against v18 keys. |
| MSVC | 2022 17.10+ | n/a (Linux) | Windows CI job only |

### vcpkg ports

Source: `microsoft/vcpkg` `master` at commit **`10541e317a660f4165ba4ac2851ab54a8d4577b1`**, which will be the manifest's `builtin-baseline`.

| Port | Version | Note |
|---|---|---|
| eigen3 | **5.0.1** | **Major version change from the long-lived 3.4.x.** Eigen 5 is what the baseline gives. Pin it with `overrides` and run the numeric test suite. If anything breaks, pin `3.4.0#…` explicitly (see ADR 0001 open item). |
| gtest | 1.18.0 | |
| benchmark | 1.9.5 | |
| quantlib | 1.42.1 | Optional feature `quantlib` in the manifest. Off by default because the build is heavy. Matches CLAUDE.md "1.42.x". |
| duckdb | 1.4.4#1 | Not needed natively; the .NET side uses DuckDB.NET. Listed for completeness. |

## Things that need your machine

- Windows: MSVC 2022 17.10+ and the .NET 10 SDK 10.0.401 via winget or Visual Studio.
- Record `dotnet --info`, `cmake --version` and `cl` versions into `docs/setup.md` during Phase 1.
