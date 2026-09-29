using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Tests;

/// <summary>
/// Plan 17 step A4: a courtage class free for its first trades (Avanza Start: 500 per 12 months, then Mini;
/// UNVERIFIED). The costs file names the class it turns into; a backtest charges that class's courtage on each Swedish
/// trade past the allowance within the rolling window, taken off the equity from that bar on.
/// </summary>
public sealed class FreeTradesTests : IDisposable
{
    private static readonly CostModel Mini = BacktestFixtures.Costs with { Name = "test-mini", CourtageMin = 1m, CourtageRate = 0.0025m, FxFeeRate = 0m };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qa-free-trades", Guid.NewGuid().ToString("N"));

    public FreeTradesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

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

    private static CostModel FreeFor(int trades, int months) => BacktestFixtures.Costs with
    {
        Name = "test-start",
        CourtageMin = 0m,
        CourtageRate = 0m,
        FxFeeRate = 0m,
        FreeTrades = new FreeTradeAllowance(trades, months, Mini, null, null),
    };

    /// <summary>In and out every other day: one trade a day.</summary>
    private sealed class Flipper : IStrategy
    {
        public void Decide(BarWindow window, Span<double> targets) => targets.Fill(window.Now % 2 == 0 ? 0.5 : 0.0);
    }

    private static BacktestResult Run(CostModel costs, MarketPanel data) =>
        BacktestRunner.Run(BacktestFixtures.Request(data, BacktestFixtures.Define("flipper", new Flipper())) with
        {
            Costs = costs,
            InitialCash = 10_000m,
            Execution = new ExecutionOptions { OrderType = BacktestOrderType.MarketOnOpen },
        });

    [Fact]
    public void Start_IsFreeFor500TradesIn12Months_ThenMini_Unverified()
    {
        CostModel start = CostModel.Load(Path.Combine(RepoConfig, "costs.avanza-start.json"));
        FreeTradeAllowance free = Assert.IsType<FreeTradeAllowance>(start.FreeTrades);
        Assert.Equal((500, 12, "avanza-mini"), (free.Trades, free.Months, free.Then.Name));
        Assert.Equal((1m, 0.0025m), (free.Then.CourtageMin, free.Then.CourtageRate));
        Assert.Null(free.VerifiedOn);
        Assert.Null(CostModel.Load(Path.Combine(RepoConfig, "costs.avanza-mini.json")).FreeTrades);
    }

    [Theory]
    [InlineData("\"then\": \"avanza-start\"", "must be another courtage class")]
    [InlineData("\"then\": \"avanza-nope\"", "costs.avanza-nope.json not found")]
    [InlineData("\"months\": 0", "trades >= 1 and months in 1..60")]
    public void ABrokenAllowance_IsRefused(string change, string expected)
    {
        foreach (string f in new[] { "costs.avanza-start.json", "costs.avanza-mini.json" })
        {
            File.Copy(Path.Combine(RepoConfig, f), Path.Combine(_dir, f));
        }

        string path = Path.Combine(_dir, "costs.avanza-start.json");
        string text = File.ReadAllText(path);
        string key = change[..change.IndexOf(':', StringComparison.Ordinal)];
        int at = text.IndexOf(key, text.IndexOf("free_trades", StringComparison.Ordinal), StringComparison.Ordinal);
        int end = text.IndexOf(',', at);
        File.WriteAllText(path, text[..at] + change + text[end..]);

        var ex = Assert.Throws<BacktestConfigException>(() => CostModel.Load(path));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAllowanceThatHasOneOfItsOwn_IsRefused()
    {
        File.Copy(Path.Combine(RepoConfig, "costs.avanza-start.json"), Path.Combine(_dir, "costs.avanza-start.json"));
        string mini = File.ReadAllText(Path.Combine(RepoConfig, "costs.avanza-mini.json"));
        string chained = mini.Replace("\"verified_on\": \"", "\"free_trades\": { \"trades\": 1, \"months\": 12, \"then\": \"avanza-start\" },\n  \"verified_on\": \"", StringComparison.Ordinal);
        Assert.NotEqual(mini, chained);
        File.WriteAllText(Path.Combine(_dir, "costs.avanza-mini.json"), chained);

        var ex = Assert.Throws<BacktestConfigException>(() => CostModel.Load(Path.Combine(_dir, "costs.avanza-start.json")));
        Assert.Contains("has an allowance of its own; one step only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TradesPastTheAllowance_PayTheNextClasssCourtage_OffTheEquity()
    {
        MarketPanel data = BacktestFixtures.FromCloses([.. Enumerable.Range(0, 12).Select(i => 100.0 + i)], new DateOnly(2024, 1, 1));

        BacktestResult free = Run(FreeFor(1_000, 12), data);
        BacktestResult allowance = Run(FreeFor(3, 12), data);

        Assert.True(free.Ok && allowance.Ok, allowance.Record.Note);
        Assert.Equal(0.0, free.AllowanceCourtage);
        Assert.True(allowance.Fills.Count > 3, $"{allowance.Fills.Count} fills");
        double expected = allowance.Fills.Skip(3).Sum(f => Math.Max(1.0, 0.0025 * f.Fill.Quantity * f.Fill.Price));
        Assert.Equal(expected, allowance.AllowanceCourtage, 1e-9);
        Assert.Equal(free.Equity[^1] - expected, allowance.Equity[^1], 1e-6);
        Assert.Equal(free.Equity[2], allowance.Equity[2], 1e-9); // before the fourth trade nothing changes
        Assert.Contains("3 free trades per 12 months ran out on 2024-01-", allowance.Record.Note, StringComparison.Ordinal);
        Assert.Contains("allowance UNVERIFIED", allowance.Record.Note, StringComparison.Ordinal);
        Assert.Equal(allowance.FinalState.TotalCosts + expected, allowance.Record.Metrics!.TotalCosts, 1e-9);
    }

    /// <summary>Six trades in the first week (in, out, three times), then nothing until six weeks later, then six again.</summary>
    private sealed class TwoBursts : IStrategy
    {
        public void Decide(BarWindow window, Span<double> targets)
        {
            int k = window.Now is >= 0 and <= 5 ? window.Now : window.Now is >= 45 and <= 50 ? window.Now - 45 : -1;
            targets.Fill(k < 0 ? double.NaN : k % 2 == 0 ? 0.5 : 0.0);
        }
    }

    [Fact]
    public void TheWindowRolls_OldTradesStopCounting()
    {
        // Three free trades a month. The first burst's last three pay; six weeks later January's trades have left the
        // window, so the second burst again has three free and three that pay.
        MarketPanel data = BacktestFixtures.FromCloses([.. Enumerable.Range(0, 60).Select(i => 100.0 + (i % 5))], new DateOnly(2024, 1, 1));
        BacktestResult r = BacktestRunner.Run(BacktestFixtures.Request(data, BacktestFixtures.Define("two-bursts", new TwoBursts())) with
        {
            Costs = FreeFor(3, 1),
            InitialCash = 10_000m,
            Execution = new ExecutionOptions { OrderType = BacktestOrderType.MarketOnOpen },
        });

        Assert.True(r.Ok, r.Record.Note);
        Assert.Equal(12, r.Fills.Count);
        double Mini(TimedFill f) => Math.Max(1.0, 0.0025 * f.Fill.Quantity * f.Fill.Price);
        double expected = r.Fills.Skip(3).Take(3).Sum(Mini) + r.Fills.Skip(9).Sum(Mini);
        Assert.Equal(expected, r.AllowanceCourtage, 1e-9);
    }
}
