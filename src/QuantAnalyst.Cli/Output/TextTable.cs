namespace QuantAnalyst.Cli.Output;

/// <summary>Minimal left/right aligned text table for terminal output.</summary>
internal sealed class TextTable
{
    private readonly string[] headers;
    private readonly bool[] rightAligned;
    private readonly List<string[]> rows = [];

    public TextTable(params (string Header, bool Right)[] columns)
    {
        headers = columns.Select(c => c.Header).ToArray();
        rightAligned = columns.Select(c => c.Right).ToArray();
    }

    public void Add(params string[] cells)
    {
        if (cells.Length != headers.Length)
        {
            throw new ArgumentException($"Expected {headers.Length} cells.", nameof(cells));
        }

        rows.Add(cells);
    }

    public void Write(TextWriter writer)
    {
        int[] widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        WriteRow(writer, headers, widths);
        writer.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (string[] row in rows)
        {
            WriteRow(writer, row, widths);
        }
    }

    private void WriteRow(TextWriter writer, string[] cells, int[] widths) =>
        writer.WriteLine(string.Join("  ", cells.Select((c, i) => rightAligned[i] ? c.PadLeft(widths[i]) : c.PadRight(widths[i]))).TrimEnd());
}
