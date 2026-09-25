namespace QuantAnalyst.Native.Tests;

public sealed class BlackScholesTests : IDisposable
{
    private readonly QeEngine engine = QeEngine.Create(seed: 42);

    public void Dispose() => engine.Dispose();

    // Spec reference (master prompt <verification_requirements>), through the C# binding.
    [Fact]
    public void SpecReference_CallAndPut_ThroughBinding()
    {
        BlackScholesInput[] inputs =
        [
            new(100, 100, 0.05, 0.0, 0.20, 1.0, OptionType.Call),
            new(100, 100, 0.05, 0.0, 0.20, 1.0, OptionType.Put),
        ];
        var outputs = new BlackScholesOutput[inputs.Length];

        long failed = engine.PriceBlackScholes(inputs, outputs);

        Assert.Equal(0, failed);
        Assert.All(outputs, o => Assert.Equal(QeStatus.Ok, o.Status));
        Assert.Equal(10.4506, outputs[0].Price, tolerance: 1e-4);
        Assert.Equal(5.5735, outputs[1].Price, tolerance: 1e-4);
        Assert.Equal(10.450583572185565, outputs[0].Price, tolerance: 1e-12);
        Assert.Equal(5.573526022256971, outputs[1].Price, tolerance: 1e-12);
    }

    [Fact]
    public void PutCallParity_HoldsAcrossGrid()
    {
        var inputs = new List<BlackScholesInput>();
        foreach (double s in new[] { 60.0, 100.0, 180.0 })
        {
            foreach (double vol in new[] { 0.1, 0.35, 0.9 })
            {
                foreach (double t in new[] { 0.05, 1.0, 3.0 })
                {
                    inputs.Add(new(s, 100, 0.02, 0.01, vol, t, OptionType.Call));
                    inputs.Add(new(s, 100, 0.02, 0.01, vol, t, OptionType.Put));
                }
            }
        }

        var outputs = new BlackScholesOutput[inputs.Count];
        Assert.Equal(0, engine.PriceBlackScholes(inputs.ToArray(), outputs));

        for (int i = 0; i < inputs.Count; i += 2)
        {
            BlackScholesInput p = inputs[i];
            double parity = (p.Spot * Math.Exp(-p.DividendYield * p.ExpiryYears)) -
                            (p.Strike * Math.Exp(-p.Rate * p.ExpiryYears));
            Assert.Equal(parity, outputs[i].Price - outputs[i + 1].Price, tolerance: 1e-10);
        }
    }

    [Fact]
    public void InvalidElements_AreReportedPerElement_WithoutFailingTheBatch()
    {
        BlackScholesInput[] inputs =
        [
            new(100, 100, 0.05, 0.0, 0.2, 1.0, OptionType.Call),
            new(-5, 100, 0.05, 0.0, 0.2, 1.0, OptionType.Call),
            new(100, 100, 0.05, 0.0, double.NaN, 1.0, OptionType.Put),
            new(100, 100, 0.05, 0.0, 0.2, 1.0, (OptionType)9),
        ];
        var outputs = new BlackScholesOutput[inputs.Length];

        long failed = engine.PriceBlackScholes(inputs, outputs);

        Assert.Equal(3, failed);
        Assert.True(outputs[0].IsOk);
        for (int i = 1; i < outputs.Length; i++)
        {
            Assert.Equal(QeStatus.InvalidArgument, outputs[i].Status);
            Assert.True(double.IsNaN(outputs[i].Price));
        }
    }

    [Fact]
    public void EmptyBatch_Succeeds()
    {
        Assert.Equal(0, engine.PriceBlackScholes([], []));
    }

    [Fact]
    public void LengthMismatch_ThrowsArgumentException()
    {
        var inputs = new BlackScholesInput[2];
        var outputs = new BlackScholesOutput[1];

        ArgumentException ex = Assert.Throws<ArgumentException>(() => engine.PriceBlackScholes(inputs, outputs));
        Assert.Equal("outputs", ex.ParamName);
    }

    [Fact]
    public void LargeBatch_IsDeterministic()
    {
        const int n = 200_000;
        var inputs = new BlackScholesInput[n];
        for (int i = 0; i < n; i++)
        {
            inputs[i] = new(50 + (i % 1000 * 0.1), 100, 0.03, 0.01, 0.25, 0.5, i % 2 == 0 ? OptionType.Call : OptionType.Put);
        }

        var first = new BlackScholesOutput[n];
        var second = new BlackScholesOutput[n];
        Assert.Equal(0, engine.PriceBlackScholes(inputs, first));
        Assert.Equal(0, engine.PriceBlackScholes(inputs, second));
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ConcurrentPricing_OnOneEngine_IsConsistent()
    {
        BlackScholesInput[] inputs = [new(100, 100, 0.05, 0.0, 0.2, 1.0, OptionType.Call)];

        double[] prices = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            var outputs = new BlackScholesOutput[1];
            engine.PriceBlackScholes(inputs, outputs);
            return outputs[0].Price;
        })));

        Assert.All(prices, p => Assert.Equal(10.450583572185565, p, tolerance: 1e-12));
    }
}
