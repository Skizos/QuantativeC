using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Tests;

/// <summary>Test data, strategies and requests shared by the backtest tests.</summary>
internal static class BacktestFixtures
{
    public static readonly CostModel Costs = new("avanza-small", "SEK", 39m, 0.0015m, 0.0025m, 5m, 5m, 0.10m, "https://www.avanza.se/priser-och-avgifter.html", null);

    public static readonly HoldoutPolicy Locked = new(true, new DateOnly(2025, 10, 1), "config/holdout.json");

    public static MarketPanel Synthetic(int instruments = 5, int periods = 300, ulong seed = 7, double drift = 0) =>
        SyntheticMarket.Generate(new SyntheticMarketOptions { Instruments = instruments, Periods = periods, Seed = seed, AnnualDrift = drift });

    public static BacktestRequest Request(MarketPanel data, StrategyDefinition strategy, TrialLedger? ledger = null) => new()
    {
        Data = data,
        Strategy = strategy,
        Costs = Costs,
        Holdout = Locked,
        Seed = 7,
        Ledger = ledger,
        Runner = "test",
    };

    public static StrategyDefinition Define(string name, IStrategy? single = null, StrategyFactory? factory = null) =>
        new(new StrategySpec(name, new SortedDictionary<string, string>(StringComparer.Ordinal)), factory ?? (_ => single!));

    /// <summary>Hand-made panel: one instrument, given closes, open = previous close, high/low ±1 %.</summary>
    public static MarketPanel FromCloses(double[] closes, DateOnly? start = null, long lot = 1)
    {
        int n = closes.Length;
        double[] open = new double[n], high = new double[n], low = new double[n], volume = new double[n];
        for (int t = 0; t < n; t++)
        {
            open[t] = t == 0 ? closes[0] : closes[t - 1];
            high[t] = Math.Max(open[t], closes[t]) * 1.01;
            low[t] = Math.Min(open[t], closes[t]) * 0.99;
            volume[t] = 1e6;
        }

        DateOnly d0 = start ?? new DateOnly(2020, 1, 1);
        DateOnly[] dates = [.. Enumerable.Range(0, n).Select(d0.AddDays)];
        return new MarketPanel(
            dates, [new PanelInstrument("TEST", lot, false, SyntheticMarket.CentTicks)], open, high, low, closes, volume,
            new DataSourceInfo("hand-made", true, true, "test"));
    }
}

/// <summary>Buys tomorrow's risers: reads the future through the panel its factory was given (the classic leak).</summary>
internal sealed class LeakyCanary(MarketPanel data) : IStrategy
{
    public void Decide(BarWindow window, Span<double> targets)
    {
        int t = window.Now;
        for (int i = 0; i < window.InstrumentCount; i++)
        {
            bool rises = t + 1 < data.Periods && data.Bar(t + 1, i).Close > window.Close(i, t);
            targets[i] = rises ? 1.0 / window.InstrumentCount : 0.0;
        }
    }
}

/// <summary>Reads the next bar through the window: caught structurally.</summary>
internal sealed class WindowPeeker : IStrategy
{
    public void Decide(BarWindow window, Span<double> targets) => targets.Fill(window.Close(0, window.Now + 1) > 0 ? 0.0 : 0.0);
}

internal sealed class FixedTargets(params double[] weights) : IStrategy
{
    public void Decide(BarWindow window, Span<double> targets) => weights.CopyTo(targets);
}

public sealed class BacktestDataTests
{
    [Fact]
    public void SeededRandom_MatchesTheReferenceImplementation()
    {
        // tools/reference/xoshiro_reference.py (independent port of the published C code).
        Assert.Equal([0x99ec5f36cb75f2b4UL, 0xbf6e1f784956452aUL, 0x1a5f849d4933e6e0UL], Draw(0));
        Assert.Equal([0x15780b2e0c2ec716UL, 0x6104d9866d113a7eUL, 0xae17533239e499a1UL], Draw(42));
        Assert.Equal([0x6198d28eab43630cUL, 0x15c89c8488910650UL, 0x9f5778d20eb9946bUL], Draw(20260926));

        var rng = new SeededRandom(1);
        double[] z = [.. Enumerable.Range(0, 200_000).Select(_ => rng.NextGaussian())];
        Assert.Equal(0.0, z.Average(), 0.01);
        Assert.Equal(1.0, z.Select(v => v * v).Average(), 0.01);

        static ulong[] Draw(ulong seed)
        {
            var r = new SeededRandom(seed);
            return [r.NextUInt64(), r.NextUInt64(), r.NextUInt64()];
        }
    }

