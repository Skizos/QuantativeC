using System.Runtime.InteropServices;

namespace QuantAnalyst.Native;

/// <summary>Backtest order type (<c>QE_BT_*</c>).</summary>
public enum BacktestOrderType
{
    /// <summary>Day limit order: fills at the open when marketable there, else at the limit only if traded through.</summary>
    Limit = 0,

    /// <summary>Opening auction at open ± (half-spread + slippage).</summary>
    MarketOnOpen = 1,

    /// <summary>Closing auction at close ± (half-spread + slippage).</summary>
    MarketOnClose = 2,
}

/// <summary>Backtest order side (<c>QE_BT_BUY</c> / <c>QE_BT_SELL</c>).</summary>
public enum BacktestSide
{
    /// <summary>Sell (reduces a long position; the engine is long-only).</summary>
    Sell = -1,

    /// <summary>Buy.</summary>
    Buy = 1,
}

/// <summary>Costs, cash and limits of a backtest (see qe_api.h, <c>qe_bt_config</c>). Money is double: the engine is a model.</summary>
public sealed record BacktestConfig
{
    /// <summary>Gets the starting cash.</summary>
    public double InitialCash { get; init; }

    /// <summary>Gets the minimum courtage per fill: courtage = max(min, rate × notional).</summary>
    public double CourtageMin { get; init; }

    /// <summary>Gets the proportional courtage rate.</summary>
    public double CourtageRate { get; init; }

    /// <summary>Gets the FX fee rate on the notional of foreign-currency instruments.</summary>
    public double FxFeeRate { get; init; }

    /// <summary>Gets the slippage on market-type fills, in basis points.</summary>
    public double SlippageBps { get; init; }

    /// <summary>Gets the half-spread paid on market-type fills, in basis points.</summary>
    public double HalfSpreadBps { get; init; }

    /// <summary>Gets the share of a bar's volume one instrument may trade, in (0, 1].</summary>
    public double ParticipationCap { get; init; } = 1.0;
}

/// <summary>An instrument of a backtest (mirror of <c>qe_bt_instrument</c>, 16 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct BacktestInstrument
{
    private readonly long lotSize;
    private readonly int foreignCurrency;
    private readonly int reserved;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="lotSize">Shares per lot, &gt;= 1.</param>
    /// <param name="foreignCurrency">True when the FX fee applies.</param>
    public BacktestInstrument(long lotSize, bool foreignCurrency)
    {
        this.lotSize = lotSize;
        this.foreignCurrency = foreignCurrency ? 1 : 0;
        reserved = 0;
    }

    /// <summary>Gets the lot size.</summary>
    public long LotSize => lotSize;

    /// <summary>Gets a value indicating whether the FX fee applies.</summary>
    public bool ForeignCurrency => foreignCurrency != 0;
}

/// <summary>One instrument's daily bar (mirror of <c>qe_bt_bar</c>, 48 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct BacktestBar
{
    private readonly double open;
    private readonly double high;
    private readonly double low;
    private readonly double close;
    private readonly double volume;
    private readonly int valid;
    private readonly int reserved;

    /// <summary>Initializes a traded bar.</summary>
    public BacktestBar(double open, double high, double low, double close, double volume)
    {
        this.open = open;
        this.high = high;
        this.low = low;
        this.close = close;
        this.volume = volume;
        valid = 1;
        reserved = 0;
    }

    /// <summary>Gets a bar without trading (holiday, halt, not listed): nothing fills and day orders expire.</summary>
    public static BacktestBar NoTrading => default;

    /// <summary>Gets the open.</summary>
    public double Open => open;

    /// <summary>Gets the high.</summary>
    public double High => high;

    /// <summary>Gets the low.</summary>
    public double Low => low;

    /// <summary>Gets the close.</summary>
    public double Close => close;

    /// <summary>Gets the traded volume in shares.</summary>
    public double Volume => volume;

    /// <summary>Gets a value indicating whether the instrument traded on this bar.</summary>
    public bool IsValid => valid != 0;
}

