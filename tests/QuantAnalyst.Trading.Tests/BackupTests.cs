using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Backup;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

/// <summary>Plan 25: a backup copies a fixed list, checks every copy, and keeps the newest few.</summary>
public sealed class BackupTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 15, 45, 0, TimeSpan.Zero)); // 17:45 Stockholm

    public BackupTests()
    {
        var audit = new AuditLog(_dir.File("audit"), _time);
        audit.Append("session-start", new { mode = "Paper" });
        audit.Append("end-of-day", new { date = "2026-10-05" });
        Directory.CreateDirectory(_dir.File(Path.Combine("state", "paper", "manual")));
        File.WriteAllText(_dir.File(Path.Combine("state", "paper", "book.json")), """{ "cash": 5000 }""");
        File.WriteAllText(_dir.File(Path.Combine("state", "paper", "manual", "done.jsonl")), "{}\n");
        File.WriteAllText(_dir.File(Path.Combine("state", "auth.json")), """{ "secret": "never copied" }""");
        Directory.CreateDirectory(_dir.File("config"));
        File.WriteAllText(_dir.File(Path.Combine("config", "paper.json")), "{}");
        new TrialLedger(_dir.File("ledger.jsonl")).Append(Trial());
        using HistoryStore store = HistoryStore.Open(_dir.File("quant.duckdb"));
        store.RegisterSource(AvanzaChartImporter.AvanzaPriceChart);
        store.UpsertDailyBars(new OrderbookId("5240"), [new DailyBar(new DateOnly(2026, 10, 2), 70m, 71m, 69m, 70.5m, 1_000), new DailyBar(new DateOnly(2026, 10, 5), 70.5m, 72m, 70m, 71.9m, 1_200)],
            AvanzaChartImporter.AvanzaPriceChart, "test", _time.GetUtcNow());
    }

    public void Dispose() => _dir.Dispose();

    private string Backups => _dir.File("backups");

    private BackupSource[] Sources() =>
    [
        new("audit", _dir.File("audit"), BackupKind.Folder),
        new("paper", _dir.File(Path.Combine("state", "paper")), BackupKind.Folder),
        new("config", _dir.File("config"), BackupKind.Folder),
        new("promotion", _dir.File("promotion"), BackupKind.Folder), // none yet
        new("ledger", _dir.File("ledger.jsonl"), BackupKind.File),
        new("store", _dir.File("quant.duckdb"), BackupKind.Store),
    ];

    [Fact]
    public void ABackup_CopiesTheListAndChecksEveryCopy()
    {
        BackupResult r = Backup.Backups.Run(Sources(), Backups, keep: 7, _time);

        Assert.Equal(Path.Combine(Path.GetFullPath(Backups), "qa-backup-2026-10-05_174500"), r.Folder);
        Assert.True(r.Manifest.Complete);
        BackupItem audit = r.Manifest.Items.Single(i => i.Name == "audit");
        Assert.EndsWith("audit chain intact (2 records)", audit.Check, StringComparison.Ordinal);
        Assert.Equal(["paper/book.json", "paper/manual/done.jsonl"], r.Manifest.Items.Single(i => i.Name == "paper").Files.Select(f => f.Path));
        Assert.EndsWith("ledger chain intact (1 trials)", r.Manifest.Items.Single(i => i.Name == "ledger").Check, StringComparison.Ordinal);
        Assert.Equal("nothing yet (no folder)", r.Manifest.Items.Single(i => i.Name == "promotion").Check);
        BackupItem store = r.Manifest.Items.Single(i => i.Name == "store");
        Assert.Equal(2, store.Rows!["daily_bars"]);
        Assert.EndsWith("the same in the copy", store.Check, StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(r.Folder, "paper", "auth.json"))); // only state/paper, never the login state
        Assert.Empty(Directory.EnumerateFiles(r.Folder, "auth.json", SearchOption.AllDirectories));
        Assert.True(AuditLog.Verify(Path.Combine(r.Folder, "audit")).Valid);
        Assert.Equal(2, HistoryStore.RowCountsOf(Path.Combine(r.Folder, "store", "quant.duckdb"))["daily_bars"]);

        BackupCheck check = Backup.Backups.Verify(r.Folder);
        Assert.True(check.Ok, string.Join("\n", check.Lines));
        Assert.Contains("audit: ok (", check.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_FindsAChangedOrMissingFile()
    {
        string folder = Backup.Backups.Run(Sources(), Backups, keep: 7, _time).Folder;
        File.AppendAllText(Path.Combine(folder, "paper", "book.json"), " ");
        File.Delete(Path.Combine(folder, "ledger", "ledger.jsonl"));

        BackupCheck check = Backup.Backups.Verify(folder);

        Assert.False(check.Ok);
        Assert.Contains("paper: FAILED: paper/book.json has changed size", check.Lines);
        Assert.Contains("ledger: FAILED: ledger/ledger.jsonl is missing", check.Lines);
        Assert.False(Backup.Backups.Verify(_dir.File("config")).Ok); // no manifest: not a backup
    }

    [Fact]
    public void TheNewestKeep_Stay_AndNothingElseIsTouched()
    {
        Directory.CreateDirectory(Path.Combine(Backups, "my-own-files"));
        Directory.CreateDirectory(Path.Combine(Backups, "qa-backup-2026-01-01_000000")); // no manifest: not ours to remove
        Directory.CreateDirectory(Path.Combine(Backups, "qa-backup-2026-10-04_180000.partial")); // a backup that stopped half-way
        var made = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            made.Add(Backup.Backups.Run(Sources(), Backups, keep: 2, _time).Folder);
            _time.Advance(TimeSpan.FromDays(1));
        }

        Assert.Equal([made[2], made[1]], Backup.Backups.List(Backups));
        Assert.True(Directory.Exists(Path.Combine(Backups, "my-own-files")));
        Assert.True(Directory.Exists(Path.Combine(Backups, "qa-backup-2026-01-01_000000")));
        Assert.False(Directory.Exists(Path.Combine(Backups, "qa-backup-2026-10-04_180000.partial")));
        Assert.Equal(made[2], Backup.Backups.Latest(Backups));
    }

    [Fact]
    public void ABackupFolderInsideWhatItBacksUp_IsRefused_AndTwoInOneSecondBothStay()
    {
        Assert.Contains("is inside", Assert.Throws<ArgumentException>(() => Backup.Backups.Run(Sources(), _dir.File(Path.Combine("audit", "backups")), 7, _time)).Message, StringComparison.Ordinal);

        string first = Backup.Backups.Run(Sources(), Backups, 7, _time).Folder;
        string second = Backup.Backups.Run(Sources(), Backups, 7, _time).Folder;
        Assert.Equal(first + "-2", second);
    }

    [Fact]
    public void AStoreCopy_GoesIntoANewFileOnly()
    {
        using HistoryStore store = HistoryStore.Open(_dir.File("quant.duckdb"));
        string target = _dir.File("copy.duckdb");
        store.CopyTo(target);
        Assert.Equal(store.RowCounts(), HistoryStore.RowCountsOf(target));
        Assert.Throws<IOException>(() => store.CopyTo(target));
    }

    [Fact]
    public void TheSettings_RoundTrip_AndAreCheckedWhenRead()
    {
        string config = _dir.File("config");
        Assert.Null(BackupSettings.Load(config));
        new BackupSettings(@"D:\Backups\QuantAnalyst", 14, Automatic: false).Save(config);
        Assert.Equal(new BackupSettings(@"D:\Backups\QuantAnalyst", 14, false), BackupSettings.Load(config));

        File.WriteAllText(Path.Combine(config, BackupSettings.FileName), """{ "format": "qa-backup/1", "to": "x", "keep": 0 }""");
        Assert.Throws<TradingConfigException>(() => BackupSettings.Load(config));
    }

    private static TrialRecord Trial() => new()
    {
        Id = "-",
        RecordedAtUtc = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero),
        Runner = "owner",
        Study = "s",
        Strategy = "buy-and-hold",
        Parameters = new Dictionary<string, string> { ["entry"] = "5" },
        Universe = ["ERIC B"],
        DataSource = AvanzaChartImporter.AvanzaPriceChart.Name,
        PointInTime = false,
        SurvivorshipFree = false,
        From = new DateOnly(2016, 1, 4),
        To = new DateOnly(2026, 9, 25),
        Seed = 1,
        CostModel = "avanza-start",
        CostsVerified = true,
        HoldoutTouched = false,
        Status = TrialStatus.Ok,
        Metrics = new TrialMetrics(500, 0.05, 0.79, 0, 3, 0.1, 0.05, 0.16, 0.2, 1, 100, 0.9, null, 1, null),
    };
}