    [Fact]
    public void SyntheticMarket_IsDeterministic_WeekdaysOnly_AndLabelled()
    {
        MarketPanel a = BacktestFixtures.Synthetic(seed: 3), b = BacktestFixtures.Synthetic(seed: 3), c = BacktestFixtures.Synthetic(seed: 4);
        Assert.Equal(a.Bar(299, 4).Close, b.Bar(299, 4).Close);
        Assert.NotEqual(a.Bar(299, 4).Close, c.Bar(299, 4).Close);
        Assert.DoesNotContain(a.Dates, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.True(a.Source.PointInTime && a.Source.SurvivorshipFree);
        Assert.Equal(SyntheticMarket.SourceName, a.Source.Name);
        // The panel constructor validates OHLC consistency, so generating is the check; spot-check anyway.
        BacktestBar bar = a.Bar(123, 2);
        Assert.True(bar.Low <= Math.Min(bar.Open, bar.Close) && bar.High >= Math.Max(bar.Open, bar.Close));
    }

    [Fact]
    public void Panel_FromDailyBars_AlignsDates_MissingBarsAreInvalid()
    {
        DailyBar Bar(int day, decimal close) => new(new DateOnly(2024, 1, day), close, close + 1, close - 1, close, 100);
        var a = new PanelInstrument("AAA", 1, false, SyntheticMarket.CentTicks);
        var b = new PanelInstrument("BBB", 1, true, SyntheticMarket.CentTicks);
        MarketPanel panel = MarketPanel.FromDailyBars(
            [(a, [Bar(2, 10), Bar(3, 11), Bar(4, 12)]), (b, [Bar(3, 20), Bar(4, 21)])],
            new DataSourceInfo("x", false, false, ""));

        Assert.Equal(3, panel.Periods);
        Assert.False(panel.IsValid(0, 1));
        Assert.Equal(BacktestBar.NoTrading, panel.Bar(0, 1));
        Assert.Equal(20.0, panel.Bar(1, 1).Close);
        Assert.Equal(12.0, panel.Bar(2, 0).Close);

        MarketPanel view = panel.Between(new DateOnly(2024, 1, 3), new DateOnly(2024, 1, 3));
        Assert.Equal(1, view.Periods);
        Assert.Equal(11.0, view.Bar(0, 0).Close);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Bar(1, 0));
        Assert.Equal(2, panel.Truncate(2).Periods);

        Assert.Throws<ArgumentException>(() => MarketPanel.FromDailyBars(
            [(a, [new DailyBar(new DateOnly(2024, 1, 2), 10, 9, 8, 10, 1)])], new DataSourceInfo("x", false, false, ""))); // high < open
    }

    [Fact]
    public void Window_ShowsOnlyThePast()
    {
        MarketPanel panel = BacktestFixtures.FromCloses([10, 11, 12, 13]);
        var window = new BarWindow(panel);
        window.MoveTo(1);
        Assert.Equal(2, window.Closes(0).Length);
        Assert.Equal(11.0, window.Close(0, 1));
        Assert.Throws<LookAheadException>(() => window.Close(0, 2));
        Assert.Throws<LookAheadException>(() => window.Date(2));
        Assert.Throws<LookAheadException>(() => window.High(0, 3));
    }
}

