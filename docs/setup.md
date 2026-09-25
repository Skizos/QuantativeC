# Setup

How to get from a fresh clone to green tests on **Windows** (primary), Linux, and Claude Code cloud sessions. It also covers how to check the guardrail that stops Claude Code from trading live.

Pinned versions and where they come from: `docs/research/versions.md`.

---

## 1. Windows (primary)

### 1.1 Toolchain

Run in an elevated PowerShell. The winget package IDs were not verified from here; if one is not found, look it up with `winget search <name>`.

```powershell
winget install --id Microsoft.VisualStudio.2022.Community --override "--add Microsoft.VisualStudio.Workload.NativeDesktop --includeRecommended --passive"
winget install --id Microsoft.DotNet.SDK.10      # .NET 10 SDK; 10.0.401 or newer recommended
winget install --id Microsoft.PowerShell         # PowerShell 7.2+ (build.ps1 requires it)
winget install --id Git.Git                      # includes Git Bash, which Claude Code hooks need on Windows
winget install --id LLVM.LLVM                    # clang-format for the C++ format check
```

**Requirements:**
- Visual Studio 2022 **17.10+** with the MSVC x64 toolset. It also ships CMake ≥ 3.28 and Ninja.
- `global.json` accepts any .NET SDK **10.0.100+** within the 10.0 band and never rolls to 11.

### 1.2 vcpkg (C++ dependencies)

```powershell
git clone https://github.com/microsoft/vcpkg C:\dev\vcpkg
C:\dev\vcpkg\bootstrap-vcpkg.bat -disableMetrics
[Environment]::SetEnvironmentVariable('VCPKG_ROOT', 'C:\dev\vcpkg', 'User')   # then open a new shell
```

- `vcpkg.json` pins the port versions through `builtin-baseline`. The first configure builds gtest and Google Benchmark, which takes a few minutes.
- The MSVC presets use the `x64-windows-static-md` triplet, so no third-party DLLs are needed next to the executables.

### 1.3 Build and test

```powershell
./build.ps1                          # msvc-dev: native build + ctest, dotnet build + test, hook self-test
./build.ps1 -Preset msvc-release -Check
./build.ps1 -Bench                   # release native build + both benchmark suites
```

`build.ps1` enters the Visual Studio x64 developer environment itself (via `vswhere`) when `cl.exe` is not on `PATH`.

Running the steps by hand from a "x64 Native Tools" / Developer PowerShell:
```powershell
cmake --preset msvc-dev; cmake --build --preset msvc-dev; ctest --preset msvc-dev --output-on-failure
dotnet build QuantAnalyst.sln; dotnet test --solution QuantAnalyst.sln
```

**How the managed tests find the native library:**
1. CMake copies `qe.dll` to `artifacts/native/win-x64/`.
2. `QuantAnalyst.Native` ships that file as `runtimes/win-x64/native/qe.dll` in every output folder.
3. To test a different build, set `QE_NATIVE_PATH` to a file or directory.

---

## 2. Linux

```bash
# Option A: vcpkg (same pins as CI)
git clone https://github.com/microsoft/vcpkg ~/vcpkg && ~/vcpkg/bootstrap-vcpkg.sh -disableMetrics
export VCPKG_ROOT=~/vcpkg

# Option B: distro packages (Ubuntu 24.04: gtest 1.14, benchmark 1.8.3)
sudo apt-get install -y dotnet-sdk-10.0 cmake ninja-build g++ clang libclang-rt-18-dev clang-format \
    libgtest-dev libgmock-dev libbenchmark-dev libeigen3-dev
export QE_USE_VCPKG=OFF

./build.sh                    # dev preset + managed tests + hook self-test
./build.sh --preset asan      # ASan + UBSan native tests (clang)
./build.sh --check            # plus dotnet format / clang-format verification
```

`QE_USE_VCPKG` in the environment **overrides** the value cached in the CMake build directory, so switching dependency sources does not require deleting `build/`.

---

## 3. Claude Code cloud sessions

`.claude/hooks/session-start.sh` runs at the start of every cloud session, and only when `CLAUDE_CODE_REMOTE=true`. It:
- installs the apt packages listed in §2 (option B)
- writes `QE_USE_VCPKG=OFF` to the session environment
- warms the NuGet cache

