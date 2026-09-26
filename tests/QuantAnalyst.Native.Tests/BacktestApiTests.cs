namespace QuantAnalyst.Native.Tests;

/// <summary>The ABI 1.2 backtest engine through the managed wrapper (the fill model itself is covered by gtest).</summary>
public sealed class BacktestApiTests
{
    private static readonly BacktestConfig Costs = new()
    {
        InitialCash = 100_000,
        CourtageMin = 39,
        CourtageRate = 0.0015,
        FxFeeRate = 0.0025,
        SlippageBps = 5,
        HalfSpreadBps = 5,
        ParticipationCap = 0.10,
    };

    private static readonly BacktestInstrument[] TwoInstruments = [new(1, foreignCurrency: false), new(10, foreignCurrency: true)];

    [Fact]
    public void FillsCostsPositionsAndState_RoundTrip()
    {
        using var bt = QeBacktest.Create(Costs, TwoInstruments);
        Assert.Equal(2, bt.InstrumentCount);
        BacktestBar[] bars = [new(100, 102, 98, 101, 1e6), new(50, 51, 49, 50, 1e6)];
        BacktestOrder[] orders =
        [
            new(0, BacktestSide.Buy, BacktestOrderType.MarketOnOpen, 100),
            new(1, BacktestSide.Buy, BacktestOrderType.Limit, 20, 49.5),
            new(0, BacktestSide.Buy, BacktestOrderType.Limit, 10, 98.0), // touches the low only
        ];
        var fills = new BacktestFill[orders.Length];

        BacktestState state = bt.Step(bars, orders, fills, out int count);

        Assert.Equal(2, count);
        Assert.Equal(BacktestOrderType.MarketOnOpen, fills[0].Type);
        Assert.Equal(BacktestSide.Buy, fills[0].Side);
        Assert.Equal(100, fills[0].Quantity);
        Assert.Equal(100.1, fills[0].Price, 1e-12);
        Assert.Equal(39.0, fills[0].Courtage);
        Assert.Equal(10.0, fills[0].SpreadSlippageCost, 1e-9);
        Assert.Equal(1, fills[1].Instrument);
        Assert.Equal(1, fills[1].OrderIndex);
        Assert.Equal(49.5, fills[1].Price);
        Assert.Equal(0.0025 * 20 * 49.5, fills[1].FxFee, 1e-12);

        double spent = (100 * 100.1) + 39 + (20 * 49.5) + 39 + (0.0025 * 20 * 49.5);
        Assert.Equal(100_000 - spent, state.Cash, 1e-9);
        Assert.Equal(state.Cash + (100 * 101.0) + (20 * 50.0), state.Equity, 1e-9);
        Assert.Equal(78 + (0.0025 * 20 * 49.5) + 10.0, state.TotalCosts, 1e-9);
        Assert.Equal(2, state.Fills);
        Assert.Equal(3, state.Orders);

        Span<long> positions = stackalloc long[2];
        bt.GetPositions(positions);
        Assert.Equal([100L, 20L], positions.ToArray());

        // Next bar: sell everything at the close; a no-trading bar for the other instrument fills nothing.
        state = bt.Step(
            [new(101, 103, 100, 102, 1e6), BacktestBar.NoTrading],
            [new(0, BacktestSide.Sell, BacktestOrderType.MarketOnClose, 100), new(1, BacktestSide.Sell, BacktestOrderType.MarketOnOpen, 20)],
            fills,
            out count);
        Assert.Equal(1, count);
        Assert.Equal(BacktestSide.Sell, fills[0].Side);
        Assert.Equal(102 * 0.999, fills[0].Price, 1e-12);
        bt.GetPositions(positions);
        Assert.Equal([0L, 20L], positions.ToArray());
        Assert.Equal(state.Cash + (20 * 50.0), state.Equity, 1e-9); // last valid close of instrument 1
    }

    [Fact]
    public void InvalidInput_ThrowsWithTheNativeMessage_AndChangesNothing()
    {
        using var bt = QeBacktest.Create(Costs, TwoInstruments);
        BacktestBar[] bars = [new(100, 102, 98, 101, 1e6), new(50, 51, 49, 50, 1e6)];
        var fills = new BacktestFill[4];

        QeException notWholeLots = Assert.Throws<QeException>(
            () => bt.Step(bars, [new(1, BacktestSide.Buy, BacktestOrderType.Limit, 15, 50)], fills, out _));
        Assert.Equal(QeStatus.InvalidArgument, notWholeLots.Status);
        Assert.Contains("lot", notWholeLots.NativeMessage, StringComparison.Ordinal);

        Assert.Throws<QeException>(() => bt.Step([new(100, 99, 98, 101, 1e6), bars[1]], [], fills, out _)); // high < open
        Assert.Throws<QeException>(() => bt.Step(bars, [new(0, (BacktestSide)0, BacktestOrderType.Limit, 1, 100)], fills, out _));
        Assert.Throws<QeException>(() => bt.Step(bars, [new(5, BacktestSide.Buy, BacktestOrderType.Limit, 1, 100)], fills, out _));
        Assert.Throws<ArgumentException>(() => bt.Step([bars[0]], [], fills, out _));
        Assert.Throws<ArgumentException>(
            () => bt.Step(bars, [new(0, BacktestSide.Buy, BacktestOrderType.MarketOnOpen, 1)], Span<BacktestFill>.Empty, out _));

        BacktestState state = bt.Step(bars, [], fills, out int count);
        Assert.Equal(0, count);
        Assert.Equal(0, state.Orders);
        Assert.Equal(100_000, state.Cash);
    }

    [Fact]
    public void Create_ValidatesConfigAndInstruments()
    {
        QeException cap = Assert.Throws<QeException>(() => QeBacktest.Create(Costs with { ParticipationCap = 0 }, TwoInstruments));
        Assert.Equal("qe_bt_create", cap.Operation);
        Assert.Contains("participation_cap", cap.NativeMessage, StringComparison.Ordinal);
        Assert.Throws<QeException>(() => QeBacktest.Create(Costs with { InitialCash = double.NaN }, TwoInstruments));
        Assert.Throws<QeException>(() => QeBacktest.Create(Costs, [new BacktestInstrument(0, false)]));
        Assert.Throws<QeException>(() => QeBacktest.Create(Costs, []));
        Assert.Throws<ArgumentNullException>(() => QeBacktest.Create(null!, TwoInstruments));
    }

    [Fact]
    public void Dispose_IsIdempotent_AndUseAfterDisposeThrows()
    {
        var bt = QeBacktest.Create(Costs, TwoInstruments);
        bt.Dispose();
        bt.Dispose();
        Assert.Throws<ObjectDisposedException>(() => bt.Step([default, default], [], [], out _));
        Assert.Throws<ObjectDisposedException>(() => bt.GetPositions(new long[2]));
    }
}
