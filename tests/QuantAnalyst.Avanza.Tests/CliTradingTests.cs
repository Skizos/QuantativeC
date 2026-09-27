using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Kill;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>Phase 6 offline verbs: <c>qa universe</c>, <c>qa risk-limits</c>, <c>qa audit verify</c>, <c>qa kill</c>.</summary>
public sealed class CliTradingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-cli-trading-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAvanza _server = new();

    public CliTradingTests()
    {
        Directory.CreateDirectory(Config);
        string repoConfig = Path.Combine(RepoRoot(), "config");
        File.Copy(Path.Combine(repoConfig, "risk-limits.json"), Path.Combine(Config, "risk-limits.json"));
        File.WriteAllText(Path.Combine(Config, "paper.json"), """
            { "format": "qa-paper/1", "costs": "avanza-start", "cash": 45000, "decision_time": "09:10" }
            """);
    }

    private string Config => Path.Combine(_root, "config");

    private string Store => Path.Combine(_root, "q.duckdb");

    private string State => Path.Combine(_root, "state");

    private string Audit => Path.Combine(_root, "audit");

    private string KillFile => Path.Combine(_root, "KILL");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private (int Code, string Output, string Error) Qa(params string[] args)
    {
        var services = new AvanzaCliServices(
            (options, secrets, prompt, logger, redactor) => AvanzaConnection.CreateForTest(
                new AvanzaOptions { StateDirectory = options.StateDirectory, LoginMethod = options.LoginMethod, RequestsPerSecond = 10, Burst = 20 },
                secrets, logger, redactor, TimeProvider.System, _server, prompt),
            _ => FakeSecrets.Store());
        var output = new StringWriter();
        var error = new StringWriter();
        int code = QaCli.Run(args, output, error, services);
        return (code, output.ToString(), error.ToString());
    }

    private (int, string, string) Kill(params string[] args) =>
        Qa(["kill", .. args, "--kill-file", KillFile, "--state-dir", State, "--audit-dir", Audit]);

    [Fact]
    public void Universe_StartsEmpty_AddsFromTheInstrumentMaster_AndRemoves()
    {
        (int code, string output, _) = Qa("universe", "list", "--config-dir", Config);
        Assert.Equal(0, code);
        Assert.Contains("empty", output, StringComparison.Ordinal);

        (code, _, string error) = Qa("universe", "add", "ERIC-B", "--config-dir", Config, "--store", Store);
        Assert.Equal(1, code);
        Assert.Contains("No history store", error, StringComparison.Ordinal); // offline: the master must exist

        Assert.Equal(0, Qa("history", "import", "ERIC-B", "--from", "2026-09-24", "--to", "2026-09-25", "--store", Store,
            "--state-dir", State, "--login", "totp").Code);
        (code, output, error) = Qa("universe", "add", "ERIC-B", "--config-dir", Config, "--store", Store);
        Assert.True(code == 0, error);
        Assert.Contains("added ERIC B (5240", output, StringComparison.Ordinal);

        (_, output, _) = Qa("universe", "list", "--config-dir", Config);
        Assert.Contains("5240", output, StringComparison.Ordinal);
        Assert.Contains("1 instrument(s)", output, StringComparison.Ordinal);

        (code, _, error) = Qa("universe", "add", "NOPE-X", "--config-dir", Config, "--store", Store);
        Assert.Equal(1, code);
        Assert.Contains("not in the instrument master", error, StringComparison.Ordinal);

        Assert.Equal(0, Qa("universe", "remove", "eric-b", "--config-dir", Config).Code);
        Assert.Contains("empty", Qa("universe", "list", "--config-dir", Config).Output, StringComparison.Ordinal);
        Assert.Contains("is not in the allowlist", Qa("universe", "remove", "ERIC-B", "--config-dir", Config).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RiskLimits_AreSizedForThePaperAccount()
    {
        (int code, string output, string error) = Qa("risk-limits", "--config-dir", Config);
        Assert.True(code == 0, error);
        // The paper account has 45,000 SEK, but the committed account cap sizes every limit on 5,000 SEK.
        Assert.Contains("sized on at most 5,000 SEK", output, StringComparison.Ordinal);
        Assert.Contains("sized on 5,000 SEK, not 45,000", output, StringComparison.Ordinal);
        Assert.Contains("500 SEK per order", output, StringComparison.Ordinal); // min(25,000, 10 % of 5,000)
        Assert.Contains("1,000 SEK per instrument", output, StringComparison.Ordinal);
        Assert.Contains("Sized for an account of 45,000 SEK, sized on the 5,000 SEK account cap", output, StringComparison.Ordinal);
        Assert.Contains("09:05–17:20", output, StringComparison.Ordinal);

        (_, output, _) = Qa("risk-limits", "--config-dir", Config, "--account-value", "1000000");
        Assert.Contains("500 SEK per order", output, StringComparison.Ordinal); // a large account doesn't raise them
        Assert.Contains("100 SEK", output, StringComparison.Ordinal); // R19: 2 % of the cap

        (_, output, _) = Qa("risk-limits", "--config-dir", Config, "--account-value", "3000");
        Assert.Contains("300 SEK per order", output, StringComparison.Ordinal); // below the cap: the account itself
        Assert.Contains("sized on 3,000 SEK", output, StringComparison.Ordinal);
        Assert.DoesNotContain("account cap (max_account_value_sek)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditVerify_ReportsIntactAndBrokenChains()
    {
        var log = new AuditLog(Audit, TimeProvider.System);
        log.Append("a", new { n = 1 });
        log.Append("b", new { n = 2 });
        (int code, string output, _) = Qa("audit", "verify", "--dir", Audit);
        Assert.Equal(0, code);
        Assert.Contains("OK: 2 record(s)", output, StringComparison.Ordinal);

        string file = Directory.GetFiles(Audit).Single();
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"n\":1", "\"n\":7", StringComparison.Ordinal));
        (code, output, _) = Qa("audit", "verify", "--dir", Audit);
        Assert.Equal(2, code);
        Assert.Contains("BROKEN", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Kill_WritesTheFlag_ShowsStatus_AndResetsOnlyWithoutARunningSession()
    {
        Assert.Contains("not active", Kill("--status").Item2, StringComparison.Ordinal);
        Assert.Contains("not active", Kill("--reset").Item2, StringComparison.Ordinal);

        (int code, string output, _) = Kill("--reason", "owner test");
        Assert.Equal(0, code);
        Assert.True(File.Exists(KillFile));
        Assert.Contains("next one will start halted", output, StringComparison.Ordinal);
        Assert.Contains("ACTIVE", Kill("--status").Item2, StringComparison.Ordinal);

        using (SessionLock.Acquire(State, TimeProvider.System))
        {
            (code, output, _) = Kill("--reset");
            Assert.Equal(1, code);
            Assert.Contains("session is running", output, StringComparison.Ordinal);
            Assert.True(File.Exists(KillFile));
        }

        (code, output, _) = Kill("--reset", "--reason", "checked by the owner");
        Assert.Equal(0, code);
        Assert.Contains("reset", output, StringComparison.Ordinal);
        Assert.False(File.Exists(KillFile));
        Assert.Contains("kill-reset", File.ReadAllText(Directory.GetFiles(Audit).Single()), StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionLock_OfADeadProcess_IsStale()
    {
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, SessionLock.FileName), "999999999 2026-09-26T00:00:00Z\n");
        Assert.Null(SessionLock.Holder(State));
        using SessionLock taken = SessionLock.Acquire(State, TimeProvider.System);
        Assert.NotNull(SessionLock.Holder(State));
        Assert.Throws<InvalidOperationException>(() => SessionLock.Acquire(State, TimeProvider.System));
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "QuantAnalyst.sln")))
            {
                return d.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
