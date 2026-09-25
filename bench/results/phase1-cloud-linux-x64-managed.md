```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.112
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method         | Mean             | Error            | StdDev           | Ratio        | RatioSD    | Allocated | Alloc Ratio |
|--------------- |-----------------:|-----------------:|-----------------:|-------------:|-----------:|----------:|------------:|
| AbiVersionCall |         10.82 ns |         14.83 ns |         0.813 ns |         1.00 |       0.09 |         - |          NA |
| PriceBatch1    |        139.79 ns |         91.87 ns |         5.036 ns |        12.97 |       0.91 |         - |          NA |
| PriceBatch1K   |     81,052.84 ns |     16,169.88 ns |       886.326 ns |     7,520.01 |     474.57 |         - |          NA |
| PriceBatch1M   | 78,536,733.81 ns | 29,020,944.49 ns | 1,590,735.901 ns | 7,286,572.07 | 472,294.52 |         - |          NA |
