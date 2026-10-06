using QuantAnalyst.Analytics.Backtesting;

namespace QuantAnalyst.Analytics.Tests;

/// <summary>
/// Plan 22: a First North share pays First North's courtage (0.25 %, minimum 1 SEK on Start and Mini; verified 2026-10-01), not
/// the main market's (free on Start), in a backtest as in Paper; a marketplace the class has no courtage for is refused.
/// </summary>
public sealed class FirstNorthCostTests
{
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
    [InlineData("avanza-small", 39, 0.0015)]
    [InlineData("avanza-medium", 69, 0.00069)]
    [InlineData("avanza-fastpris", 99, 0)]
    public void EveryClass_HasItsFirstNorthCourtage_VerifiedAgainstThePriceList(string name, double min, double rate)
    {
        CostModel c = CostModel.Load(Path.Combine(RepoConfig, $"costs.{name}.json"));
        Assert.Equal(new MarketplaceCourtage((decimal)min, (decimal)rate), c.MarketplaceFor("FNSE"));
        Assert.Null(c.MarketplaceFor("XSTO"));
        Assert.Null(c.MarketplaceFor((string?)null));
        Assert.Equal(new DateOnly(2026, 10, 1), c.MarketplaceVerifiedOn); // the owner's screenshot of the price list
    }

    [Fact]
    public void CourtageIn_ChargesFirstNorthItsOwn_AndRefusesAnUnknownMarketplace()
    {
        CostModel start = CostModel.Load(Path.Combine(RepoConfig, "costs.avanza-start.json"));
        Assert.Equal(0m, start.CourtageIn("SEK", 10_000m, "XSTO")); // Start is free on the main market
        Assert.Equal(25m, start.CourtageIn("SEK", 10_000m, "FNSE")); // 0.25 %
        Assert.Equal(1m, start.CourtageIn("SEK", 100m, "FNSE")); // the 1 SEK minimum
        var ex = Assert.Throws<BacktestConfigException>(() => start.CourtageIn("SEK", 1_000m, "SSME"));
        Assert.Contains("no courtage for shares on SSME", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMainMarket_CannotBeGivenAsAMarketplaceCourtage()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qa-marketplace-costs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string text = File.ReadAllText(Path.Combine(RepoConfig, "costs.avanza-mini.json")).Replace("\"FNSE\":", "\"XSTO\":", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(dir, "costs.avanza-mini.json"), text);
            var ex = Assert.Throws<BacktestConfigException>(() => CostModel.Load(Path.Combine(dir, "costs.avanza-mini.json")));
            Assert.Contains("marketplace_courtage.XSTO is the main market", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AFirstNorthShare_PaysItsCourtage_InABacktest_AMainMarketOneTheClasss()
    {
        CostModel start = CostModel.Load(Path.Combine(RepoConfig, "costs.avanza-start.json")) with { FreeTrades = null };
        MarketPanel main = BacktestFixtures.FromCloses([.. Enumerable.Range(0, 60).Select(i => 100.0 + i)], new DateOnly(2024, 1, 1));
        MarketPanel firstNorth = OnMarketplace(main, "FNSE");
        StrategyDefinition hold = StrategyCatalog.Create("buy-and-hold", new Dictionary<string, string>());

        BacktestResult onXsto = BacktestRunner.Run(BacktestFixtures.Request(main, hold) with { Costs = start });
        BacktestResult onFnse = BacktestRunner.Run(BacktestFixtures.Request(firstNorth, hold) with { Costs = start });

        Assert.True(onXsto.Ok && onFnse.Ok);
        Assert.Equal(0.0, onXsto.FinalState.Courtage);
        Assert.Equal(0.0025 * onFnse.TradedNotional, onFnse.FinalState.Courtage, 1e-6);

        CostModel noFirstNorth = start with { Marketplaces = new Dictionary<string, MarketplaceCourtage>(StringComparer.Ordinal) };
        var ex = Assert.Throws<BacktestConfigException>(() => BacktestRunner.Run(BacktestFixtures.Request(firstNorth, hold) with { Costs = noFirstNorth }));
        Assert.Contains("no courtage for shares on FNSE", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The same bars as a Swedish share on <paramref name="marketPlace"/>.</summary>
    private static MarketPanel OnMarketplace(MarketPanel panel, string marketPlace)
    {
        PanelInstrument i = panel.Instruments[0];
        int n = panel.Periods;
        double[] open = new double[n], high = new double[n], low = new double[n], close = new double[n], volume = new double[n];
        for (int t = 0; t < n; t++)
        {
            var b = panel.Bar(t, 0);
            (open[t], high[t], low[t], close[t], volume[t]) = (b.Open, b.High, b.Low, b.Close, b.Volume);
        }

        return new MarketPanel(panel.Dates, [i with { MarketPlace = marketPlace }], open, high, low, close, volume, panel.Source);
    }
}
