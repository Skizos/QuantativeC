using System.Text.RegularExpressions;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>
/// The Windows app trades Paper only (docs/plans/10-windows-app.md). Its sources, XAML included, never ask for a
/// trading mode, never promote, and never name the order or preflight plumbing. The IL of its core is checked in
/// OrderArchitectureTests as well.
/// </summary>
public sealed partial class AppSafetyTests
{
    private static readonly string[] AppProjects = ["QuantAnalyst.Desktop", "QuantAnalyst.Desktop.Core"];

    [Fact]
    public void TheAppSources_NeverSelectAMode_Promote_OrNameOrderPlumbing()
    {
        string root = TempWorkspace.RepoRoot();
        string[] files =
        [
            .. AppProjects
                .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, "src", p), "*.*", SearchOption.AllDirectories))
                .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)),
        ];
        Assert.True(files.Length >= 15, $"only {files.Length} app source files found");

        var offenders = files
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(x => Forbidden().IsMatch(x.Text))
            .Select(x => $"{Path.GetRelativePath(root, x.File)}:{x.Line}: {x.Text.Trim()}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("\"--mode\", \"confirm\"")]
    [InlineData("TRADING__MODE")]
    [InlineData("[\"promote\", \"--to\"]")]
    [InlineData("qa promote --to Confirm")]
    [InlineData("connection.CreateOrderChannel()")]
    [InlineData("AvanzaPreflightRoutes.Validate")]
    public void TheForbiddenPattern_CatchesWhatItMust(string sample) => Assert.Matches(Forbidden(), sample);

    [GeneratedRegex(@"--mode|trading__mode|""promote""|qa promote|CreateOrderChannel|CreatePreflight|AvanzaOrderRoutes|AvanzaOrderChannel|AvanzaPreflight|IBrokerOrderChannel|OrderGateway", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();
}
