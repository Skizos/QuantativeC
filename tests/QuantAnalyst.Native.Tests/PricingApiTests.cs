namespace QuantAnalyst.Native.Tests;

public sealed class PricingApiTests : IDisposable
{
    private readonly QeEngine engine = QeEngine.Create(seed: 777);

    public void Dispose() => engine.Dispose();

    [Fact]
    public void Greeks_ThroughBinding_AndDegenerateElementFlagged()
    {
        BlackScholesInput[] inputs =
        [
            new(100, 100, 0.05, 0.0, 0.2, 1.0, OptionType.Call),
            new(100, 100, 0.05, 0.0, 0.2, 1.0, OptionType.Put),
            new(100, 100, 0.05, 0.0, 0.0, 1.0, OptionType.Call),
        ];
        var outputs = new BlackScholesGreeks[3];

        Assert.Equal(1, engine.ComputeGreeks(inputs, outputs));
        Assert.Equal(10.450583572185565, outputs[0].Price, tolerance: 1e-12);
        Assert.Equal(1.0, outputs[0].Delta - outputs[1].Delta, tolerance: 1e-14);
        Assert.True(outputs[0].Gamma > 0 && outputs[0].Vega > 0);
        Assert.Equal(QeStatus.InvalidArgument, outputs[2].Status);
        Assert.True(double.IsNaN(outputs[2].Vega));
    }

    [Fact]
    public void ImpliedVolatility_RoundTrips()
    {
        ImpliedVolInput[] inputs =
        [
            new(100, 100, 0.05, 0.0, 1.0, 10.450583572185565, OptionType.Call),
            new(100, 100, 0.05, 0.0, 1.0, 150.0, OptionType.Call),
        ];
        var outputs = new ImpliedVolOutput[2];

        Assert.Equal(1, engine.ImpliedVolatility(inputs, outputs));
        Assert.Equal(0.2, outputs[0].Volatility, tolerance: 1e-12);
        Assert.Equal(QeStatus.InvalidArgument, outputs[1].Status);
    }

    [Fact]
    public void Lattice_AmericanPutMatchesHullAndExceedsEuropean()
    {
        var put = new BlackScholesInput(50, 50, 0.10, 0.0, 0.40, 5.0 / 12.0, OptionType.Put);
        LatticeInput[] inputs = [new(put, 5, ExerciseStyle.American), new(put, 5, ExerciseStyle.European)];
        var outputs = new BlackScholesOutput[2];

        Assert.Equal(0, engine.PriceLattice(inputs, outputs));
        Assert.Equal(4.49, outputs[0].Price, tolerance: 5e-3);
        Assert.True(outputs[0].Price >= outputs[1].Price);
    }

    [Fact]
    public void MonteCarlo_WithinThreeSe_RecordsSeedAndPaths()
    {
        var option = new BlackScholesInput(100, 100, 0.05, 0.0, 0.2, 1.0, OptionType.Call);

        MonteCarloResult engineSeeded = engine.PriceMonteCarlo(option, new MonteCarloOptions { Antithetic = true, ControlVariate = true });
        MonteCarloResult explicitSeed = engine.PriceMonteCarlo(option, new MonteCarloOptions { Seed = 42, Sobol = true, Replications = 20 });

        Assert.Equal(777UL, engineSeeded.Seed);
        Assert.Equal(100_000, engineSeeded.Paths);
        Assert.True(Math.Abs(engineSeeded.Price - 10.450583572185565) <= 3 * engineSeeded.StdError);
        Assert.Equal(42UL, explicitSeed.Seed);
        Assert.True(Math.Abs(explicitSeed.Price - 10.450583572185565) <= 3 * explicitSeed.StdError);
    }

    [Fact]
    public void MonteCarlo_InvalidConfiguration_ThrowsWithNativeMessage()
    {
        var option = new BlackScholesInput(100, 100, 0.05, 0.0, 0.2, 1.0, OptionType.Call);

        QeException ex = Assert.Throws<QeException>(() =>
            engine.PriceMonteCarlo(option, new MonteCarloOptions { Paths = 1001, Antithetic = true }));
        Assert.Equal(QeStatus.InvalidArgument, ex.Status);
        Assert.Contains("even", ex.NativeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchLengthMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() => engine.ComputeGreeks(new BlackScholesInput[2], new BlackScholesGreeks[1]));
        Assert.Throws<ArgumentException>(() => engine.PriceLattice(new LatticeInput[1], new BlackScholesOutput[2]));
    }
}
