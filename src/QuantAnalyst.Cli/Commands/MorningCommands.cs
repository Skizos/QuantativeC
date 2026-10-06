using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using QuantAnalyst.Avanza.Credentials;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Alerts;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Cli.Commands;

/// <summary>One Windows scheduled task (plan 26): weekdays at <paramref name="Time"/>, <c>qa.ps1</c> with <paramref name="QaArguments"/>.</summary>
/// <param name="Log">The log file under <c>data\</c> the output goes to.</param>
/// <param name="Visible">True: the task's window shows (the Paper session: you can watch it and press Ctrl+C).</param>
internal sealed record ScheduledTask(string Name, string Time, string QaArguments, string Log, bool Visible);

/// <summary>
/// Plan 26: the morning start. <c>qa morning</c> reminds you (a notification) when a trading day's Paper session has not
/// started; <c>qa schedule</c> sets up the Windows scheduled tasks: that reminder, the evening intraday import, and, only
/// with <c>--unattended</c>, the Paper session itself with the stored TOTP login (your choice: it needs the password and
/// TOTP secret in Windows Credential Manager, 'qa secrets set').
/// </summary>
internal static class MorningCommands
{
    public const string ReminderTask = "QuantAnalyst morning reminder";
    public const string ImportTask = "QuantAnalyst intraday import";
    public const string PaperTask = "QuantAnalyst Paper";

    public static IEnumerable<Command> Create(AvanzaCliServices services)
    {
        yield return Morning(services);
        yield return Schedule();
    }

