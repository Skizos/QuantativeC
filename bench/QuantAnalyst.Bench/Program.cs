using BenchmarkDotNet.Running;

namespace QuantAnalyst.Bench;

/// <summary>
/// Entry point. Example (Phase 1 gate):
/// dotnet run -c Release --project bench/QuantAnalyst.Bench -- --filter "*" --job short --exporters json markdown
/// </summary>
public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
