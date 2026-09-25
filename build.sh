#!/usr/bin/env bash
# One-command build + test for Linux/macOS and cloud sessions (CLAUDE.md "Commands: All").
#
#   ./build.sh                    dev preset: native build + ctest, dotnet build + test, hook self-test
#   ./build.sh --preset release   release native build (stages the release libqe for .NET)
#   ./build.sh --preset asan      ASan+UBSan native tests only (sanitized libqe is not loadable by .NET)
#   ./build.sh --check            also run format checks (dotnet format, clang-format)
#   ./build.sh --bench            also run native + managed benchmarks (release native build)
#   ./build.sh --no-native | --no-managed
#
# Dependencies come from vcpkg (VCPKG_ROOT) unless QE_USE_VCPKG=OFF (cloud sessions; docs/setup.md).
set -euo pipefail

preset=dev
native=1
managed=1
check=0
bench=0

usage() { sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
    case "$1" in
        --preset) preset="${2:?--preset needs a value}"; shift 2 ;;
        --no-native) native=0; shift ;;
        --no-managed) managed=0; shift ;;
        --check) check=1; shift ;;
        --bench) bench=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

cd "$(dirname "$0")"
step() { printf '\n==> %s\n' "$*"; }

if [ "$preset" = "asan" ] && [ "$managed" -eq 1 ]; then
    echo "note: the asan preset does not stage libqe for .NET; skipping managed steps." >&2
    managed=0
fi

step "Guardrail self-test (.claude/hooks/block-live-trading.sh)"
bash tests/hooks/block-live-trading.test.sh >/dev/null
echo "ok"

if [ "$native" -eq 1 ]; then
    step "Native: cmake --preset $preset"
    cmake --preset "$preset"
    cmake --build --preset "$preset"
    ctest --preset "$preset" --output-on-failure
fi

if [ "$managed" -eq 1 ]; then
    step "Managed: dotnet build + test"
    dotnet build QuantAnalyst.sln
    dotnet test --solution QuantAnalyst.sln
fi

if [ "$check" -eq 1 ]; then
    step "Format checks"
    dotnet format QuantAnalyst.sln --verify-no-changes -v q
    git ls-files -- '*.c' '*.cc' '*.cpp' '*.h' '*.hpp' | xargs clang-format --dry-run -Werror
    echo "ok"
fi

if [ "$bench" -eq 1 ]; then
    step "Benchmarks (release native build)"
    cmake --preset release
    cmake --build --preset release
    machine="$(hostname -s 2>/dev/null || echo local)-$(uname -s | tr '[:upper:]' '[:lower:]')-$(uname -m)"
    ./build/release/bin/qe_bench --benchmark_repetitions=3 --benchmark_report_aggregates_only=true \
        --benchmark_out="bench/results/local-${machine}-native.json" --benchmark_out_format=json
    dotnet run -c Release --project bench/QuantAnalyst.Bench -- --filter "*" --job short --exporters json markdown
    echo "native results: bench/results/local-${machine}-native.json; managed results: BenchmarkDotNet.Artifacts/results/"
fi

step "Done"