    private static Command Morning(AvanzaCliServices services)
    {
        var configDir = TradingCommands.ConfigDirOption();
        var stateDir = new Option<string>("--state-dir") { Description = "State folder (session lock, alerts)", DefaultValueFactory = _ => "state" };
        var killFile = new Option<string>("--kill-file") { Description = "The kill flag file", DefaultValueFactory = _ => TradingCommands.DefaultKillFile };
        var store = DataCommands.StoreOption();
        var command = new Command(
            "morning",
            "The morning reminder (scheduled by 'qa schedule'): on a trading day without a running Paper session, a notification to start it before the decision. Offline.");
        foreach (Option o in new Option[] { configDir, stateDir, killFile, store })
        {
            command.Options.Add(o);
        }

        command.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            DateTimeOffset now = services.Time.GetUtcNow();
            string state = parse.GetValue(stateDir)!;
            (TradingSchedule home, IReadOnlyList<TradingSchedule> foreign) = StatusCommand.SessionSchedules(TradingCommands.ResolveConfigDir(parse.GetValue(configDir)), parse.GetValue(store)!);
            (TradingSchedule Schedule, SessionPlan Plan)[] open = [.. new[] { home }.Concat(foreign)
                .Select(s => (Schedule: s, Plan: s.Plan(s.Calendar.LocalDate(now))))
                .Where(x => x.Plan is not null && now < x.Plan.WindowCloseUtc)
                .Select(x => (x.Schedule, x.Plan!))];
            if (open.Length == 0)
            {
                w.WriteLine("No Paper session today (a weekend, a holiday, or the markets have closed): nothing to remind.");
                return 0;
            }

            if (SessionLock.Holder(state) is { } holder)
            {
                w.WriteLine($"A session is running ({holder}): nothing to remind.");
                return 0;
            }

            Alerter alerts = Alerting.Create(services, parse.GetValue(configDir), state, w, text => text, "Morning reminder");
            if (KillSwitch.RecordedKill(parse.GetValue(killFile)!, state) is { } kill)
            {
                alerts.Raise(AlertLevel.Warning, "reminder", "Kill switch still on",
                    $"The kill switch is still on ({kill.Source}: {kill.Reason}): today's Paper session would not start. Check with 'qa kill --status'; clear it with 'qa kill --reset' when it is safe.");
                return 0;
            }

            DateTimeOffset decision = open.Min(x => x.Plan.DecisionUtc);
            DateTimeOffset windowClose = open.Max(x => x.Plan.WindowCloseUtc);
            alerts.Raise(AlertLevel.Info, "reminder", "Start today's Paper session", now < decision
                ? $"Paper has not started today: start it before {Clock(decision)} (.\\qa paper run, or Start on the app's Trading page)."
                : $"Paper has not started today and its decision time ({Clock(decision)}) has passed: a session started now still decides at once, until its window closes at {Clock(windowClose)}.");
            return 0;
        }));
        return command;
    }

    private static Command Schedule()
    {
        var install = new Option<bool>("--install") { Description = "Register the tasks with Windows Task Scheduler (replacing ones of the same name)" };
        var remove = new Option<bool>("--remove") { Description = "Remove this program's scheduled tasks" };
        var unattended = new Option<bool>("--unattended") { Description = "Also start the Paper session itself, logging in with the stored TOTP secret (store it first: qa secrets set)" };
        var reminder = new Option<string?>("--reminder") { Description = "Reminder time, HH:mm (default 08:50; 09:03 with --unattended)" };
        var import = new Option<string>("--import") { Description = "Evening intraday import time, HH:mm (default 18:05)", DefaultValueFactory = _ => "18:05" };
        var start = new Option<string>("--start") { Description = "With --unattended: when the Paper session starts, HH:mm (default 08:50)", DefaultValueFactory = _ => "08:50" };
        var command = new Command(
            "schedule",
            "The Windows scheduled tasks for the morning start (plan 26): the morning reminder, the evening intraday import, and with --unattended the Paper session itself. Without --install it only shows them.");
        foreach (Option o in new Option[] { install, remove, unattended, reminder, import, start })
        {
            command.Options.Add(o);
        }

        command.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            string repo = RepoRoot();
            if (parse.GetValue(remove))
            {
                return Register(w, RemoveScript([ReminderTask, ImportTask, PaperTask]), "Removed this program's scheduled tasks (those that existed).");
            }

            bool alone = parse.GetValue(unattended);
            IReadOnlyList<ScheduledTask> tasks = Tasks(alone, parse.GetValue(reminder), parse.GetValue(import)!, parse.GetValue(start)!);
            foreach (ScheduledTask t in tasks)
            {
                w.WriteLine($"{t.Name}: weekdays at {t.Time}: qa {t.QaArguments} (output in data\\{t.Log})");
            }

            if (alone && parse.GetValue(install) && OperatingSystem.IsWindows())
            {
                var credentials = new WindowsCredentialStore();
                if (!WindowsCredentialStore.Exists(credentials.LoginTarget) || !WindowsCredentialStore.Exists(credentials.TotpTarget))
                {
                    throw new ArgumentException("--unattended needs the Avanza username, password and TOTP secret in Windows Credential Manager: store them first with 'qa secrets set'.");
                }
            }

            string script = Script(repo, tasks);
            if (!parse.GetValue(install))
            {
                w.WriteLine();
                w.WriteLine("Nothing was registered. 'qa schedule --install' registers these tasks; this is what it runs (Windows PowerShell):");
                w.WriteLine(script);
                return 0;
            }

            return Register(w, script, alone
                ? "The Paper session now starts by itself on weekdays (your PC on and you logged in); a failed start is an alert. 'qa schedule --remove' takes the tasks away."
                : "Scheduled. Your PC must be on and you logged in; a task missed while it was off runs when it is on again. 'qa schedule --remove' takes the tasks away.");
        }));
        return command;
    }

    /// <summary>The tasks: the reminder, the evening import, and with <paramref name="unattended"/> the Paper session (the reminder then comes after its start).</summary>
    internal static IReadOnlyList<ScheduledTask> Tasks(bool unattended, string? reminder, string import, string start)
    {
        string remind = Time(reminder ?? (unattended ? "09:03" : "08:50"), "--reminder");
        var tasks = new List<ScheduledTask>
        {
            new(ReminderTask, remind, "morning", "morning.log", Visible: false),
            new(ImportTask, Time(import, "--import"), "intraday import", "intraday-import.log", Visible: false),
        };
        if (unattended)
        {
            string begin = Time(start, "--start");
            if (string.CompareOrdinal(remind, begin) <= 0)
            {
                throw new ArgumentException($"--reminder ({remind}) must come after --start ({begin}): it reminds you only when the session did not start.");
            }

            tasks.Insert(0, new ScheduledTask(PaperTask, begin, "paper run --login totp", "paper-run.log", Visible: true));
        }

        return tasks;
    }

    /// <summary>
    /// The Windows PowerShell that registers <paramref name="tasks"/> (the ScheduledTasks module, in every Windows 10/11):
    /// weekdays, started when available (a run missed while the PC was off comes when it is on again), as you.
    /// </summary>
    internal static string Script(string repo, IReadOnlyList<ScheduledTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (repo.IndexOfAny(['"', '`', '$', '\'']) >= 0)
        {
            throw new ArgumentException($"The repository path {repo} has a quote or '$': move it to a plain path (e.g. C:\\dev\\QuantativeC) to schedule it.");
        }

        var s = new StringBuilder();
        s.AppendLine("$ErrorActionPreference = 'Stop'");
        s.AppendLine(CultureInfo.InvariantCulture, $"$repo = '{repo}'");
        s.AppendLine("$days = 'Monday','Tuesday','Wednesday','Thursday','Friday'");
        s.AppendLine("$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable");
        foreach (ScheduledTask t in tasks)
        {
            string qa = $"& '{repo}\\qa.ps1' {t.QaArguments}";
            string log = $"'{repo}\\data\\{t.Log}'";
            string run = t.Visible ? $"{qa} *>&1 | Tee-Object -Append -FilePath {log}" : $"{qa} *>> {log}";
            string window = t.Visible ? string.Empty : "-WindowStyle Hidden ";
            s.AppendLine(CultureInfo.InvariantCulture, $"# {t.Name}: weekdays at {t.Time}");
            s.AppendLine(CultureInfo.InvariantCulture, $"$action = New-ScheduledTaskAction -Execute 'pwsh.exe' -Argument \"-NoProfile {window}-Command `\"{run}`\"\" -WorkingDirectory $repo");
            s.AppendLine(CultureInfo.InvariantCulture,
                $"Register-ScheduledTask -TaskName '{t.Name}' -Action $action -Trigger (New-ScheduledTaskTrigger -Weekly -DaysOfWeek $days -At '{t.Time}') -Settings $settings -Force | Out-Null");
            s.AppendLine(CultureInfo.InvariantCulture, $"Write-Output 'Scheduled: {t.Name}, weekdays at {t.Time}'");
        }

        return s.ToString();
    }

    internal static string RemoveScript(IEnumerable<string> names) =>
        string.Concat(names.Select(n => $"Unregister-ScheduledTask -TaskName '{n}' -Confirm:$false -ErrorAction SilentlyContinue\n"));

    /// <summary>Runs <paramref name="script"/> in Windows PowerShell (Task Scheduler's own cmdlets) and says <paramref name="done"/>.</summary>
    private static int Register(TextWriter w, string script, string done)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ArgumentException("Scheduled tasks are Windows Task Scheduler's: run this on your Windows PC.");
        }

        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
        {
            info.ArgumentList.Add(a);
        }

        using Process p = Process.Start(info) ?? throw new InvalidOperationException("Windows PowerShell did not start.");
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        w.Write(output);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"Task Scheduler refused ({error.Trim()}). If it says access is denied, run PowerShell as administrator once.");
        }

        w.WriteLine(done);
        return 0;
    }

    private static string Time(string text, string option) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly t)
            ? t.ToString("HH:mm", CultureInfo.InvariantCulture)
            : throw new ArgumentException($"{option}: '{text}' is not a time like 08:50.");

    private static string Clock(DateTimeOffset utc) => MarketTime.ToStockholm(utc).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The repository folder (with qa.ps1 and QuantAnalyst.sln): qa.ps1 runs qa from there.</summary>
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "QuantAnalyst.sln")) && File.Exists(Path.Combine(d.FullName, "qa.ps1")))
            {
                return d.FullName;
            }
        }

        throw new ArgumentException("Not inside the repository: run 'qa schedule' through .\\qa from the repository folder.");
    }
}