public sealed class StrategyTests
{
    [Fact]
    public void MovingAverageCross_IsLongWhileFastAboveSlow()
    {
        double[] closes = [10, 10, 10, 11, 12, 13, 12, 10, 8, 7];
        MarketPanel panel = BacktestFixtures.FromCloses(closes);
        var strategy = new MovingAverageCross(fast: 2, slow: 3);
        var window = new BarWindow(panel);
        var decisions = new List<double>();
        Span<double> target = stackalloc double[1];
        for (int t = 0; t < closes.Length; t++)
        {
            window.MoveTo(t);
            strategy.Decide(window, target);
            decisions.Add(target[0]);
        }

        // t=2: SMA2 10 vs SMA3 10 (not above) → flat; t=3..6 rising → long; t=7: SMA2 11 vs SMA3 11.67 → flat.
        Assert.Equal([0, 0, 0, 1, 1, 1, 1, 0, 0, 0], decisions);
    }

    [Fact]
    public void RandomTargets_AreSeeded_LongOnly_AndChangeOnlyOnRebalanceBars()
    {
        MarketPanel panel = BacktestFixtures.Synthetic(instruments: 8, periods: 60);
        double[][] Run(ulong seed)
        {
            var s = new RandomTargets(seed, rebalance: 21);
            var w = new BarWindow(panel);
            var all = new double[60][];
            for (int t = 0; t < 60; t++)
            {
                w.MoveTo(t);
                all[t] = new double[8];
                s.Decide(w, all[t]);
            }

            return all;
        }

        double[][] a = Run(5), b = Run(5), c = Run(6);
        Assert.Equal(a[59], b[59]);
        Assert.NotEqual(a.SelectMany(x => x), c.SelectMany(x => x));
        Assert.All(a, w => Assert.True(w.All(x => x >= 0) && w.Sum() <= 1 + 1e-12));
        Assert.Equal(a[0], a[20]);
        Assert.Equal(a[21], a[41]);
    }

    [Fact]
    public void Catalog_ParsesAndCanonicalisesParameters_AndRejectsUnknownOnes()
    {
        StrategyDefinition ma = StrategyCatalog.Create("ma-cross", new Dictionary<string, string> { ["slow"] = "0100", ["fast"] = "20" });
        Assert.Equal("ma-cross(fast=20, slow=100)", ma.Spec.Describe());
        StrategyDefinition random = StrategyCatalog.Create("random-targets", new Dictionary<string, string> { ["seed"] = "9" });
        Assert.Equal(["p", "rebalance", "seed"], random.Spec.Parameters.Keys);
        Assert.Equal("21", random.Spec.Parameters["rebalance"]);

        Assert.Throws<ArgumentException>(() => StrategyCatalog.Create("ma-cross", new Dictionary<string, string> { ["fast"] = "50", ["slow"] = "20" }));
        Assert.Throws<ArgumentException>(() => StrategyCatalog.Create("ma-cross", new Dictionary<string, string> { ["fast"] = "5", ["slow"] = "20", ["x"] = "1" }));
        Assert.Throws<ArgumentException>(() => StrategyCatalog.Create("ma-cross", new Dictionary<string, string> { ["fast"] = "5" }));
        Assert.Throws<ArgumentException>(() => StrategyCatalog.Create("nope", new Dictionary<string, string>()));
    }

    [Fact]
    public void CatalogMetadata_MatchesWhatCreateAccepts()
    {
        Dictionary<string, string> samples = new() { ["fast"] = "20", ["slow"] = "100", ["seed"] = "7" };
        foreach (string name in StrategyCatalog.Names)
        {
            Assert.False(string.IsNullOrWhiteSpace(StrategyCatalog.Summary(name)));
            IReadOnlyList<StrategyParameter> ps = StrategyCatalog.ParametersOf(name);
            Assert.All(ps, p => Assert.True(p.Default is not null || samples.ContainsKey(p.Key), $"{name}.{p.Key} needs a sample"));

            // Defaults plus a sample for each required key are accepted, and the spec lists exactly the declared keys with those defaults.
            StrategyDefinition d = StrategyCatalog.Create(name, ps.ToDictionary(p => p.Key, p => p.Default ?? samples[p.Key]));
            Assert.Equal(ps.Select(p => p.Key).Order(StringComparer.Ordinal), d.Spec.Parameters.Keys.Order(StringComparer.Ordinal));
            Assert.All(ps.Where(p => p.Default is not null), p => Assert.Equal(StrategyCatalog.Create(name, ps.Where(q => q.Default is null).ToDictionary(q => q.Key, q => samples[q.Key])).Spec.Parameters[p.Key], p.Default));

            // Every required key really is required.
            Assert.All(ps.Where(p => p.Default is null), p => Assert.Throws<ArgumentException>(() =>
                StrategyCatalog.Create(name, ps.Where(q => q.Default is null && q.Key != p.Key).ToDictionary(q => q.Key, q => samples[q.Key]))));
        }

        Assert.Throws<ArgumentException>(() => StrategyCatalog.ParametersOf("nope"));
    }
}