/// <summary>A day order for one bar (mirror of <c>qe_bt_order</c>, 32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct BacktestOrder
{
    private readonly int instrument;
    private readonly int side;
    private readonly int type;
    private readonly int reserved;
    private readonly long quantity;
    private readonly double limitPrice;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="instrument">Index into the instruments of the backtest.</param>
    /// <param name="side">Buy or sell.</param>
    /// <param name="type">Order type.</param>
    /// <param name="quantity">Shares, a positive multiple of the lot size.</param>
    /// <param name="limitPrice">Limit price (limit orders only).</param>
    public BacktestOrder(int instrument, BacktestSide side, BacktestOrderType type, long quantity, double limitPrice = 0.0)
    {
        this.instrument = instrument;
        this.side = (int)side;
        this.type = (int)type;
        reserved = 0;
        this.quantity = quantity;
        this.limitPrice = limitPrice;
    }

    /// <summary>Gets the instrument index.</summary>
    public int Instrument => instrument;

    /// <summary>Gets the side.</summary>
    public BacktestSide Side => (BacktestSide)side;

    /// <summary>Gets the order type.</summary>
    public BacktestOrderType Type => (BacktestOrderType)type;

    /// <summary>Gets the quantity in shares.</summary>
    public long Quantity => quantity;

    /// <summary>Gets the limit price.</summary>
    public double LimitPrice => limitPrice;
}

/// <summary>A fill (mirror of <c>qe_bt_fill</c>, 56 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct BacktestFill
{
    private readonly int instrument;
    private readonly int side;
    private readonly int type;
    private readonly int orderIndex;
    private readonly long quantity;
    private readonly double price;
    private readonly double courtage;
    private readonly double fxFee;
    private readonly double spreadSlippageCost;

    /// <summary>Gets the instrument index.</summary>
    public int Instrument => instrument;

    /// <summary>Gets the side.</summary>
    public BacktestSide Side => (BacktestSide)side;

    /// <summary>Gets the order type.</summary>
    public BacktestOrderType Type => (BacktestOrderType)type;

    /// <summary>Gets the index of the order in the step call.</summary>
    public int OrderIndex => orderIndex;

    /// <summary>Gets the filled shares.</summary>
    public long Quantity => quantity;

    /// <summary>Gets the fill price (spread and slippage included for market-type fills).</summary>
    public double Price => price;

    /// <summary>Gets the courtage charged.</summary>
    public double Courtage => courtage;

    /// <summary>Gets the FX fee charged.</summary>
    public double FxFee => fxFee;

    /// <summary>Gets |price − auction price| × quantity for market-type fills (0 for limits).</summary>
    public double SpreadSlippageCost => spreadSlippageCost;
}

/// <summary>Backtest state after a bar (mirror of <c>qe_bt_state</c>, 64 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct BacktestState
{
    private readonly double cash;
    private readonly double equity;
    private readonly double grossExposure;
    private readonly double courtage;
    private readonly double fxFees;
    private readonly double spreadSlippage;
    private readonly long fills;
    private readonly long orders;

    /// <summary>Gets the cash.</summary>
    public double Cash => cash;

    /// <summary>Gets cash plus positions at the last valid close.</summary>
    public double Equity => equity;

    /// <summary>Gets the positions at the last valid close.</summary>
    public double GrossExposure => grossExposure;

    /// <summary>Gets the cumulative courtage.</summary>
    public double Courtage => courtage;

    /// <summary>Gets the cumulative FX fees.</summary>
    public double FxFees => fxFees;

    /// <summary>Gets the cumulative spread and slippage cost.</summary>
    public double SpreadSlippage => spreadSlippage;

    /// <summary>Gets the cumulative number of fills.</summary>
    public long Fills => fills;

    /// <summary>Gets the cumulative number of orders submitted.</summary>
    public long Orders => orders;

    /// <summary>Gets the cumulative costs of all kinds.</summary>
    public double TotalCosts => courtage + fxFees + spreadSlippage;
}
