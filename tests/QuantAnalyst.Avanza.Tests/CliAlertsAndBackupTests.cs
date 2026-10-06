using System.Text;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Trading.Alerts;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Kill;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>Plan 25: <c>qa alerts</c>, <c>qa backup</c> and their <c>qa status</c> lines, offline.</summary>
public sealed class CliAlertsAndBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-cli-alerts", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 16, 0, 0, TimeSpan.Zero)); // 18:00 Stockholm
    private readonly List<Alert> _shown = [];

    public CliAlertsAndBackupTests()
    {
        Directory.CreateDirectory(Config);
        string repo = Path.Combine(RepoRoot(), "config");
        foreach (string f in new[] { "risk-limits.json", "costs.avanza-start.json", "market-calendar.XSTO.2026.json", "market-calendar.XSTO.2027.json" })
        {
            File.Copy(Path.Combine(repo, f), Path.Combine(Config, f));
        }

        File.WriteAllText(Path.Combine(Config, "paper.json"), """{ "format": "qa-paper/1", "costs": "avanza-start", "cash": 5000, "decision_time": "09:10" }""");
        File.WriteAllText(Path.Combine(Config, "universe.json"), """{ "format": "qa-universe/1", "instruments": [] }""");
        new AuditLog(Audit, _time).Append("session-start", new { mode = "Paper" });
        Directory.CreateDirectory(Path.Combine(State, "paper"));
        File.WriteAllText(Path.Combine(State, "paper", "book.json"), "{}");
        File.WriteAllText(Path.Combine(State, "auth.json"), """{ "never": "backed up" }""");
    }

    private string Config => Path.Combine(_root, "config");

    private string State => Path.Combine(_root, "state");

    private string Audit => Path.Combine(_root, "audit");

    private string Store => Path.Combine(_root, "q.duckdb");

    private string Ledger => Path.Combine(_root, "ledger.jsonl");

    private string Backups => Path.Combine(_root, "backups");

    private string[] Paths => ["--config-dir", Config, "--state-dir", State, "--audit-dir", Audit, "--store", Store, "--ledger", Ledger, "--promotion-dir", Path.Combine(_root, "promotion")];

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
        var services = new AvanzaCliServices((_, _, _, _, _) => throw new InvalidOperationException("no network in these tests"), _ => FakeSecrets.Store())
        {
            Time = _time,
            Notifier = () => new Recording(_shown),
        };
        var output = new StringWriter();
        var error = new StringWriter();
        int code = QaCli.Run(args, output, error, services);
        return (code, output.ToString(), error.ToString());
    }

    private string Status() =>
        Qa("status", "--config-dir", Config, "--store", Store, "--state-dir", State, "--audit-dir", Audit, "--kill-file", Path.Combine(_root, "KILL"),
            "--promotion-dir", Path.Combine(_root, "promotion"), "--ledger", Ledger).Output;

    [Fact]
    public void AlertsTest_ShowsANotification_AndAlertsListsIt_AndSetTurnsThemOff()
    {
        (int code, string output, string error) = Qa("alerts", "test", "--config-dir", Config, "--state-dir", State);
        Assert.True(code == 0, error);
        Assert.StartsWith("ALERT: Test alert at 18:00: alerts reach you.", output, StringComparison.Ordinal);
        Alert shown = Assert.Single(_shown);
        Assert.Equal((AlertLevel.Info, "test", "qa alerts test"), (shown.Level, shown.Kind, shown.Source));

        (_, output, _) = Qa("alerts", "--state-dir", State);
        Assert.Contains("1 alert(s) in the last 7 day(s):", output, StringComparison.Ordinal);
        Assert.Contains("2026-10-05 18:00 INFO Test alert: Test alert at 18:00: alerts reach you. [qa alerts test]", output, StringComparison.Ordinal);

        (_, output, _) = Qa("alerts", "set", "--notifications", "off", "--config-dir", Config);
        Assert.Contains("Notifications: off; day summary: on.", output, StringComparison.Ordinal);
        Assert.Contains("Notifications are off", Qa("alerts", "test", "--config-dir", Config, "--state-dir", State).Output, StringComparison.Ordinal);
        Assert.Single(_shown); // logged, not shown
        Assert.Equal(2, new AlertLog(State).Since(DateTimeOffset.MinValue).Count);
        Assert.Contains("'maybe' is not on or off", Qa("alerts", "set", "--day-summary", "maybe", "--config-dir", Config).Error, StringComparison.Ordinal);
        Assert.Contains("No alerts in the last 1 day(s)", Qa("alerts", "--days", "1", "--state-dir", Path.Combine(_root, "elsewhere")).Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_ShowsTheDaysWarnings_AndACriticalAsAFailWithAStep()
    {
        Assert.DoesNotContain("Alerts", Status(), StringComparison.Ordinal); // none yet (an info alert is no warning)
        var log = new AlertLog(State);
        log.Append(new Alert(_time.GetUtcNow().AddHours(-2), AlertLevel.Warning, "decision-failed", "Decision failed", "Paper decision failed: no history.", "Paper session"));
        Assert.Contains("warn  Alerts", Status(), StringComparison.Ordinal);

        log.Append(new Alert(_time.GetUtcNow().AddHours(-1), AlertLevel.Critical, "kill", "Trading stopped", "KILL SWITCH (automatic): loss stop.", "Paper session"));
        string status = Status();
        Assert.Contains("FAIL  Alerts         2 in the last day; the last: 17:00 Trading stopped: KILL SWITCH (automatic): loss stop. (qa alerts)", status, StringComparison.Ordinal);
        Assert.Contains("Read today's alerts ('qa alerts'): trading stopped at 17:00.", status, StringComparison.Ordinal);

        _time.Advance(TimeSpan.FromDays(2));
        Assert.DoesNotContain("Alerts", Status(), StringComparison.Ordinal); // a day later they are history
    }

    [Fact]
    public void Backup_NeedsAFolder_ThenMakesAndVerifiesOne_AndStatusSaysWhen()
    {
        Assert.Contains("todo  Backup         none set up", Status(), StringComparison.Ordinal);
        Assert.Contains("No backup folder: set one with 'qa backup setup --to <folder>'", Qa(["backup", .. Paths]).Error, StringComparison.Ordinal);

        (int code, string output, string error) = Qa("backup", "setup", "--to", Backups, "--keep", "3", "--config-dir", Config);
        Assert.True(code == 0, error);
        Assert.Contains($"Backups go to {Path.GetFullPath(Backups)} (the newest 3 stay; automatic on: after each Paper session and evening import).", output, StringComparison.Ordinal);
        Assert.Contains("warn  Backup         none made yet", Status(), StringComparison.Ordinal);

        (code, output, error) = Qa(["backup", .. Paths]);
        Assert.True(code == 0, error);
        Assert.Contains("Backup made and checked: ", output, StringComparison.Ordinal);
        Assert.Contains("  audit: 1 file(s), ", output, StringComparison.Ordinal);
        Assert.Contains("  store: nothing yet (no store)", output, StringComparison.Ordinal);
        string folder = Assert.Single(Trading.Backup.Backups.List(Backups));
        Assert.Empty(Directory.EnumerateFiles(folder, "auth.json", SearchOption.AllDirectories)); // never the login state
        Assert.Contains("ok    Backup         last 2026-10-05 18:00, checked to ", Status(), StringComparison.Ordinal);

        (code, output, _) = Qa("backup", "verify", "--config-dir", Config);
        Assert.Equal(0, code);
        Assert.Contains("The backup is intact.", output, StringComparison.Ordinal);
        File.AppendAllText(Path.Combine(folder, "paper", "book.json"), "tampered");
        (code, output, _) = Qa("backup", "verify", "--path", folder);
        Assert.Equal(1, code);
        Assert.Contains("paper: FAILED: paper/book.json has changed size", output, StringComparison.Ordinal);

        _time.Advance(TimeSpan.FromDays(4));
        Assert.Contains("over 3 days old, run qa backup", Status(), StringComparison.Ordinal);

        // A broken backup setting is a warning with advice: it never holds back a session.
        File.WriteAllText(Path.Combine(Config, "backup.json"), "{ not json");
        string status = Status();
        Assert.Contains("warn  Backup", status, StringComparison.Ordinal);
        Assert.Contains("Fix the backup setting:", status, StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_WaitsForARunningSession()
    {
        Assert.Equal(0, Qa("backup", "setup", "--to", Backups, "--config-dir", Config).Code);
        using (SessionLock.Acquire(State, _time))
        {
            Assert.Contains("A session is running", Qa(["backup", .. Paths]).Error, StringComparison.Ordinal);
        }

        Assert.Equal(0, Qa(["backup", .. Paths]).Code);
    }

    [Fact]
    public void TheWindowsNotification_StartsAFixedScript_WithTheAlertOnlyInTheEnvironment()
    {
        var alert = new Alert(_time.GetUtcNow(), AlertLevel.Critical, "kill", "Trading stopped", "KILL SWITCH ($(Remove-Item C:\\) \"quoted\" <xml> & more) " + new string('x', 400), "Paper session");
        System.Diagnostics.ProcessStartInfo info = WindowsToastNotifier.StartInfo(alert);

        Assert.Equal("powershell.exe", info.FileName); // Windows PowerShell 5.1: PowerShell 7 can't load WinRT types
        Assert.Equal(["-NoProfile", "-NonInteractive", "-EncodedCommand"], info.ArgumentList.Take(3));
        Assert.Equal(WindowsToastNotifier.Script, Encoding.Unicode.GetString(Convert.FromBase64String(info.ArgumentList[3])));
        Assert.DoesNotContain(info.ArgumentList, a => a.Contains("KILL", StringComparison.Ordinal));
        Assert.Equal("QuantAnalyst: Trading stopped", info.Environment["QA_ALERT_TITLE"]);
        Assert.Equal(WindowsToastNotifier.MaxText, info.Environment["QA_ALERT_TEXT"]!.Length);
        Assert.Equal(WindowsToastNotifier.AppId, info.Environment["QA_ALERT_APPID"]);
        Assert.Contains("[System.Security.SecurityElement]::Escape($env:QA_ALERT_TEXT)", WindowsToastNotifier.Script, StringComparison.Ordinal);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
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

        throw new InvalidOperationException("repository root not found");
    }

    private sealed class Recording(List<Alert> shown) : IAlertNotifier
    {
        public void Show(Alert alert) => shown.Add(alert);
    }
}
