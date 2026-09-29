using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Analytics.Tests;

/// <summary>
/// ADR 0005 / plan 16 step 4: every courtage class carries a foreign courtage, and a backtest charges a US or Canadian
/// share its market's courtage (native ABI 1.3), the minimum converted to SEK at the last fixing.
/// </summary>
public sealed class ForeignBacktestTests
{
    private static readonly CostModel StartLike = BacktestFixtures.Costs with
    {
        Name = "avanza-start",
        CourtageMin = 0m,
        CourtageRate = 0m,
        FxFeeRate = 0m,
        Foreign = new Dictionary<string, ForeignCourtage>(StringComparer.Ordinal) { ["USD"] = new(1m, 0.0025m) },
    };

    private static string RepoConfig
    {
        get
        {
            for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "QuantAnalyst.sln")))
                {
                    return Path.Combine(d.FullName, "config");
                }
            }

            throw new DirectoryNotFoundException("repository root not found");
        }
    }

    [Theory]
    [InlineData("avanza-start", 1, 0.0025)]
    [InlineData("avanza-mini", 1, 0.0025)]
    [InlineData("avanza-small", 6, 0.0015)]
    [InlineData("avanza-medium", 8, 0.00089)]
    [InlineData("avanza-fastpris", 12, 0.00079)]
    public void EveryClass_HasItsForeignCourtage_UnverifiedUntilChecked(string name, double min, double rate)
    {
        CostModel c = CostModel.Load(Path.Combine(RepoConfig, $"costs.{name}.json"));
        foreach (string currency in new[] { "USD", "CAD" })
        {
            Assert.Equal(new ForeignCourtage((decimal)min, (decimal)rate), c.ForeignFor(currency));
        }

        Assert.Null(c.ForeignVerifiedOn);
        Assert.Contains("handel-utland", c.ForeignSourceUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void CourtageIn_UsesTheSharesCurrency()
    {
        CostModel start = CostModel.Load(Path.Combine(RepoConfig, "costs.avanza-start.json"));
        Assert.Equal(0m, start.CourtageIn("SEK", 10_000m)); // Start is free in Stockholm
        Assert.Equal(1m, start.CourtageIn("USD", 100m)); // the 1 USD minimum
        Assert.Equal(2.5m, start.CourtageIn("USD", 1_000m)); // 0.25 %
        var ex = Assert.Throws<BacktestConfigException>(() => start.CourtageIn("EUR", 1_000m));
        Assert.Contains("no courtage for EUR shares", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AForeignCourtageForAnotherCurrency_IsRefused()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qa-foreign-costs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string text = File.ReadAllText(Path.Combine(RepoConfig, "costs.avanza-mini.json")).Replace("\"CAD\":", "\"EUR\":", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(dir, "costs.avanza-mini.json"), text);
            var ex = Assert.Throws<BacktestConfigException>(() => CostModel.Load(Path.Combine(dir, "costs.avanza-mini.json")));
            Assert.Contains("foreign_courtage.EUR is not a foreign currency the program trades (USD, CAD)", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AUsShare_PaysItsMarketsCourtage_InABacktest_ASwedishOneTheClasss()
    {
        MarketPanel swedish = BacktestFixtures.FromCloses([.. Enumerable.Range(0, 60).Select(i => 100.0 + i)], new DateOnly(2024, 1, 1));
        MarketPanel us = Foreign(swedish, sekPerUnit: 10m);
        StrategyDefinition hold = StrategyCatalog.Create("buy-and-hold", new Dictionary<string, string>());

        BacktestResult inStockholm = BacktestRunner.Run(BacktestFixtures.Request(swedish, hold) with { Costs = StartLike });
        BacktestResult inNewYork = BacktestRunner.Run(BacktestFixtures.Request(us, hold) with { Costs = StartLike });

        Assert.True(inStockholm.Ok && inNewYork.Ok);
        Assert.Equal(0.0, inStockholm.FinalState.Courtage);
        Assert.True(inNewYork.FinalState.Courtage >= 10.0, $"courtage {inNewYork.FinalState.Courtage}: at least 1 USD at 10 SEK");
        Assert.Equal(0.0025 * inNewYork.TradedNotional, inNewYork.FinalState.Courtage, 1e-6); // 0.25 % above the minimum
    }

    [Fact]
    public void AUsShareWithoutAForeignCourtage_IsRefused_NotChargedAsSwedish()
    {
        MarketPanel us = Foreign(BacktestFixtures.FromCloses([.. Enumerable.Range(0, 30).Select(i => 100.0 + i)], new DateOnly(2024, 1, 1)), 10m);
        StrategyDefinition hold = StrategyCatalog.Create("buy-and-hold", new Dictionary<string, string>());
        CostModel noForeign = StartLike with { Foreign = new Dictionary<string, ForeignCourtage>(StringComparer.Ordinal) };

        var ex = Assert.Throws<BacktestConfigException>(() => BacktestRunner.Run(BacktestFixtures.Request(us, hold) with { Costs = noForeign }));
        Assert.Contains("no courtage for USD shares (TEST)", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The same bars as a US share (already in SEK, as the store panel gives them) with its last fixing.</summary>
    private static MarketPanel Foreign(MarketPanel sek, decimal sekPerUnit)
    {
        PanelInstrument i = sek.Instruments[0];
        var foreign = new PanelInstrument(i.Symbol, i.LotSize, true, i.TickSizes) { Currency = "USD", LastSekPerUnit = sekPerUnit };
        int n = sek.Periods;
        double[] open = new double[n], high = new double[n], low = new double[n], close = new double[n], volume = new double[n];
        for (int t = 0; t < n; t++)
        {
            var b = sek.Bar(t, 0);
            (open[t], high[t], low[t], close[t], volume[t]) = (b.Open, b.High, b.Low, b.Close, b.Volume);
        }

        return new MarketPanel(sek.Dates, [foreign], open, high, low, close, volume, sek.Source);
    }
}
