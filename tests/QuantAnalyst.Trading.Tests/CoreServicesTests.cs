using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qa-trading-tests", Guid.NewGuid().ToString("N"));

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class AuditLogTests
{
    [Fact]
    public void ChainContinuesAcrossDaysAndRestarts_AndVerifies()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 21, 30, 0, TimeSpan.Zero)); // 23:30 Stockholm
        var log = new AuditLog(dir.Path, time);
        log.Append("intent", new { ticker = "ERIC B", qty = 10 });
        time.Advance(TimeSpan.FromHours(1)); // 00:30 Stockholm the next day: a new file
        log.Append("risk", new { passed = true });

        var reopened = new AuditLog(dir.Path, time); // a restart continues the same chain
        Assert.Equal(3, reopened.Append("halt", new { reason = "test" }));

        Assert.Equal(["2026-09-28.jsonl", "2026-09-29.jsonl"], Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order());
        AuditVerification v = AuditLog.Verify(dir.Path);
        Assert.True(v.Valid, v.Problem);
        Assert.Equal(3, v.Records);
        Assert.Equal(2, v.Files);
    }

    [Fact]
    public void EditedLines_DeletedLines_AndDeletedDays_AreDetected()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero));
        var log = new AuditLog(dir.Path, time);
        for (int day = 0; day < 3; day++)
        {
            log.Append("a", new { day, n = 1 });
            log.Append("b", new { day, n = 2 });
            time.Advance(TimeSpan.FromDays(1));
        }

        string middle = dir.File("2026-09-29.jsonl");
        string[] lines = File.ReadAllLines(middle);

        File.WriteAllLines(middle, [lines[0].Replace("\"n\":1", "\"n\":9", StringComparison.Ordinal), lines[1]]);
        Assert.Contains("edited", AuditLog.Verify(dir.Path).Problem, StringComparison.Ordinal);

        File.WriteAllLines(middle, [lines[1]]);
        Assert.Contains("prevHash", AuditLog.Verify(dir.Path).Problem, StringComparison.Ordinal);

        File.Delete(middle); // a whole day removed
        AuditVerification v = AuditLog.Verify(dir.Path);
        Assert.False(v.Valid);
        Assert.Contains("2026-09-30.jsonl line 1", v.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveValues_NeverReachTheFile()
    {
        using var dir = new TempDir();
        var log = new AuditLog(dir.Path, TimeProvider.System);
        var account = new AccountId("98765432101");
        log.AddSensitive(account.Value, account.Masked);
        log.Append("submit", new { account = account.Value, note = $"for account {account.Value}" });

        string text = File.ReadAllText(Directory.GetFiles(dir.Path).Single());
        Assert.DoesNotContain("98765432101", text, StringComparison.Ordinal);
        Assert.Contains("***101", text, StringComparison.Ordinal);
        Assert.True(AuditLog.Verify(dir.Path).Valid);
    }
}

public sealed class HaltControllerTests
{
    [Fact]
    public void RaiseIsIdempotent_Audited_AndTheKillSwitchIsSticky()
    {
        using var dir = new TempDir();
        var audit = new AuditLog(dir.Path, TimeProvider.System);
        var halts = new HaltController(audit, TimeProvider.System);
        int raised = 0;
        halts.Raised += _ => raised++;

        halts.Raise(HaltReason.StaleData, "stream down");
        halts.Raise(HaltReason.StaleData, "again");
        halts.Raise(HaltReason.KillSwitch, "qa kill");
        Assert.Equal(2, raised);
        Assert.Equal("stream down", halts.Active.Single(h => h.Reason == HaltReason.StaleData).Detail);

        halts.Clear(HaltReason.StaleData, "fresh again");
        Assert.Throws<InvalidOperationException>(() => halts.Clear(HaltReason.KillSwitch, "nope"));
        Assert.True(halts.IsHalted);
        halts.ClearKill("reset by the owner");
        Assert.False(halts.IsHalted);

        string[] kinds = [.. File.ReadLines(Directory.GetFiles(dir.Path).Single()).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("record").GetProperty("kind").GetString()!)];
        Assert.Equal(["halt", "halt", "halt-cleared", "halt-cleared"], kinds);
    }
}

public sealed class TradingConfigTests
{
    private static string RepoConfig()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "QuantAnalyst.sln")))
            {
                return Path.Combine(d.FullName, "config");
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void CommittedRiskLimits_Load_AndValidate()
    {
        // Whatever values the owner sets, the committed file must load and pass validation.
        RiskLimits limits = RiskLimits.Load(Path.Combine(RepoConfig(), RiskLimits.FileName));
        Assert.True(limits.MaxOrderValueSek > 0);
        Assert.True(limits.WindowOpen < limits.WindowClose);
    }

    [Fact]
    public void AdrDefaults_AreValid_AndBadFilesAreRejected()
    {
        RiskLimits.AdrDefaults.Validate("defaults");
        using var dir = new TempDir();
        string path = dir.File("risk-limits.json");
        Assert.Throws<TradingConfigException>(() => RiskLimits.Load(path)); // missing: nothing trades

        string good = File.ReadAllText(Path.Combine(RepoConfig(), RiskLimits.FileName));
        File.WriteAllText(path, good.Replace("\"max_gross_exposure_pct\": 1.00", "\"max_gross_exposure_pct\": 1.50", StringComparison.Ordinal));
        Assert.Contains("no leverage", Assert.Throws<TradingConfigException>(() => RiskLimits.Load(path)).Message, StringComparison.Ordinal);
        File.WriteAllText(path, good.Replace("\"max_orders_per_day\": 20,", string.Empty, StringComparison.Ordinal));
        Assert.Throws<TradingConfigException>(() => RiskLimits.Load(path)); // every field is required
        File.WriteAllText(path, good.Replace("\"open\": \"09:05\"", "\"open\": \"18:00\"", StringComparison.Ordinal));
        Assert.Throws<TradingConfigException>(() => RiskLimits.Load(path));
    }

    [Fact]
    public void Universe_RoundTrips_RejectsDuplicates_AndMissingMeansEmpty()
    {
        using var dir = new TempDir();
        string path = dir.File("universe.json");
        Assert.Empty(Universe.Load(path).Entries);

        Universe u = Universe.Empty.With(new UniverseEntry(new OrderbookId("5240"), "ERIC B", "Ericsson B")).With(new UniverseEntry(new OrderbookId("1001"), "TEST B", "Test B"));
        u.Save(path);
        Universe back = Universe.Load(path);
        Assert.Equal(["ERIC B", "TEST B"], back.Entries.Select(e => e.Ticker));
        Assert.True(back.Contains(new OrderbookId("5240")));
        Assert.False(back.Without(new OrderbookId("5240")).Contains(new OrderbookId("5240")));

        Assert.Throws<TradingConfigException>(() => new Universe([new UniverseEntry(new OrderbookId("1"), "A", "A"), new UniverseEntry(new OrderbookId("1"), "B", "B")]));
    }
}
