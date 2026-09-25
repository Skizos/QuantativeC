using BenchmarkDotNet.Attributes;
using QuantAnalyst.Native;
using QuantAnalyst.Native.Interop;

namespace QuantAnalyst.Bench;

/// <summary>
/// Phase 1 gate: managed-to-native call overhead and batch throughput through the C ABI.
/// <see cref="AbiVersionCall"/> is the bare transition cost (two out-pointers, no work).
/// </summary>
[MemoryDiagnoser]
public class InteropOverheadBenchmarks
{
    private QeEngine engine = null!;
    private BlackScholesInput[] inputs1 = [];
    private BlackScholesOutput[] outputs1 = [];
    private BlackScholesInput[] inputs1K = [];
    private BlackScholesOutput[] outputs1K = [];
    private BlackScholesInput[] inputs1M = [];
    private BlackScholesOutput[] outputs1M = [];

    [GlobalSetup]
    public void Setup()
    {
        engine = QeEngine.Create(seed: 1);
        inputs1 = MakeInputs(1);
        outputs1 = new BlackScholesOutput[1];
        inputs1K = MakeInputs(1_000);
        outputs1K = new BlackScholesOutput[1_000];
        inputs1M = MakeInputs(1_000_000);
        outputs1M = new BlackScholesOutput[1_000_000];
    }

    [GlobalCleanup]
    public void Cleanup() => engine.Dispose();

    [Benchmark(Baseline = true)]
    public unsafe int AbiVersionCall()
    {
        int major;
        int minor;
        _ = QeNative.AbiVersion(&major, &minor);
        return major + minor;
    }

    [Benchmark]
    public long PriceBatch1() => engine.PriceBlackScholes(inputs1, outputs1);

    [Benchmark]
    public long PriceBatch1K() => engine.PriceBlackScholes(inputs1K, outputs1K);

    [Benchmark]
    public long PriceBatch1M() => engine.PriceBlackScholes(inputs1M, outputs1M);

    private static BlackScholesInput[] MakeInputs(int n)
    {
        var inputs = new BlackScholesInput[n];
        for (int i = 0; i < n; i++)
        {
            inputs[i] = new(50 + (i % 1000 * 0.1), 100, 0.03, 0.01, 0.25, 0.5, i % 2 == 0 ? OptionType.Call : OptionType.Put);
        }

        return inputs;
    }
}
