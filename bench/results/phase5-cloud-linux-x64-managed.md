```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.112
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                     | Mean      | Error     | StdDev    | Ratio | RatioSD | Allocated  | Alloc Ratio |
|--------------------------- |----------:|----------:|----------:|------:|--------:|-----------:|------------:|
| MaCross300x2520            | 168.70 ms | 247.98 ms | 13.593 ms |  1.00 |    0.10 | 2439.16 KB |        1.00 |
| MaCrossWithoutLeakageCheck |  76.50 ms |  90.37 ms |  4.954 ms |  0.46 |    0.04 |  372.63 KB |        0.15 |
