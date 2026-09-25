namespace QuantAnalyst.Native;

/// <summary>Dense row-major matrix of doubles, the matrix format of the qe C ABI.</summary>
public sealed class DenseMatrix
{
    private readonly double[] data;

    /// <summary>Initializes a zero matrix.</summary>
    public DenseMatrix(int rows, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        Rows = rows;
        Columns = columns;
        data = new double[checked(rows * columns)];
    }

    /// <summary>Initializes a matrix by copying row-major values.</summary>
    public DenseMatrix(int rows, int columns, ReadOnlySpan<double> rowMajor)
        : this(rows, columns)
    {
        if (rowMajor.Length != data.Length)
        {
            throw new ArgumentException($"Expected {data.Length} values for a {rows}x{columns} matrix, got {rowMajor.Length}.", nameof(rowMajor));
        }

        rowMajor.CopyTo(data);
    }

    /// <summary>Gets the number of rows.</summary>
    public int Rows { get; }

    /// <summary>Gets the number of columns.</summary>
    public int Columns { get; }

    /// <summary>Gets or sets an element.</summary>
    public double this[int row, int column]
    {
        get => data[Index(row, column)];
        set => data[Index(row, column)] = value;
    }

    /// <summary>Builds a matrix from equally long rows.</summary>
    public static DenseMatrix FromRows(IReadOnlyList<IReadOnlyList<double>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0 || rows[0].Count == 0)
        {
            throw new ArgumentException("At least one row and one column are required.", nameof(rows));
        }

        var m = new DenseMatrix(rows.Count, rows[0].Count);
        for (int r = 0; r < rows.Count; r++)
        {
            if (rows[r].Count != m.Columns)
            {
                throw new ArgumentException($"Row {r} has {rows[r].Count} values; expected {m.Columns}.", nameof(rows));
            }

            for (int c = 0; c < m.Columns; c++)
            {
                m[r, c] = rows[r][c];
            }
        }

        return m;
    }

    /// <summary>Gets the row-major values.</summary>
    public ReadOnlySpan<double> AsSpan() => data;

    /// <summary>Copies one row.</summary>
    public double[] GetRow(int row) => data.AsSpan(Index(row, 0), Columns).ToArray();

    internal Span<double> AsWritableSpan() => data;

    private int Index(int row, int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Rows);
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, Columns);
        return (row * Columns) + column;
    }
}
