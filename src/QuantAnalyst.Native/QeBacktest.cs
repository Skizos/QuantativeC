using QuantAnalyst.Native.Interop;

namespace QuantAnalyst.Native;

/// <summary>
/// The native event-driven backtest engine (qe_api.h, ABI 1.2; 1.3 adds per-instrument courtage): one <see cref="Step"/> per bar with the day orders
/// decided at the previous close. Not thread-safe; dispose once.
/// </summary>
public sealed class QeBacktest : IDisposable
{
    private readonly QeBacktestHandle handle;

    private QeBacktest(QeBacktestHandle handle, int instrumentCount)
    {
        this.handle = handle;
        InstrumentCount = instrumentCount;
    }

    /// <summary>Gets the number of instruments; every step takes one bar per instrument.</summary>
    public int InstrumentCount { get; }

    /// <summary>Creates a backtest after verifying the native ABI version. The instrument order fixes the indices.</summary>
    public static unsafe QeBacktest Create(BacktestConfig config, ReadOnlySpan<BacktestInstrument> instruments)
    {
        ArgumentNullException.ThrowIfNull(config);
        QeAbi.EnsureCompatible();
        var native = new QeBtConfig(config);
        QeStatus status;
        QeBacktestHandle created;
        fixed (BacktestInstrument* pInstruments = instruments)
        {
            status = QeNative.BtCreate(&native, pInstruments, instruments.Length, out created);
        }

        if (status != QeStatus.Ok)
        {
            QeException error = QeErrors.CreateException(status, "qe_bt_create");
            created.Dispose();
            throw error;
        }

        return new QeBacktest(created, instruments.Length);
    }

    /// <summary>
    /// Processes one bar. <paramref name="fills"/> must hold at least <c>orders.Length</c> entries (an order fills at
    /// most once); the first <paramref name="fillCount"/> are written. Invalid input throws and changes nothing.
    /// </summary>
    /// <returns>The state after the bar.</returns>
    public unsafe BacktestState Step(
        ReadOnlySpan<BacktestBar> bars,
        ReadOnlySpan<BacktestOrder> orders,
        Span<BacktestFill> fills,
        out int fillCount)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (bars.Length != InstrumentCount)
        {
            throw new ArgumentException($"bars must have {InstrumentCount} elements (one per instrument), got {bars.Length}.", nameof(bars));
        }

        if (fills.Length < orders.Length)
        {
            throw new ArgumentException($"fills must hold at least {orders.Length} elements, got {fills.Length}.", nameof(fills));
        }

        long count;
        BacktestState state;
        fixed (BacktestBar* pBars = bars)
        fixed (BacktestOrder* pOrders = orders)
        fixed (BacktestFill* pFills = fills)
        {
            QeErrors.ThrowIfFailed(
                QeNative.BtStep(handle, pBars, bars.Length, pOrders, orders.Length, pFills, fills.Length, &count, &state),
                "qe_bt_step");
        }

        fillCount = (int)count;
        return state;
    }

    /// <summary>
    /// Gives one instrument its own courtage, max(<paramref name="courtageMin"/>, <paramref name="courtageRate"/> × notional),
    /// instead of the config's: a foreign share pays its market's (ADR 0005, ABI 1.3). Only before the first
    /// <see cref="Step"/>; invalid input throws and changes nothing.
    /// </summary>
    public void SetCourtage(int instrument, double courtageMin, double courtageRate)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        QeErrors.ThrowIfFailed(QeNative.BtSetCourtage(handle, instrument, courtageMin, courtageRate), "qe_bt_set_courtage");
    }

    /// <summary>Copies the positions (shares per instrument) into <paramref name="positions"/>.</summary>
    public unsafe void GetPositions(Span<long> positions)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (positions.Length != InstrumentCount)
        {
            throw new ArgumentException($"positions must have {InstrumentCount} elements, got {positions.Length}.", nameof(positions));
        }

        fixed (long* p = positions)
        {
            QeErrors.ThrowIfFailed(QeNative.BtPositions(handle, p, positions.Length), "qe_bt_positions");
        }
    }

    /// <inheritdoc/>
    public void Dispose() => handle.Dispose();
}
