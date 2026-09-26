using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Data;

/// <summary>How returns are computed from prices.</summary>
public enum ReturnKind
{
    /// <summary>p_t / p_(t-1) - 1. Aggregates linearly across a portfolio.</summary>
    Simple,

    /// <summary>ln(p_t / p_(t-1)).</summary>
    Log,
}

/// <summary>Close prices: one row per date (strictly increasing), one column per instrument.</summary>
public sealed class PriceTable
{
    /// <summary>Initializes a new instance.</summary>
    public PriceTable(string source, IReadOnlyList<DateOnly> dates, IReadOnlyList<string> instruments, DenseMatrix prices)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(prices);
        if (prices.Rows != dates.Count || prices.Columns != instruments.Count)
        {
            throw new ArgumentException("prices must be dates.Count x instruments.Count.", nameof(prices));
        }

        Source = source;
        Dates = dates;
        Instruments = instruments;
        Prices = prices;
    }

    /// <summary>Gets where the data came from (file path or description).</summary>
    public string Source { get; }

    /// <summary>Gets the observation dates.</summary>
    public IReadOnlyList<DateOnly> Dates { get; }

    /// <summary>Gets the instrument (column) names.</summary>
    public IReadOnlyList<string> Instruments { get; }

    /// <summary>Gets the T x N price matrix.</summary>
    public DenseMatrix Prices { get; }

    /// <summary>Computes returns; the result has one row less, dated by each period's end.</summary>
    public ReturnsTable ToReturns(ReturnKind kind)
    {
        int t = Dates.Count - 1;
        if (t < 1)
        {
            throw new InvalidOperationException("At least two price rows are needed to compute returns.");
        }

        var returns = new DenseMatrix(t, Instruments.Count);
        for (int row = 0; row < t; row++)
        {
            for (int c = 0; c < Instruments.Count; c++)
            {
                double ratio = Prices[row + 1, c] / Prices[row, c];
                returns[row, c] = kind == ReturnKind.Log ? Math.Log(ratio) : ratio - 1.0;
            }
        }

        return new ReturnsTable(Source, Dates.Skip(1).ToArray(), Instruments, returns, kind);
    }
}

/// <summary>Periodic returns: one row per period end date, one column per instrument.</summary>
public sealed class ReturnsTable
{
    /// <summary>Initializes a new instance.</summary>
    public ReturnsTable(string source, IReadOnlyList<DateOnly> dates, IReadOnlyList<string> instruments, DenseMatrix returns, ReturnKind kind)
    {
        ArgumentNullException.ThrowIfNull(returns);
        Source = source;
        Dates = dates;
        Instruments = instruments;
        Returns = returns;
        Kind = kind;
    }

    /// <summary>Gets where the data came from.</summary>
    public string Source { get; }

    /// <summary>Gets the period end dates.</summary>
    public IReadOnlyList<DateOnly> Dates { get; }

    /// <summary>Gets the instrument names.</summary>
    public IReadOnlyList<string> Instruments { get; }

    /// <summary>Gets the T x N return matrix.</summary>
    public DenseMatrix Returns { get; }

    /// <summary>Gets how returns were computed.</summary>
    public ReturnKind Kind { get; }

    /// <summary>Gets the returns of one instrument.</summary>
    public double[] Column(string instrument)
    {
        int c = IndexOf(instrument);
        var values = new double[Returns.Rows];
        for (int r = 0; r < values.Length; r++)
        {
            values[r] = Returns[r, c];
        }

        return values;
    }

    /// <summary>Keeps the given instruments, in the given order.</summary>
    public ReturnsTable Select(IReadOnlyList<string> instruments)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        if (instruments.Count == 0)
        {
            throw new ArgumentException("Select at least one instrument.", nameof(instruments));
        }

        int[] columns = instruments.Select(IndexOf).ToArray();
        var m = new DenseMatrix(Returns.Rows, columns.Length);
        for (int r = 0; r < Returns.Rows; r++)
        {
            for (int c = 0; c < columns.Length; c++)
            {
                m[r, c] = Returns[r, columns[c]];
            }
        }

        return new ReturnsTable(Source, Dates, instruments.ToArray(), m, Kind);
    }

    /// <summary>Per-instrument sample mean return.</summary>
    public double[] MeanReturns()
    {
        var means = new double[Returns.Columns];
        for (int c = 0; c < means.Length; c++)
        {
            double sum = 0;
            for (int r = 0; r < Returns.Rows; r++)
            {
                sum += Returns[r, c];
            }

            means[c] = sum / Returns.Rows;
        }

        return means;
    }

    private int IndexOf(string instrument)
    {
        for (int i = 0; i < Instruments.Count; i++)
        {
            if (string.Equals(Instruments[i], instrument, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ArgumentException($"Unknown instrument '{instrument}'. Available: {string.Join(", ", Instruments)}.", nameof(instrument));
    }
}
