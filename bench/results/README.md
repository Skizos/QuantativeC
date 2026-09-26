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

## Phase 2 (2026-09-25, same cloud container, GCC 13.3 Release, Eigen 3.4.0)

File: `phase2-cloud-linux-x64-native.json` (3 repetitions, means shown).

| Benchmark | Time | Notes |
|---|---|---|
| **MC European, 1e5 paths, plain** | 7.4 ms | SE 0.0467 (ATM call, S=K=100, 20 % vol, 1y) |
| MC 1e5, antithetic + control variate | 5.3 ms | SE 0.0086 (5.4x smaller) |
| MC 1e5, Sobol RQMC (16 digital shifts) | 6.2 ms | SE 0.0017 (27x smaller) |
| Ledoit-Wolf covariance, 2520 x 30 / 2520 x 300 | 1.5 ms / 115 ms | 10 years of daily returns |
| Min-variance, 30 / 300 assets, bounds [0, 0.1] | 0.24 ms / 30 ms | 69 / 327 FISTA iterations |
| HRP, 30 / 300 assets | 0.03 ms / 20 ms | O(N^3) naive single linkage |
| MC VaR/ES, 30 assets, 1e5 paths | 190 ms | 3e6 normals via inverse CDF |
| Integer-lot rebalance, 30 assets | 2.7 µs | |
| 1e6 BS prices (for comparison with Phase 1) | 76 ms scalar / 81 ms ABI | unchanged |

**Lesson recorded:** with GCC and Google Benchmark 1.8.3, `benchmark::DoNotOptimize` on a single `double` lvalue clobbered the value read after the loop, so the SE counter showed 0. The fix is `DoNotOptimize` on the whole result struct, which uses a memory constraint (`bench/native/phase2_bench.cpp`). Timings were not affected.

## Phase 5 (2026-09-26, same cloud container, GCC 13.3 Release native, .NET 10.0.12, ShortRun job)

Files: `phase5-cloud-linux-x64-managed.json` / `.md`. Configuration: 10 years × 300 instruments of synthetic bars
(2520 × 300, seed 20260925), MA-cross(20, 100), limit orders, `avanza-small` costs.

| Benchmark | Mean | Allocated | Notes |
|---|---|---|---|
| **Full run** (`BacktestRunner.Run`) | 169 ms | 2.4 MB | 2520 native steps, 2519 decisions, order planning, 8 truncation replays, metrics |
| Same run without the leakage check | 77 ms | 0.37 MB | the replays cost about as much as the run itself |
| CLI end to end (`qa backtest run … --synthetic 300x2520`) | 0.83 s wall | | one run: process start, data generation, run, ledger append; logged as T000001 |

**Reading these numbers:**
- About 30 µs per bar for 300 instruments, including the strategy and order planning in .NET and one batched call
  into the engine per bar.
- BenchmarkDotNet repeats the run, so its iterations are timing-only and are not in the TrialLedger. The one CLI
  run of the same configuration is.
- That run lost 96 %: 25,412 fills × the 39 SEK minimum courtage on 3,333 SEK slices (1 MSEK over 300 names).
  This is a property of the cost model, not a bug: small positions are expensive at Avanza's minimum fee.

**How to reproduce:**
```
cmake --preset release && cmake --build --preset release
dotnet run -c Release --project bench/QuantAnalyst.Bench -- --filter "*BacktestBenchmarks*" --job short --exporters json markdown
```
