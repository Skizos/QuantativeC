using QuantAnalyst.Analytics.Data;

namespace QuantAnalyst.Analytics.Tests;

public sealed class CsvPriceLoaderTests
{
    [Fact]
    public void Parses_CommaSeparated_WithCommentsAndBlankLines()
    {
        string[] lines =
        [
            "# comment",
            "date,A,B",
            "2025-01-02,100,50.5",
            string.Empty,
            "2025-01-03,110,50",
            "2025-01-06,99,55.55",
        ];

        PriceTable table = CsvPriceLoader.Parse(lines, "test.csv");

        Assert.Equal(["A", "B"], table.Instruments);
        Assert.Equal(new DateOnly(2025, 1, 6), table.Dates[^1]);
        Assert.Equal(50.5, table.Prices[0, 1]);
        Assert.Equal(55.55, table.Prices[2, 1]);
    }

    [Fact]
    public void Parses_SwedishSemicolonFile_WithDecimalComma()
    {
        string[] lines = ["Date;ERIC-B;VOLV-B", "2025-01-02;74,12;251,3", "2025-01-03;75,00;250,1"];

        PriceTable table = CsvPriceLoader.Parse(lines, "sv.csv");

        Assert.Equal(74.12, table.Prices[0, 0]);
        Assert.Equal(250.1, table.Prices[1, 1]);
    }

    [Theory]
    [InlineData("date,A\n2025-01-03,1\n2025-01-02,2", "strictly increasing", 3)]
    [InlineData("date,A\n2025-01-02,1\n2025-01-03,-2", "positive number", 3)]
    [InlineData("date,A\n2025-01-02,1\n2025-01-03,", "positive number", 3)]
    [InlineData("date,A,B\n2025-01-02,1,2\n2025-01-03,1", "expected 3 cells", 3)]
    [InlineData("day,A\n2025-01-02,1\n2025-01-03,2", "header must be", 1)]
    [InlineData("date,A\n02/01/2025,1\n2025-01-03,2", "yyyy-MM-dd", 2)]
    [InlineData("date,A,a\n2025-01-02,1,1\n2025-01-03,2,2", "unique", 1)]
    public void Rejects_BadInput_WithLineNumber(string csv, string expectedMessage, int line)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => CsvPriceLoader.Parse(csv.Split('\n'), "bad.csv"));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.Ordinal);
        Assert.StartsWith($"bad.csv:{line}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SimpleAndLogReturns_AreComputedPerPeriod()
    {
        PriceTable table = CsvPriceLoader.Parse(["date,A", "2025-01-02,100", "2025-01-03,110", "2025-01-06,99"], "t");

        ReturnsTable simple = table.ToReturns(ReturnKind.Simple);
        ReturnsTable log = table.ToReturns(ReturnKind.Log);

        Assert.Equal(2, simple.Returns.Rows);
        Assert.Equal(0.1, simple.Returns[0, 0], tolerance: 1e-15);
        Assert.Equal(-0.1, simple.Returns[1, 0], tolerance: 1e-15);
        Assert.Equal(Math.Log(1.1), log.Returns[0, 0], tolerance: 1e-15);
        Assert.Equal(new DateOnly(2025, 1, 3), simple.Dates[0]);
    }

    [Fact]
    public void Select_ReordersAndRejectsUnknownInstruments()
    {
        ReturnsTable r = CsvPriceLoader.Parse(["date,A,B", "2025-01-02,1,2", "2025-01-03,2,3"], "t").ToReturns(ReturnKind.Simple);

        ReturnsTable selected = r.Select(["b", "A"]);

        Assert.Equal(["b", "A"], selected.Instruments);
        Assert.Equal(0.5, selected.Returns[0, 0], tolerance: 1e-15);
        Assert.Throws<ArgumentException>(() => r.Select(["C"]));
    }

    [Fact]
    public void SyntheticSample_Loads()
    {
        PriceTable table = CsvPriceLoader.Load(Path.Combine(AppContext.BaseDirectory, "data", "synthetic-prices.csv"));

        Assert.Equal(260, table.Dates.Count);
        Assert.Contains("SYN-INDEX", table.Instruments);
    }
}