**Why not vcpkg there:** the cloud egress proxy blocks GitHub archive and release downloads (HTTP 403), so vcpkg cannot fetch port sources. `git clone` and nuget.org do work.

**Other hosts blocked by the proxy** (see `docs/research/*.md`):
- avanza.se
- nasdaq.com
- eur-lex.europa.eu
- learn.microsoft.com

You can allow them in the cloud environment's network settings if you want Claude to verify sources directly.

---

## 4. Verifying the live-trading guardrail (do this on every new machine)

Claude Code must never run QuantAnalyst in **Confirm** or **Auto** mode, or call an Avanza order endpoint (CLAUDE.md, "Absolute safety rules"). The hard guarantee is the `PreToolUse` hook `.claude/hooks/block-live-trading.sh`, registered in `.claude/settings.json` for every `Bash` tool call. It blocks commands (exit code 2) that:
- pass `--mode Confirm|Auto`, `--mode=auto`, `-m auto`, …
- set `TRADING__MODE` / `Trading:Mode` to Confirm/Auto, including `dotnet user-secrets set`
- reference Avanza order routes (`order-entry/order`, `rest/order/{new,modify,delete}`, `stoploss/{new,modify}`, `fund-order-page/{buy,sell}`)

**Check it in three steps:**
1. **Self-test:** `bash tests/hooks/block-live-trading.test.sh`. Expect 23 lines starting with `ok` and exit code 0. CI runs this on every push.
2. **Registration:** in Claude Code, run `/hooks` and confirm there is a `PreToolUse` entry for `Bash` pointing to `block-live-trading.sh`.
3. **Live refusal:** ask Claude Code to run `echo qa paper run --mode Auto`. The tool call must be refused with `BLOCKED by .claude/hooks/block-live-trading.sh: …`. If it runs, stop, because the hook is not active.

**Keep these true:**
- Never add a permission-allowlist entry that starts the app in Confirm/Auto.
- Never weaken the hook without adding a failing case to its self-test first.
- On Windows, Claude Code runs hooks with Git Bash; without Git Bash the hook cannot run.

---

## 5. Secrets (Phase 3 onwards)

Nothing in Phase 1–2 needs credentials. The following comes into play in Phase 3:
- **Store:** Avanza credentials (`AVANZA__USERNAME`, `AVANZA__PASSWORD`, `AVANZA__TOTPSECRET`, `AVANZA__ALLOWEDACCOUNTIDS`) go in **Windows Credential Manager**. `dotnet user-secrets` is the development fallback.
- **Never:** in the repo, in `.env` files Claude can read, or in logs.
- **Claude Code access:** `.claude/settings.json` denies reading `.env*`, `secrets/**` and `recordings/live/**`.

---

## 6. Toolchain record

| Component | Cloud container (2026-09-25) | Your Windows machine |
|---|---|---|
| .NET SDK | 10.0.112 (Ubuntu package), runtime 10.0.12 | *fill in from `dotnet --info`* |
| CMake / Ninja | 3.28.3 / 1.11.1 | *`cmake --version`* |
| C++ compilers | GCC 13.3.0, Clang 18.1.3 | *`cl` banner* |
| clang-format | 18.1.3 | |
| gtest / benchmark | 1.14.0 / 1.8.3 (Ubuntu packages) | vcpkg baseline: 1.18.0 / 1.9.5 |

## 7. Troubleshooting

| Symptom | Fix |
|---|---|
| `DllNotFoundException: Could not find the qe native library` | Build the native library first (`cmake --preset dev && cmake --build --preset dev`, or `./build.ps1`). The message lists every path searched. |
| Build warning `QE0001` | Same cause: no staged library under `artifacts/native/`. |
| `QE_USE_VCPKG=ON but VCPKG_ROOT is not set` | Install vcpkg (§1.2 or §2), or set `QE_USE_VCPKG=OFF` to use installed packages. |
| `Specifying a solution for 'dotnet test' should be via '--solution'` | Use `dotnet test --solution QuantAnalyst.sln`. Tests run on Microsoft.Testing.Platform (`global.json`). |
| ASan link error `libclang_rt.asan… not found` | Install `libclang-rt-18-dev`. |
| `NativeAbiMismatchException` | The staged `qe` library is older or newer than `QuantAnalyst.Native`. Rebuild both. |
