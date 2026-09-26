using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Cli;

namespace QuantAnalyst.Analytics.Tests;

public sealed class CliTests
{
    private static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "data", "synthetic-prices.csv");

    [Fact]
    public void Price_BlackScholes_PrintsSpecReferenceAndGreeks()
    {
        (int code, string output, _) = Run("price", "--spot", "100", "--strike", "100", "--rate", "0.05", "--vol", "0.2", "--expiry", "1");

        Assert.Equal(0, code);
        Assert.Contains("10.450584", output, StringComparison.Ordinal);
        Assert.Contains("delta", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Price_ParsesInvariantNumbersUnderSwedishCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            (int code, string output, string error) = Run("price", "--spot", "100", "--strike", "100", "--rate", "0.05", "--vol", "0.2", "--expiry", "1");

            Assert.True(code == 0, error);
            Assert.Contains("10.450584", output, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Price_MonteCarloJson_RecordsSeedAndPaths()
    {
        (int code, string output, _) = Run("price", "--spot", "100", "--strike", "100", "--rate", "0.05", "--vol", "0.2", "--expiry", "1",
            "--model", "mc", "--paths", "20000", "--seed", "7", "--antithetic", "--json");

        Assert.Equal(0, code);
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.Equal(7UL, doc.RootElement.GetProperty("seed").GetUInt64());
        Assert.Equal(20000, doc.RootElement.GetProperty("paths").GetInt64());
        Assert.True(Math.Abs(doc.RootElement.GetProperty("zScore").GetDouble()) < 3);
    }

    [Fact]
    public void Price_ImpliedVolatility()
    {
        (int code, string output, _) = Run("price", "--spot", "100", "--strike", "100", "--rate", "0.05", "--expiry", "1", "--implied-from", "10.450583572185565");

        Assert.Equal(0, code);
        Assert.Contains("0.200000", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Risk_PrintsLabelsVarTableAndStress()
    {
        (int code, string output, string error) = Run("risk", "--prices", Sample, "--index", "SYN-INDEX", "--fx-exposed", "SYN-D");

        Assert.True(code == 0, error);
        Assert.Contains("NOT survivorship-free", output, StringComparison.Ordinal);
        Assert.Contains("historical", output, StringComparison.Ordinal);
        Assert.Contains("monte-carlo", output, StringComparison.Ordinal);
        Assert.Contains("SYN-INDEX -10", output, StringComparison.Ordinal);
        Assert.Contains("20260925", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Optimize_JsonWeightsSumToOne()
    {
        (int code, string output, string error) = Run("optimize", "--prices", Sample, "--index", "SYN-INDEX", "--method", "rp", "--json");

        Assert.True(code == 0, error);
        using JsonDocument doc = JsonDocument.Parse(output);
        double sum = doc.RootElement.GetProperty("weights").EnumerateArray().Sum(w => w.GetProperty("weight").GetDouble());
        Assert.Equal(1.0, sum, tolerance: 1e-12);
        Assert.Equal("Simple", doc.RootElement.GetProperty("label").GetProperty("returns").GetString());
    }

    [Fact]
    public void Errors_GoToStderrWithExitCodeOne()
    {
        (int code, _, string error) = Run("risk", "--prices", "does-not-exist.csv");

        Assert.Equal(1, code);
        Assert.StartsWith("error:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidChoice_IsAParseError()
    {
        (int code, _, string error) = Run("price", "--spot", "1", "--strike", "1", "--vol", "0.2", "--expiry", "1", "--model", "quantum");

        Assert.NotEqual(0, code);
        Assert.Contains("quantum", error, StringComparison.Ordinal);
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        int code = QaCli.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