public sealed class OrderPlanningTests
{
    private static readonly TickSizeTable Ticks = new([new TickSizeBand(0m, 49.99m, 0.01m), new TickSizeBand(50m, 99.95m, 0.05m), new TickSizeBand(100m, 100_000m, 0.1m)]);

    private static MarketPanel Panel(double close, long lot = 1)
    {
        var instrument = new PanelInstrument("X", lot, false, Ticks);
        return new MarketPanel(
            [new DateOnly(2024, 1, 2)], [instrument], [close], [close * 1.01], [close * 0.99], [close], [1e6], new DataSourceInfo("t", true, true, ""));
    }

    private static List<BacktestOrder> Plan(MarketPanel panel, double target, long position, ExecutionOptions? execution = null, double equity = 100_000)
    {
        var orders = new List<BacktestOrder>();
        BacktestRunner.PlanOrders(panel, 0, [target], equity, [position], execution ?? new ExecutionOptions { CashBuffer = 0 }, orders);
        return orders;
    }

    [Fact]
    public void BuyLimit_WholeLots_RoundedDownToTheTick()
    {
        // 50 % of 100 000 at 73.33 = 681.8 → 600 in lots of 100; limit 73.33 × 1.005 = 73.69665 → 73.65 (0.05 tick, down).
        BacktestOrder o = Assert.Single(Plan(Panel(73.33, lot: 100), 0.5, 0));
        Assert.Equal(BacktestSide.Buy, o.Side);
        Assert.Equal(600, o.Quantity);
        Assert.Equal(73.65, o.LimitPrice, 1e-12);
    }

    [Fact]
    public void SellLimit_RoundedUpToTheTick()
    {
        // Exit 300 at 101.37 × 0.995 = 100.863 → 100.9 (0.1 tick, up).
        BacktestOrder o = Assert.Single(Plan(Panel(101.37), 0.0, 300));
        Assert.Equal(BacktestSide.Sell, o.Side);
        Assert.Equal(300, o.Quantity);
        Assert.Equal(100.9, o.LimitPrice, 1e-12);
    }

    [Fact]
    public void Band_SkipsDrift_ButNotEntriesOrExits()
    {
        MarketPanel panel = Panel(100);
        Assert.Empty(Plan(panel, 0.5, 480)); // target 500, 4 % off
        Assert.Single(Plan(panel, 0.5, 400)); // 20 % off
        Assert.Single(Plan(panel, 0.0, 1)); // exit, however small
        Assert.Single(Plan(panel, 0.001, 0)); // entry of 1 share
        Assert.Empty(Plan(panel, 0.001, 0, new ExecutionOptions { CashBuffer = 0, MinTradeValue = 1000m })); // below the minimum value
        Assert.Empty(Plan(panel, double.NaN, 400)); // NaN = hold
    }

    [Fact]
    public void MarketOrders_HaveNoLimit()
    {
        BacktestOrder o = Assert.Single(Plan(Panel(100), 0.5, 0, new ExecutionOptions { CashBuffer = 0, OrderType = BacktestOrderType.MarketOnOpen }));
        Assert.Equal(BacktestOrderType.MarketOnOpen, o.Type);
        Assert.Equal(0.0, o.LimitPrice);
    }

