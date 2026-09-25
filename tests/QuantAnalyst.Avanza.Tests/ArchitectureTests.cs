using System.Reflection;
using System.Text.RegularExpressions;
using QuantAnalyst.Avanza.Http;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// Structural guarantees for the read-only phase (CLAUDE.md "Absolute safety rules", ADR 0002, ADR 0004), checked
/// by scanning the source tree so they also cover code that is never executed by tests.
/// </summary>
public sealed partial class ArchitectureTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    // Order entry, stop-loss, fund orders (ADR 0002 §6) and any money movement (ADR 0004).
    private static readonly Regex Forbidden = ForbiddenRoutes();

    private static IEnumerable<(string File, string Literal)> SourceStringLiterals() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => StringLiteral().Matches(File.ReadAllText(f)).Select(m => (f, m.Value)));

    [Fact]
    public void ApiPathsLiveOnlyInTheRoutesFile()
    {
        var offenders = SourceStringLiterals()
            .Where(x => x.Literal.Contains("/_api/", StringComparison.Ordinal) || x.Literal.Contains("/_push/", StringComparison.Ordinal))
            .Where(x => !x.File.EndsWith($"Http{Path.DirectorySeparatorChar}AvanzaRoutes.cs", StringComparison.Ordinal))
            .Select(x => $"{Path.GetRelativePath(RepoRoot, x.File)}: {x.Literal}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void NoOrderStopLossOrMoneyTransferRouteExistsInSource()
    {
        var offenders = SourceStringLiterals()
            .Where(x => x.Literal.Contains("/_api", StringComparison.Ordinal) || x.Literal.Contains("/_push", StringComparison.Ordinal))
            .Where(x => Forbidden.IsMatch(x.Literal))
            .Select(x => $"{Path.GetRelativePath(RepoRoot, x.File)}: {x.Literal}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void RoutesAreReadOnly_OnlyLoginStepsAndSearchArePosts()
    {
        FieldInfo[] fields = typeof(AvanzaRoutes).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(AvanzaRoute)).ToArray();
        AvanzaRoute[] declared = [.. fields.Select(f => (AvanzaRoute)f.GetValue(null)!)];

        Assert.Equal(declared.Length, AvanzaRoutes.All.Count); // nothing hidden outside All
        Assert.All(declared, r => Assert.Contains(r, AvanzaRoutes.All));
        Assert.All(declared, r => Assert.DoesNotMatch(Forbidden, r.PathTemplate));
        Assert.Equal(
            ["auth.bankid.collect", "auth.bankid.restart", "auth.bankid.start", "auth.totp", "auth.usercredentials", "search"],
            declared.Where(r => r.Method != "GET").Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.All(declared, r => Assert.StartsWith("https://github.com/", r.Source, StringComparison.Ordinal));
    }

    [Fact]
    public void AvanzaDtosAreNotReferencedOutsideTheAvanzaProject()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}QuantAnalyst.Avanza{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("QuantAnalyst.Avanza.Dto", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(offenders);
        Assert.All(typeof(AvanzaRoutes).Assembly.GetTypes().Where(t => t.Namespace == "QuantAnalyst.Avanza.Dto"), t => Assert.False(t.IsPublic, t.FullName));
    }

    [Fact]
    public void ForbiddenPatternCatchesKnownOrderAndTransferPaths()
    {
        // Built from fragments so this file never contains a complete order route literal.
        string[] samples =
        [
            "/_api/trading/" + "order-entry/order/new",
            "/_api/trading-critical/rest/" + "order/modify",
            "/_api/trading/" + "stoploss/new",
            "/_api/" + "transfer/internal",
            "/_api/payment/" + "withdrawal",
        ];
        Assert.All(samples, s => Assert.Matches(Forbidden, s));
        Assert.DoesNotMatch(Forbidden, "/_api/transactions/list");
        Assert.DoesNotMatch(Forbidden, "/_api/trading/rest/orders");
        Assert.DoesNotMatch(Forbidden, "/_api/trading-critical/rest/orderbook/{0}");
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "QuantAnalyst.sln")))
            {
                return d.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (QuantAnalyst.sln) not found.");
    }

    [GeneratedRegex("\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"order-entry|rest/order/(new|modify|delete)|/order/(new|modify|delete)|stoploss|stop-loss|fund-order|transfer|withdraw|deposit|payment|uttag|overforing|insattning", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenRoutes();
}
