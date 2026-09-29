using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Tests;

/// <summary>
/// Plan 17 step A3: the backtest on intraday bars. The panel carries bar times, the window hands them out without
/// look-ahead, the engine fills limits at the limit only on a trade-through (ABI 1.4), a measured spread is paid,
/// a strategy must be flat by each day's close, and the statistics are on daily P&amp;L.
/// </summary>
public sealed class IntradayBacktestTests
{
    private static readonly TickSizeTable Cents = new([new TickSizeBand(0m, 99_999m, 0.01m)]);

    // Monday 3 March 2025, 09:00 Stockholm (CET) = 08:00 UTC; before the locked holdout (2025-10-01).
    private static readonly DateTimeOffset Monday = new(2025, 3, 3, 8, 0, 0, TimeSpan.Zero);

    private static readonly DataSourceInfo Source = new("test-intraday", false, false, "hand-made 5-minute bars");

    /// <summary>Six 5-minute bars from 09:00 on each day; closes rise 1 a bar from 100, opens at the previous close.</summary>
    private static MarketPanel Panel(int days = 1, double? halfSpreadBps = null, params (string Symbol, int SkipBar)[] shares)
    {
        (string Symbol, int SkipBar)[] names = shares.Length > 0 ? shares : [("TEST", -1)];
        var series = new List<(PanelInstrument, IReadOnlyList<Bar>)>();
        foreach ((string symbol, int skip) in names)
        {
            var bars = new List<Bar>();
            decimal previous = 100m;
            for (int d = 0; d < days; d++)
            {
                for (int k = 0; k < 6; k++)
                {
                    decimal close = 100m + k;
                    decimal open = k == 0 ? 100m : previous;
                    if ((d * 6) + k != skip)
                    {
                        bars.Add(new Bar(Monday.AddDays(d).AddMinutes(5 * k), open, Math.Max(open, close) * 1.002m, Math.Min(open, close) * 0.998m, close, 1_000_000));
                    }

                    previous = close;
                }
            }

            series.Add((new PanelInstrument(symbol, 1, false, Cents) { HalfSpreadBps = halfSpreadBps }, bars));
        }

        return MarketPanel.FromIntradayBars(series, Source);
    }

    private static BacktestRequest Request(MarketPanel data, IStrategy strategy, BacktestOrderType type = BacktestOrderType.MarketOnOpen, double offsetBps = 50) =>
        BacktestFixtures.Request(data, BacktestFixtures.Define("intraday-test", strategy)) with
        {
            Execution = new ExecutionOptions { OrderType = type, LimitOffsetBps = offsetBps },
        };

    /// <summary>In at the 09:00 bar's close (half the equity), out at the 09:10 bar's close: flat long before the close.</summary>
    private sealed class DayTrader : IStrategy
    {
        public void Decide(BarWindow window, Span<double> targets)
        {
            TimeOnly at = TimeOnly.FromDateTime(MarketTime.ToStockholm(window.Time(window.Now)).DateTime);
            targets.Fill(at == new TimeOnly(9, 0) ? 0.5 : at == new TimeOnly(9, 10) ? 0.0 : double.NaN);
        }
    }

    private sealed class Holder : IStrategy
    {
        public void Decide(BarWindow window, Span<double> targets) => targets.Fill(0.5);
    }

    private sealed class TimePeeker : IStrategy
    {
        public void Decide(BarWindow window, Span<double> targets)
        {
            _ = window.Time(window.Now + 1); // the next bar's time: look-ahead
            targets.Fill(double.NaN);
        }
    }

    [Fact]
    public void ThePanel_AlignsIntradayBars_OnTheirStarts_WithEachBarsStockholmDate()
    {
        MarketPanel p = Panel(2, null, ("A", -1), ("B", 7)); // B has no 09:05 bar on Tuesday
        Assert.True(p.IsIntraday);
        Assert.Equal(12, p.Periods);
        Assert.Equal(Monday.AddDays(1).AddMinutes(5), p.BarStartsUtc![7]);
        Assert.Equal([new DateOnly(2025, 3, 3), new DateOnly(2025, 3, 4)], p.Dates.Distinct());
        Assert.False(p.IsValid(7, 1));
        Assert.True(p.IsValid(7, 0));
        Assert.Equal([5, 11], Enumerable.Range(0, p.Periods).Where(p.IsLastOfDay));
        Assert.Equal("2025-03-04 09:05", p.Label(7));

        MarketPanel firstDay = p.Truncate(6);
        Assert.Equal(p.BarStartsUtc.Take(6), firstDay.BarStartsUtc);
        Assert.True(firstDay.IsLastOfDay(5));
        MarketPanel daily = BacktestFixtures.FromCloses([1.0, 2.0], new DateOnly(2025, 3, 3));
        Assert.False(daily.IsIntraday);
        Assert.Equal("2025-03-03", daily.Label(0)); // a daily bar's label is its date
    }

