using System.Text.Json;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

/// <summary>config/paper.json, including the saved strategy <c>qa paper run</c> trades when none is given.</summary>
public sealed class PaperStrategyConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qa-paper-config", Guid.NewGuid().ToString("N"));

    public PaperStrategyConfigTests() => Directory.CreateDirectory(_dir);

    private string PaperJson => Path.Combine(_dir, PaperConfig.FileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void SaveStrategy_WritesOnlyTheStrategy_AndReadsBack_StringsOrNumbers()
    {
        File.WriteAllText(PaperJson, """{ "format": "qa-paper/1", "costs": "avanza-start", "cash": 5000, "decision_time": "09:10", "note": "keep me – Swedish ö too" }""");
        Assert.Null(PaperConfig.Load(PaperJson).Strategy);

        PaperConfig.SaveStrategy(PaperJson, new PaperStrategy("ma-cross", new Dictionary<string, string> { ["slow"] = "100", ["fast"] = "20" }));
        PaperConfig loaded = PaperConfig.Load(PaperJson);
        Assert.Equal("ma-cross", loaded.Strategy!.Name);
        Assert.Equal(["fast", "slow"], loaded.Strategy.Parameters.Keys);
        Assert.Equal("ma-cross --param fast=20 --param slow=100", loaded.Strategy.CommandLine());
        Assert.Equal((5000m, "avanza-start"), (loaded.Cash, loaded.Costs));
        string text = File.ReadAllText(PaperJson);
        Assert.Contains("keep me – Swedish ö too", text, StringComparison.Ordinal); // other members kept, readable

        // Hand-edited numbers are read as their literal text.
        File.WriteAllText(PaperJson, text.Replace("\"20\"", "20", StringComparison.Ordinal));
        Assert.Equal("20", PaperConfig.Load(PaperJson).Strategy!.Parameters["fast"]);

        PaperConfig.SaveStrategy(PaperJson, null);
        Assert.Null(PaperConfig.Load(PaperJson).Strategy);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(PaperJson));
        Assert.False(doc.RootElement.TryGetProperty("strategy", out _));
    }

    [Fact]
    public void ABrokenStrategy_IsAnError_ThatSavingAGoodOneRepairs()
    {
        File.WriteAllText(PaperJson, """{ "format": "qa-paper/1", "costs": "avanza-start", "cash": 5000, "decision_time": "09:10", "strategy": { "name": "", "params": {} } }""");
        Assert.Contains("strategy.name", Assert.Throws<TradingConfigException>(() => PaperConfig.Load(PaperJson)).Message, StringComparison.Ordinal);

        PaperConfig.SaveStrategy(PaperJson, new PaperStrategy("buy-and-hold", new Dictionary<string, string> { ["entry"] = "5" }));
        Assert.Equal("buy-and-hold", PaperConfig.Load(PaperJson).Strategy!.Name);
    }

    [Fact]
    public void SaveStrategy_RefusesAFileThatIsNotAPaperConfig()
    {
        Assert.Throws<TradingConfigException>(() => PaperConfig.SaveStrategy(PaperJson, null)); // missing
        File.WriteAllText(PaperJson, """{ "format": "qa-universe/1" }""");
        Assert.Contains("qa-paper/1", Assert.Throws<TradingConfigException>(() => PaperConfig.SaveStrategy(PaperJson, null)).Message, StringComparison.Ordinal);
        File.WriteAllText(PaperJson, "not json");
        Assert.Throws<TradingConfigException>(() => PaperConfig.SaveStrategy(PaperJson, null));
        Assert.Equal("not json", File.ReadAllText(PaperJson));
    }
}
