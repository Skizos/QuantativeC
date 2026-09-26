using QuantAnalyst.Analytics.Backtesting;

namespace QuantAnalyst.Analytics.Tests;

public sealed class TrialLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qa-ledger-tests", Guid.NewGuid().ToString("N"));

    public TrialLedgerTests() => Directory.CreateDirectory(_dir);

    private string LedgerPath => Path.Combine(_dir, "research", "trial-ledger.jsonl");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    internal static TrialRecord Record(string study = "ma-cross|SYN|2016-01-01..2025-09-30|synthetic", double sharpe = 0.05, TrialStatus status = TrialStatus.Ok) => new()
    {
        Id = "pending",
        RecordedAtUtc = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero),
        Runner = "test",
        GitCommit = null,
        Study = study,
        Strategy = "ma-cross",
        Parameters = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["fast"] = "20", ["slow"] = "100" },
        Universe = ["SYN0001", "SYN0002"],
        DataSource = "synthetic-gbm",
        PointInTime = true,
        SurvivorshipFree = true,
        From = new DateOnly(2016, 1, 1),
        To = new DateOnly(2025, 9, 30),
        Seed = 42,
        CostModel = "avanza-small",
        CostsVerified = false,
        HoldoutTouched = false,
        Status = status,
        Metrics = status == TrialStatus.Ok
            ? new TrialMetrics(2520, sharpe, sharpe * Math.Sqrt(252), -0.1, 4.2, 0.35, 0.03, 0.2, 0.25, 1.5, 1234.5, 0.9, null, 1, null)
            : null,
    };

    [Fact]
    public void Appends_AreChained_AndVerify()
    {
        var ledger = new TrialLedger(LedgerPath);
        Assert.True(ledger.Verify().Valid); // missing file = empty ledger
        TrialRecord a = ledger.Append(Record(sharpe: 0.04));
        TrialRecord b = ledger.Append(Record(sharpe: 0.06));
        TrialRecord c = ledger.Append(Record(status: TrialStatus.RejectedLeakage));

        Assert.Equal([1L, 2L, 3L], new[] { a.Sequence, b.Sequence, c.Sequence });
        Assert.Equal(["T000001", "T000002", "T000003"], new[] { a.Id, b.Id, c.Id });
        Assert.Equal(TrialLedger.GenesisHash, a.PrevHash);
        Assert.NotEqual(a.PrevHash, b.PrevHash);

        LedgerVerification v = ledger.Verify();
        Assert.True(v.Valid, v.Problem);
        Assert.Equal(3, v.Records);

        IReadOnlyList<TrialRecord> all = ledger.ReadAll();
        Assert.Equal(3, all.Count);
        Assert.Equal(0.06, all[1].Metrics!.SharpePerPeriod);
        Assert.Equal("20", all[0].Parameters["fast"]);
        Assert.Equal(TrialStatus.RejectedLeakage, all[2].Status);
        Assert.Null(all[2].Metrics);
    }

    [Fact]
    public void EditedOrDeletedLines_AreDetected()
    {
        var ledger = new TrialLedger(LedgerPath);
        for (int i = 0; i < 3; i++)
        {
            ledger.Append(Record(sharpe: 0.01 * (i + 1)));
        }

        string[] lines = File.ReadAllLines(LedgerPath);
        File.WriteAllLines(LedgerPath, [lines[0], lines[1].Replace("\"sharpePerPeriod\":0.02", "\"sharpePerPeriod\":0.09", StringComparison.Ordinal), lines[2]]);
        LedgerVerification edited = ledger.Verify();
        Assert.False(edited.Valid);
        Assert.Contains("line 2", edited.Problem, StringComparison.Ordinal);
        Assert.Contains("hash", edited.Problem, StringComparison.Ordinal);

        File.WriteAllLines(LedgerPath, [lines[0], lines[2]]);
        LedgerVerification deleted = ledger.Verify();
        Assert.False(deleted.Valid);
        Assert.Contains("line 2: prevHash", deleted.Problem, StringComparison.Ordinal);

        File.WriteAllLines(LedgerPath, [lines[0], "not json", lines[2]]);
        Assert.False(ledger.Verify().Valid);
    }

    [Fact]
    public async Task ConcurrentAppends_StayOneChain()
    {
        var ledger = new TrialLedger(LedgerPath);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 10; i++)
            {
                new TrialLedger(LedgerPath).Append(Record(sharpe: (w * 10) + i));
            }
        }, TestContext.Current.CancellationToken)));

        LedgerVerification v = ledger.Verify();
        Assert.True(v.Valid, v.Problem);
        Assert.Equal(80, v.Records);
        Assert.Equal(Enumerable.Range(1, 80).Select(i => (long)i), ledger.ReadAll().Select(r => r.Sequence));
    }

    [Fact]
    public void StudyStats_CountOnlyCompletedTrialsOfTheStudy()
    {
        var ledger = new TrialLedger(LedgerPath);
        ledger.Append(Record(sharpe: 0.02));
        ledger.Append(Record(sharpe: 0.04));
        ledger.Append(Record(sharpe: 0.06));
        ledger.Append(Record(status: TrialStatus.RejectedLeakage));
        ledger.Append(Record(study: "other", sharpe: 1.0));

        (int trials, double std) = ledger.StudyStats(Record().Study);
        Assert.Equal(3, trials);
        Assert.Equal(0.02, std, 1e-15);
        Assert.Equal((0, double.NaN), ledger.StudyStats("nothing"));
        Assert.Equal(1, ledger.StudyStats("other").Trials);
    }

    [Fact]
    public void GitCommitIsFoundFromTheTestDirectory()
    {
        string? commit = GitInfo.TryGetCommit(AppContext.BaseDirectory);
        Assert.NotNull(commit);
        Assert.Equal(40, commit.Length);
        Assert.Null(GitInfo.TryGetCommit(Path.GetTempPath()));
    }

    [Fact]
    public void CommittedLedger_Verifies()
    {
        // The repository's own ledger must always verify (CI catches a hand-edited line).
        string? root = FindRepoRoot();
        Assert.NotNull(root);
        string path = Path.Combine(root, TrialLedger.DefaultPath);
        LedgerVerification v = new TrialLedger(path).Verify();
        Assert.True(v.Valid, v.Problem);
    }

    private static string? FindRepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "QuantAnalyst.sln")))
            {
                return d.FullName;
            }
        }

        return null;
    }
}