    [Fact]
    public void AnIntradayPanel_RefusesStartsOutOfOrder_AndAWrongDate()
    {
        double[] one = [100];
        var instrument = new PanelInstrument("X", 1, false, Cents);
        Assert.Contains("strictly increasing", Assert.Throws<ArgumentException>(() => new MarketPanel(
            [new DateOnly(2025, 3, 3), new DateOnly(2025, 3, 3)], [instrument], [100, 100], [100, 100], [100, 100], [100, 100], [1, 1], Source,
            [Monday.AddMinutes(5), Monday])).Message, StringComparison.Ordinal);
        Assert.Contains("not its Stockholm date", Assert.Throws<ArgumentException>(() => new MarketPanel(
            [new DateOnly(2025, 3, 4)], [instrument], one, one, one, one, one, Source, [Monday])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADayTrader_IsFlatEachEvening_AndJudgedOnDailyPnl()
    {
        BacktestResult r = BacktestRunner.Run(Request(Panel(days: 3), new DayTrader()));

        Assert.True(r.Ok, r.Record.Note);
        Assert.Equal([0L], r.Positions);
        Assert.Equal(3, r.Record.Metrics!.Observations); // one return per day, not per 5-minute bar
        Assert.Equal(6, r.FinalState.Fills); // in and out on each of three days
        Assert.Equal(new DateOnly(2025, 3, 5), r.Record.To);
    }

    [Fact]
    public void HoldingOvernight_FailsTheRun_NamingTheShareAndTheDay()
    {
        BacktestResult r = BacktestRunner.Run(Request(Panel(days: 2), new Holder()));

        Assert.Equal(TrialStatus.Failed, r.Record.Status);
        Assert.Contains("TEST: ", r.Record.Note, StringComparison.Ordinal);
        Assert.Contains("held after the last bar of 2025-03-03 (2025-03-03 09:25); an intraday strategy must be flat by the close (ADR 0006)", r.Record.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIntradayLimit_FillsAtItsLimit_NeverAtTheBetterOpen()
    {
        // Buy limit 1 % over the 09:00 close (100 → 101.00); the 09:05 bar opens at 100 and trades through 101.
        // The daily rule would fill at the open (100); intraday it is filled at 101.00. The exit, a sell limit 1 %
        // under the 09:10 close (102 → 100.98), is filled at 100.98 the same way.
        BacktestResult r = BacktestRunner.Run(Request(Panel(days: 1), new DayTrader(), BacktestOrderType.Limit, offsetBps: 100) with
        {
            Costs = BacktestFixtures.Costs with { CourtageMin = 0, CourtageRate = 0 },
        });

        Assert.True(r.Ok, r.Record.Note);
        long shares = (long)Math.Floor(0.5 * 1_000_000 * 0.99 / 100);
        Assert.Equal((shares * 101.00) + (shares * 100.98), r.TradedNotional, 1e-6);
    }

    [Fact]
    public void AMeasuredHalfSpread_IsWhatMarketOrdersPay()
    {
        BacktestResult config = BacktestRunner.Run(Request(Panel(days: 1), new DayTrader()));
        BacktestResult measured = BacktestRunner.Run(Request(Panel(days: 1, halfSpreadBps: 25), new DayTrader()));

        Assert.True(config.Ok && measured.Ok);
        // The cost model's 5 bps half-spread + 5 bps slippage, against the share's 25 + 5: three times the cost.
        Assert.Equal(3 * config.FinalState.SpreadSlippage, measured.FinalState.SpreadSlippage, 1e-6 * config.FinalState.SpreadSlippage);
    }

    [Fact]
    public void ReadingTheNextBarsTime_IsLookAhead_AndRejected()
    {
        BacktestResult r = BacktestRunner.Run(Request(Panel(days: 1), new TimePeeker()));
        Assert.Equal(TrialStatus.RejectedLeakage, r.Record.Status);
        Assert.Contains("look-ahead", r.Record.Note, StringComparison.Ordinal);
    }

    /// <summary>The intraday leakage canary: its morning weight depends on how much data it was given (the future).</summary>
    private sealed class LengthPeeker(MarketPanel data) : IStrategy
    {
        public void Decide(BarWindow window, Span<double> targets)
        {
            TimeOnly at = TimeOnly.FromDateTime(MarketTime.ToStockholm(window.Time(window.Now)).DateTime);
            targets.Fill(at < new TimeOnly(9, 10) ? (data.Periods >= 12 ? 0.5 : 0.25) : 0.0);
        }
    }

    [Fact]
    public void AStrategyThatUsesLaterBars_IsCaughtByTheTruncationReplay()
    {
        BacktestResult r = BacktestRunner.Run(BacktestFixtures.Request(Panel(days: 2), BacktestFixtures.Define("length-peeker", factory: d => new LengthPeeker(d))) with
        {
            Execution = new ExecutionOptions { OrderType = BacktestOrderType.MarketOnOpen },
        });

        Assert.Equal(TrialStatus.RejectedLeakage, r.Record.Status);
        Assert.Contains("look-ahead: the decision at the close of 2025-03-03 09:05 for TEST was 0.5 on the full data but 0.25 on data ending with that bar", r.Record.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void DailyBarsHaveNoTimes()
    {
        var panel = BacktestFixtures.FromCloses([100.0, 101.0, 102.0]);
        BacktestResult r = BacktestRunner.Run(BacktestFixtures.Request(panel, BacktestFixtures.Define("time-on-daily", new DayTrader())));
        Assert.Equal(TrialStatus.Failed, r.Record.Status);
        Assert.Contains("Daily bars have no start time", r.Record.Note, StringComparison.Ordinal);
    }
}
