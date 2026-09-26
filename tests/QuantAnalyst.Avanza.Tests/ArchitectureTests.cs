using System.Reflection;
using System.Text.RegularExpressions;
using QuantAnalyst.Avanza.Http;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// Structural guarantees (CLAUDE.md "Absolute safety rules", ADR 0002, ADR 0003, ADR 0004), checked by scanning the
/// source tree so they also cover code that is never executed by tests. Since Phase 6 the three order-entry routes
/// exist, only in <c>AvanzaOrderRoutes</c>; <c>OrderArchitectureTests</c> scans the IL for who uses them.
/// </summary>
public sealed partial class ArchitectureTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    // Order entry, stop-loss, fund orders (ADR 0002 §6) and any money movement (ADR 0004).
    private static readonly Regex Forbidden = ForbiddenRoutes();

    // The subset that is never allowed anywhere; order entry is allowed only as the three AvanzaOrderRoutes (Phase 6).
    private static readonly Regex Never = NeverRoutes();

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
    public void NoStopLossFundOrderOrMoneyTransferRouteExistsInSource()
    {
        var offenders = SourceStringLiterals()
            .Where(x => x.Literal.Contains("/_api", StringComparison.Ordinal) || x.Literal.Contains("/_push", StringComparison.Ordinal))
            .Where(x => Never.IsMatch(x.Literal))
            .Select(x => $"{Path.GetRelativePath(RepoRoot, x.File)}: {x.Literal}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void OrderEntryPaths_AppearOnlyAsTheThreeOrderRoutes()
    {
        string[] allowed = [.. AvanzaOrderRoutes.All.Select(r => $"\"{r.PathTemplate}\"")];
        var offenders = SourceStringLiterals()
            .Where(x => x.Literal.Contains("/_api", StringComparison.Ordinal) && Forbidden.IsMatch(x.Literal))
            .Where(x => !(x.File.EndsWith($"Http{Path.DirectorySeparatorChar}AvanzaRoutes.cs", StringComparison.Ordinal) && allowed.Contains(x.Literal)))
            .Select(x => $"{Path.GetRelativePath(RepoRoot, x.File)}: {x.Literal}")
            .ToList();
        Assert.Empty(offenders);

        // Each allowed literal appears exactly once, inside AvanzaOrderRoutes.
        string routes = File.ReadAllText(Path.Combine(RepoRoot, "src", "QuantAnalyst.Avanza", "Http", "AvanzaRoutes.cs"));
        int orderClass = routes.IndexOf("internal static class AvanzaOrderRoutes", StringComparison.Ordinal);
        Assert.True(orderClass > 0);
        Assert.All(allowed, a =>
        {
            Assert.Equal(1, routes.Split(a).Length - 1);
            Assert.True(routes.IndexOf(a, StringComparison.Ordinal) > orderClass, $"{a} is outside AvanzaOrderRoutes");
        });
    }

    [Fact]
    public void OrderRoutes_AreInternal_TierA_PostsOnly_AndSeparateFromTheReadRoutes()
    {
        Assert.False(typeof(AvanzaOrderRoutes).IsPublic);
        Assert.Equal(["order.delete", "order.modify", "order.place"], AvanzaOrderRoutes.All.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.All(AvanzaOrderRoutes.All, r =>
        {
            Assert.Equal("POST", r.Method);
            Assert.Equal(QuantAnalyst.Core.Broker.DtoTier.A, r.Tier);
            Assert.False(r.IsAuthentication);
            Assert.DoesNotContain(r, AvanzaRoutes.All);
            Assert.StartsWith("https://github.com/", r.Source, StringComparison.Ordinal);
        });
        FieldInfo[] declared = typeof(AvanzaOrderRoutes).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(AvanzaRoute)).ToArray();
        Assert.Equal(AvanzaOrderRoutes.All.Count, declared.Length);
    }

    [Fact]
    public void PreflightRoutes_AreInternal_TierA_Posts_AndNeitherReadNorOrderRoutes()
    {
        Assert.False(typeof(AvanzaPreflightRoutes).IsPublic);
        Assert.Equal(["preflight.fee", "preflight.validate"], AvanzaPreflightRoutes.All.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.All(AvanzaPreflightRoutes.All, r =>
        {
            Assert.Equal("POST", r.Method);
            Assert.Equal(QuantAnalyst.Core.Broker.DtoTier.A, r.Tier);
            Assert.False(r.IsAuthentication);
            Assert.DoesNotContain(r, AvanzaRoutes.All);
            Assert.DoesNotContain(AvanzaOrderRoutes.All, o => o.PathTemplate == r.PathTemplate);
            Assert.DoesNotMatch(Forbidden, r.PathTemplate); // a check, not an order: the hook and the order rules don't apply
            Assert.StartsWith("https://github.com/vmorsell/avanza-sdk-go/blob/", r.Source, StringComparison.Ordinal);
        });
        FieldInfo[] declared = typeof(AvanzaPreflightRoutes).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(AvanzaRoute)).ToArray();
        Assert.Equal(AvanzaPreflightRoutes.All.Count, declared.Length);
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

    // Never, in any file (ADR 0002 §6 stop-loss and fund orders; ADR 0004 money movement).
    [GeneratedRegex(@"stoploss|stop-loss|fund-order|transfer|withdraw|deposit|payment|uttag|overforing|insattning", RegexOptions.IgnoreCase)]
    private static partial Regex NeverRoutes();
}