    [Fact]
    public void InvalidTargets_AreStrategyErrors()
    {
        var orders = new List<BacktestOrder>();
        MarketPanel two = BacktestFixtures.Synthetic(instruments: 2, periods: 2);
        Assert.Throws<StrategyException>(() => BacktestRunner.PlanOrders(two, 0, [0.6, 0.6], 1e5, [0, 0], new ExecutionOptions(), orders));
        Assert.Throws<StrategyException>(() => BacktestRunner.PlanOrders(two, 0, [-0.1, 0.1], 1e5, [0, 0], new ExecutionOptions(), orders));
        Assert.Throws<StrategyException>(() => BacktestRunner.PlanOrders(two, 0, [double.PositiveInfinity, 0], 1e5, [0, 0], new ExecutionOptions(), orders));
    }
}

public sealed class BacktestRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qa-backtest-tests", Guid.NewGuid().ToString("N"));

    public BacktestRunnerTests() => Directory.CreateDirectory(_dir);

    private TrialLedger NewLedger() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void LeakageCanary_IsRejected_AndLogged()
    {
        TrialLedger ledger = NewLedger();
        MarketPanel data = BacktestFixtures.Synthetic();
        BacktestResult r = BacktestRunner.Run(BacktestFixtures.Request(data, BacktestFixtures.Define("leaky-canary", factory: d => new LeakyCanary(d)), ledger));

        Assert.Equal(TrialStatus.RejectedLeakage, r.Record.Status);
        Assert.Contains("look-ahead", r.Record.Note, StringComparison.Ordinal);
        Assert.Null(r.Record.Metrics);
        TrialRecord logged = Assert.Single(ledger.ReadAll());
        Assert.Equal(TrialStatus.RejectedLeakage, logged.Status);
        Assert.Equal("T000001", r.Record.Id);
    }

    [Fact]
    public void ReadingTheNextBarThroughTheWindow_IsRejected()
    {
        TrialLedger ledger = NewLedger();
        BacktestResult r = BacktestRunner.Run(BacktestFixtures.Request(BacktestFixtures.Synthetic(), BacktestFixtures.Define("peeker", new WindowPeeker()), ledger));
        Assert.Equal(TrialStatus.RejectedLeakage, r.Record.Status);
        Assert.Single(ledger.ReadAll());
    }

    [Theory]
    [InlineData("buy-and-hold", "")]
    [InlineData("ma-cross", "fast=5,slow=20")]
    [InlineData("random-targets", "seed=3")]
    public void HonestStrategies_PassTheLeakageCheck(string name, string parameters)
    {
        Dictionary<string, string> p = parameters.Length == 0
            ? []
            : parameters.Split(',').Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => kv[1]);
        BacktestResult r = BacktestRunner.Run(BacktestFixtures.Request(BacktestFixtures.Synthetic(), StrategyCatalog.Create(name, p)));
        Assert.True(r.Ok, r.Record.Note);
        Assert.NotNull(r.Record.Metrics);
        Assert.Equal(299, r.Record.Metrics.Observations);
        Assert.True(r.FinalState.Fills > 0);
    }

    [Fact]
    public void LockedHoldout_RefusesTheRun_AndLogsIt()
    {
        TrialLedger ledger = NewLedger();
        MarketPanel data = BacktestFixtures.FromCloses([.. Enumerable.Range(0, 10).Select(i => 100.0 + i)], start: new DateOnly(2025, 9, 25));
        BacktestResult r = BacktestRunner.Run(BacktestFixtures.Request(data, StrategyCatalog.Create("buy-and-hold", new Dictionary<string, string>()), ledger));

        Assert.Equal(TrialStatus.RejectedHoldout, r.Record.Status);
        Assert.Contains("locked final holdout", r.Record.Note, StringComparison.Ordinal);
        Assert.Empty(r.Equity);
        Assert.Single(ledger.ReadAll());

        BacktestResult before = BacktestRunner.Run(BacktestFixtures.Request(data.Between(data.Dates[0], new DateOnly(2025, 9, 30)), StrategyCatalog.Create("buy-and-hold", new Dictionary<string, string>())));
        Assert.True(before.Ok, before.Record.Note);
        Assert.False(before.Record.HoldoutTouched);

        BacktestRequest unlocked = BacktestFixtures.Request(data, StrategyCatalog.Create("buy-and-hold", new Dictionary<string, string>())) with
        {
            Holdout = BacktestFixtures.Locked with { Locked = false },
        };
        BacktestResult after = BacktestRunner.Run(unlocked);
        Assert.True(after.Ok);
        Assert.True(after.Record.HoldoutTouched);
    }

    [Fact]
    public void OrdersFillOnTheBarAfterTheDecision_NeverTheSameBar()
    {
        // Decision at the close of bar 0 (100) → market-on-open order → fills at bar 1's open (gap to 110).
        MarketPanel data = new(
            [new DateOnly(2020, 1, 2), new DateOnly(2020, 1, 3)],
            [new PanelInstrument("GAP", 1, false, SyntheticMarket.CentTicks)],
            open: [100, 110],
            high: [101, 111],
            low: [99, 109],
            close: [100, 110],
            volume: [1e6, 1e6],
            new DataSourceInfo("hand-made", true, true, "test"));
        BacktestRequest request = BacktestFixtures.Request(data, BacktestFixtures.Define("all-in", new FixedTargets(0.5))) with
        {
            Execution = new ExecutionOptions { OrderType = BacktestOrderType.MarketOnOpen, CashBuffer = 0 },
            Costs = BacktestFixtures.Costs with { CourtageMin = 0, CourtageRate = 0, SlippageBps = 0, HalfSpreadBps = 0 },
            InitialCash = 10_000m,
        };
        BacktestResult r = BacktestRunner.Run(request);

        Assert.True(r.Ok, r.Record.Note);
        Assert.Equal(10_000.0, r.Equity[0]); // nothing traded on the decision bar
        Assert.Equal(50, r.Positions[0]); // 5 000 / 100 at the decision close, bought at 110 (the next open)
        Assert.Equal(10_000 - (50 * 110.0) + (50 * 110.0), r.Equity[1], 1e-9); // bought at 110, not at the decision close 100
        Assert.Equal(1, r.FinalState.Fills);
    }

    [Fact]
    public void Runs_AreDeterministic_AndCarryLabelsAndCostStatus()
    {
        StrategyDefinition ma = StrategyCatalog.Create("ma-cross", new Dictionary<string, string> { ["fast"] = "5", ["slow"] = "20" });
        BacktestResult a = BacktestRunner.Run(BacktestFixtures.Request(BacktestFixtures.Synthetic(seed: 11), ma));
        BacktestResult b = BacktestRunner.Run(BacktestFixtures.Request(BacktestFixtures.Synthetic(seed: 11), ma));

        Assert.Equal(a.Equity, b.Equity);
        Assert.Equal(a.Record.Metrics, b.Record.Metrics);
        Assert.Equal("ma-cross|syn5s7|2015-01-05..2016-02-26|synthetic-gbm", a.Record.Study);
        Assert.Equal(["SYN0001..SYN0005"], a.Record.Universe);
        Assert.True(a.Record.PointInTime && a.Record.SurvivorshipFree);
        Assert.False(a.Record.CostsVerified);
        Assert.Contains("UNVERIFIED", a.Record.Note, StringComparison.Ordinal);
        Assert.True(a.Record.Metrics!.TotalCosts > 0);
        Assert.Equal(a.FinalState.Courtage + a.FinalState.FxFees + a.FinalState.SpreadSlippage, a.Record.Metrics.TotalCosts, 1e-9);
    }

    [Fact]
    public void Dsr_UsesEveryCompletedTrialOfTheStudy()
    {
        TrialLedger ledger = NewLedger();
        MarketPanel data = BacktestFixtures.Synthetic(periods: 200);
        BacktestResult first = BacktestRunner.Run(BacktestFixtures.Request(data, StrategyCatalog.Create("random-targets", new Dictionary<string, string> { ["seed"] = "1" }), ledger));
        BacktestResult second = BacktestRunner.Run(BacktestFixtures.Request(data, StrategyCatalog.Create("random-targets", new Dictionary<string, string> { ["seed"] = "2" }), ledger));

        Assert.Null(first.Record.Metrics!.Dsr); // one trial: nothing to deflate against yet
        Assert.Equal(1, first.Record.Metrics.DsrTrials);
        Assert.Equal(2, second.Record.Metrics!.DsrTrials);
        Assert.NotNull(second.Record.Metrics.Dsr);
        Assert.True(ledger.Verify().Valid);
    }

    [Fact]
    public void InvalidTargets_AreLoggedAsFailed()
    {
        TrialLedger ledger = NewLedger();
        BacktestResult r = BacktestRunner.Run(BacktestFixtures.Request(BacktestFixtures.Synthetic(instruments: 2), BacktestFixtures.Define("leveraged", new FixedTargets(0.8, 0.8)), ledger));
        Assert.Equal(TrialStatus.Failed, r.Record.Status);
        Assert.Contains("sum to", r.Record.Note, StringComparison.Ordinal);
        Assert.Single(ledger.ReadAll());
    }

    [Fact]
    public void Sweep_ExpandsTheGrid_LogsEveryConfiguration_AndReportsPbo()
    {
        TrialLedger ledger = NewLedger();
        IReadOnlyList<IReadOnlyDictionary<string, string>> grid = BacktestSweep.ExpandGrid(
            new Dictionary<string, IReadOnlyList<string>> { ["fast"] = ["5", "10"], ["slow"] = ["20", "40", "60"] });
        Assert.Equal(6, grid.Count);
        Assert.Equal("10", grid[3]["fast"]);
        Assert.Equal("20", grid[3]["slow"]);

        MarketPanel data = BacktestFixtures.Synthetic(periods: 400);
        SweepResult sweep = BacktestSweep.Run(
            BacktestFixtures.Request(data, StrategyCatalog.Create("buy-and-hold", new Dictionary<string, string>()), ledger),
            [.. grid.Select(p => StrategyCatalog.Create("ma-cross", p))]);

        Assert.Equal(6, sweep.Trials.Count);
        Assert.Equal(6, ledger.ReadAll().Count);
        Assert.NotNull(sweep.Pbo);
        Assert.InRange(sweep.Pbo.Probability, 0.0, 1.0);
        Assert.NotNull(sweep.Best);
        Assert.Equal(6, sweep.StudyTrials);
        Assert.NotNull(sweep.BestDsr);
    }
}

