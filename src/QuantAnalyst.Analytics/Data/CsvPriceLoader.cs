using System.Globalization;
using QuantAnalyst.Native;

namespace QuantAnalyst.Analytics.Data;

/// <summary>
/// Loads close prices from CSV: a header <c>date,NAME1,NAME2,...</c> then one row per date
/// (<c>yyyy-MM-dd</c>, strictly increasing). Comma-separated files use '.' decimals; semicolon-separated
/// files (Swedish Excel exports) may use ',' decimals. Blank lines and lines starting with '#' are ignored.
/// </summary>
public static class CsvPriceLoader
{
    /// <summary>Loads and validates a price file.</summary>
    public static PriceTable Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllLines(path), path);
    }

    /// <summary>Parses and validates CSV lines. Errors cite the 1-based line number.</summary>
    public static PriceTable Parse(IReadOnlyList<string> lines, string source)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var content = lines
            .Select((text, index) => (Text: text.Trim(), Line: index + 1))
            .Where(l => l.Text.Length > 0 && !l.Text.StartsWith('#'))
            .ToList();
        if (content.Count < 3)
        {
            throw new InvalidDataException($"{source}: need a header and at least two price rows.");
        }

        (string headerText, int headerLine) = content[0];
        char delimiter = headerText.Contains(';', StringComparison.Ordinal) && !headerText.Contains(',', StringComparison.Ordinal) ? ';' : ',';
        string[] header = headerText.Split(delimiter).Select(h => h.Trim()).ToArray();
        if (header.Length < 2 || !string.Equals(header[0], "date", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{source}:{headerLine}: header must be 'date{delimiter}NAME1{delimiter}...'.");
        }

        string[] instruments = header[1..];
        var duplicate = instruments.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null || instruments.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException($"{source}:{headerLine}: instrument names must be unique and non-empty.");
        }

        var dates = new List<DateOnly>(content.Count - 1);
        var prices = new DenseMatrix(content.Count - 1, instruments.Length);
        for (int row = 0; row < content.Count - 1; row++)
        {
            (string text, int line) = content[row + 1];
            string[] cells = text.Split(delimiter);
            if (cells.Length != header.Length)
            {
                throw new InvalidDataException($"{source}:{line}: expected {header.Length} cells, found {cells.Length}.");
            }

            if (!DateOnly.TryParseExact(cells[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                throw new InvalidDataException($"{source}:{line}: date '{cells[0]}' is not yyyy-MM-dd.");
            }

            if (dates.Count > 0 && date <= dates[^1])
            {
                throw new InvalidDataException($"{source}:{line}: dates must be strictly increasing ({date:yyyy-MM-dd} after {dates[^1]:yyyy-MM-dd}).");
            }

            dates.Add(date);
            for (int c = 0; c < instruments.Length; c++)
            {
                string cell = cells[c + 1].Trim();
                if (delimiter == ';')
                {
                    cell = cell.Replace(',', '.');
                }

                if (!double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double price) ||
                    !double.IsFinite(price) || price <= 0)
                {
                    throw new InvalidDataException($"{source}:{line}: {instruments[c]} price '{cells[c + 1]}' must be a positive number.");
                }

                prices[row, c] = price;
            }
        }

        return new PriceTable(source, dates, instruments, prices);
    }
}
