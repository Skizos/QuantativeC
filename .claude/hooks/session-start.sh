#!/bin/bash
# SessionStart hook for Claude Code on the web (cloud containers only).
# Installs the toolchain so the CLAUDE.md commands work:
#   cmake --preset dev && cmake --build --preset dev && ctest --preset dev --output-on-failure
#   dotnet build QuantAnalyst.sln && dotnet test --solution QuantAnalyst.sln
# The cloud egress proxy blocks GitHub archive/release downloads, so vcpkg cannot fetch port
# sources here: C++ dependencies come from Ubuntu packages instead (QE_USE_VCPKG=OFF).
# Idempotent and non-interactive. Local machines use vcpkg (docs/setup.md) and skip this hook.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
    exit 0
fi

log() { echo "[session-start] $*" >&2; }

packages=(
    dotnet-sdk-10.0      # .NET 10 LTS SDK (global.json floor 10.0.100, latestFeature)
    cmake ninja-build    # CMake >= 3.28 presets, Ninja generator
    g++ clang            # dev/release (GCC) and asan (clang) presets
    libclang-rt-18-dev   # clang ASan/UBSan runtimes
    clang-format         # format hook + CI format check
    libgtest-dev libgmock-dev libbenchmark-dev libeigen3-dev
)

missing=()
for pkg in "${packages[@]}"; do
    if ! dpkg-query -W -f='${Status}' "$pkg" 2>/dev/null | grep -q "install ok installed"; then
        missing+=("$pkg")
    fi
done

if [ "${#missing[@]}" -gt 0 ]; then
    log "installing: ${missing[*]}"
    export DEBIAN_FRONTEND=noninteractive
    # Stale package lists produce 404s for superseded .debs; always refresh first.
    apt-get update -q >/dev/null 2>&1 || log "apt-get update reported errors (unreachable third-party repos?); continuing"
    apt-get install -y -q --no-install-recommends "${missing[@]}" >/dev/null
else
    log "apt packages already installed"
fi

if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
    {
        echo 'export QE_USE_VCPKG=OFF'
        echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1'
        echo 'export DOTNET_NOLOGO=1'
    } >> "$CLAUDE_ENV_FILE"
fi

# Warm the NuGet cache (nuget.org is reachable); the container snapshot keeps it.
cd "${CLAUDE_PROJECT_DIR:-$(pwd)}"
if [ -f QuantAnalyst.sln ]; then
    log "restoring NuGet packages"
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 dotnet restore QuantAnalyst.sln -v q >/dev/null
fi

log "done: dotnet $(dotnet --version), cmake $(cmake --version | head -n1 | awk '{print $3}'), QE_USE_VCPKG=OFF"