/// <summary>
/// Gate (docs/plans/05-phase5-backtesting.md "Deflation"): 200 random-target strategies on a driftless random walk.
/// The best raw Sharpe looks good; deflated for 200 trials it is not significant, and PBO is near 0.5.
/// </summary>
public sealed class DeflationGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qa-deflation-gate", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TwoHundredRandomStrategies_BestLooksGood_ButDeflatesAway()
    {
        var ledger = new TrialLedger(Path.Combine(_dir, "ledger.jsonl"));
        MarketPanel data = SyntheticMarket.Generate(new SyntheticMarketOptions { Instruments = 20, Periods = 756, Seed = 20260926, AnnualDrift = 0 });
        StrategyDefinition[] strategies = [.. Enumerable.Range(1, 200).Select(s =>
            StrategyCatalog.Create("random-targets", new Dictionary<string, string> { ["seed"] = s.ToString(System.Globalization.CultureInfo.InvariantCulture) }))];

        SweepResult sweep = BacktestSweep.Run(BacktestFixtures.Request(data, strategies[0], ledger), strategies);

        Assert.Equal(200, sweep.StudyTrials);
        TrialMetrics best = sweep.Best!.Record.Metrics!;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"best {sweep.Best.Record.Parameters["seed"]}: SR/yr {best.SharpeAnnualised:0.000}, PSR(0) {best.Psr0:0.000}, DSR {sweep.BestDsr:0.000}, PBO {sweep.Pbo!.Probability:0.000}");
        Assert.True(best.SharpeAnnualised > 0.5, $"best annualised Sharpe {best.SharpeAnnualised:0.00} should look good by chance");
        Assert.True(best.Psr0 > 0.8, $"undeflated PSR {best.Psr0:0.000}");
        Assert.True(sweep.BestDsr < 0.95, $"DSR {sweep.BestDsr:0.000} must not be significant after 200 trials");
        Assert.InRange(sweep.Pbo!.Probability, 0.3, 0.7);
        Assert.True(ledger.Verify().Valid);
    }
}
