# Benchmark results

Files are named `phase<N>-<machine>-<native|managed>.<ext>`. Only commit results you ran yourself.
Each file records the exact toolchain and CPU in its header.

## Phase 1 (2026-09-25, cloud container)

**Machine:** Intel Xeon @ 2.80GHz, 4 vCPU, Ubuntu 24.04. GCC 13.3 Release build; .NET 10.0.12, ShortRun job.

| Measurement | Result | File |
|---|---|---|
| 1e6 BS prices, scalar C++ loop | 74.3 ms (13.6 M prices/s) | `phase1-cloud-linux-x64-native.json` |
| 1e6 BS prices, through the C ABI batch call | 80.9 ms (12.5 M/s) | same |
| One-element batch via the C ABI (native caller) | 0.10 µs | same |
| Bare P/Invoke transition (`qe_abi_version`) | 10.8 ns, 0 B allocated | `phase1-cloud-linux-x64-managed.md` / `.json` |
| One-element batch from C# (`QeEngine.PriceBlackScholes`) | 140 ns, 0 B allocated | same |
| 1e3 / 1e6 batch from C# | 81 µs / 78.5 ms | same |

**Reading these numbers:**
- Interop overhead is noise at batch sizes ≥ 1e3: the C# 1e6 batch matches the native one within run-to-run variance.
- On a single element, C# adds about 40 ns over native: SafeHandle ref-counting, pinning and argument checks.
- Pricing costs about 75–80 ns per option (exp, log, sqrt and two `erfc` per price, no vectorization). Phase 2 can revisit this (e.g. hoisting discount factors, SIMD `erfc`); it is not a Phase 1 gate item.

**How to reproduce:**
```
cmake --preset release && cmake --build --preset release
./build/release/bin/qe_bench --benchmark_out=bench/results/phase1-<machine>-native.json --benchmark_out_format=json --benchmark_repetitions=3 --benchmark_report_aggregates_only=true
dotnet run -c Release --project bench/QuantAnalyst.Bench -- --filter "*" --job short --exporters json markdown
```
