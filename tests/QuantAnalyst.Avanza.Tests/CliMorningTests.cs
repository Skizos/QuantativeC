using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Trading.Alerts;
using QuantAnalyst.Trading.Kill;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>Plan 26: the morning reminder and the scheduled tasks.</summary>
public sealed class CliMorningTests : IDisposable
{
    private static readonly DateTimeOffset Monday0850 = new(2026, 10, 5, 6, 50, 0, TimeSpan.Zero); // 08:50 Stockholm

    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-cli-morning", Guid.NewGuid().ToString("N"));
    private readonly List<Alert> _shown = [];

    public CliMorningTests()
    {
        Directory.CreateDirectory(Config);
        string repo = Path.Combine(RepoRoot(), "config");
        foreach (string f in new[] { "risk-limits.json", "costs.avanza-start.json", "market-calendar.XSTO.2026.json", "market-calendar.XSTO.2027.json" })
        {
            File.Copy(Path.Combine(repo, f), Path.Combine(Config, f));
        }

        File.WriteAllText(Path.Combine(Config, "paper.json"), """{ "format": "qa-paper/1", "costs": "avanza-start", "cash": 5000, "decision_time": "09:10" }""");
        File.WriteAllText(Path.Combine(Config, "universe.json"), """{ "format": "qa-universe/1", "instruments": [] }""");
    }

    private string Config => Path.Combine(_root, "config");

    private string State => Path.Combine(_root, "state");

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

    private (int Code, string Output, string Error) Morning(DateTimeOffset now)
    {
        var services = new AvanzaCliServices((_, _, _, _, _) => throw new InvalidOperationException("offline"), _ => FakeSecrets.Store())
        {
            Time = new FakeTimeProvider(now),
            Notifier = () => new Recording(_shown),
        };
        var output = new StringWriter();
        var error = new StringWriter();
        int code = QaCli.Run(["morning", "--config-dir", Config, "--state-dir", State, "--kill-file", KillFile, "--store", Path.Combine(_root, "q.duckdb")], output, error, services);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void OnATradingMorningWithoutASession_ItReminds_AndOtherwiseItIsQuiet()
    {
        (int code, string output, string error) = Morning(Monday0850);
        Assert.True(code == 0, error);
        Alert reminder = Assert.Single(_shown);
        Assert.Equal((AlertLevel.Info, "reminder", "Start today's Paper session"), (reminder.Level, reminder.Kind, reminder.Title));
        Assert.Equal("Paper has not started today: start it before 09:10 (.\\qa paper run, or Start on the app's Trading page).", reminder.Text);
        Assert.Contains("ALERT: Paper has not started today", output, StringComparison.Ordinal);

        Assert.Contains("decision time (09:10) has passed: a session started now still decides at once, until its window closes at 17:20",
            Morning(Monday0850.AddHours(2)).Output, StringComparison.Ordinal);

        _shown.Clear();
        Assert.Contains("No Paper session today", Morning(new DateTimeOffset(2026, 10, 3, 6, 50, 0, TimeSpan.Zero)).Output, StringComparison.Ordinal); // Saturday
        Assert.Contains("No Paper session today", Morning(new DateTimeOffset(2026, 12, 24, 7, 50, 0, TimeSpan.Zero)).Output, StringComparison.Ordinal); // Christmas Eve
        Assert.Contains("No Paper session today", Morning(Monday0850.AddHours(10)).Output, StringComparison.Ordinal); // after the close
        using (SessionLock.Acquire(State, TimeProvider.System))
        {
            Assert.Contains("A session is running", Morning(Monday0850).Output, StringComparison.Ordinal);
        }

        Assert.Empty(_shown);
    }

    [Fact]
    public void WithTheKillSwitchStillOn_ItWarnsInstead()
    {
        KillSwitch.Request(KillFile, "left over", TimeProvider.System);
        Morning(Monday0850);
        Alert warning = Assert.Single(_shown);
        Assert.Equal((AlertLevel.Warning, "Kill switch still on"), (warning.Level, warning.Title));
        Assert.Contains("today's Paper session would not start", warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTasks_AreTheReminderAndTheImport_AndUnattendedAddsThePaperSessionFirst()
    {
        Assert.Equal(
            [(MorningCommands.ReminderTask, "08:50", "morning"), (MorningCommands.ImportTask, "18:05", "intraday import")],
            MorningCommands.Tasks(false, null, "18:05", "08:50").Select(t => (t.Name, t.Time, t.QaArguments)));

        IReadOnlyList<ScheduledTask> alone = MorningCommands.Tasks(true, null, "18:05", "08:45");
        Assert.Equal((MorningCommands.PaperTask, "08:45", "paper run --login totp", true), (alone[0].Name, alone[0].Time, alone[0].QaArguments, alone[0].Visible));
        Assert.Equal("09:03", alone[1].Time); // the reminder comes after the unattended start: it fires only if that did not start

        Assert.Contains("must come after --start", Assert.Throws<ArgumentException>(() => MorningCommands.Tasks(true, "08:40", "18:05", "08:50")).Message, StringComparison.Ordinal);
        Assert.Contains("is not a time like 08:50", Assert.Throws<ArgumentException>(() => MorningCommands.Tasks(false, "8 o'clock", "18:05", "08:50")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScript_RegistersEachTaskForWeekdays_AndRefusesAPathItCouldNotQuote()
    {
        string script = MorningCommands.Script(@"C:\dev\QuantativeC", MorningCommands.Tasks(true, null, "18:05", "08:50"));

        Assert.Contains("$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable", script, StringComparison.Ordinal);
        Assert.Contains(@"-Argument ""-NoProfile -Command `""& 'C:\dev\QuantativeC\qa.ps1' paper run --login totp *>&1 | Tee-Object -Append -FilePath 'C:\dev\QuantativeC\data\paper-run.log'`""""", script, StringComparison.Ordinal);
        Assert.Contains(@"-NoProfile -WindowStyle Hidden -Command `""& 'C:\dev\QuantativeC\qa.ps1' morning *>> 'C:\dev\QuantativeC\data\morning.log'`""", script, StringComparison.Ordinal);
        Assert.Contains("Register-ScheduledTask -TaskName 'QuantAnalyst intraday import'", script, StringComparison.Ordinal);
        Assert.Equal(3, script.Split("Register-ScheduledTask").Length - 1);
        Assert.Throws<ArgumentException>(() => MorningCommands.Script(@"C:\Users\O'Brien\qa", MorningCommands.Tasks(false, null, "18:05", "08:50")));
        Assert.Equal("Unregister-ScheduledTask -TaskName 'QuantAnalyst Paper' -Confirm:$false -ErrorAction SilentlyContinue\n", MorningCommands.RemoveScript([MorningCommands.PaperTask]));
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
