using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Alerts;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Cli.Commands;

/// <summary>
/// Plan 25: an alert as a Windows notification, through Windows PowerShell 5.1's WinRT toast API with PowerShell's own
/// app id (no module, nothing to install; the pattern of github.com/GitHub30/toast-notification-examples). PowerShell 7
/// can't load WinRT types, so this starts <c>powershell.exe</c>. The title and text go in as environment variables and
/// are XML-escaped inside the script: they can never change it. Started and forgotten; a notification that can't be
/// shown changes nothing else.
/// </summary>
internal sealed class WindowsToastNotifier : IAlertNotifier
{
    internal const string AppId = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

    /// <summary>A notification shows a few lines; the full text is in <c>qa alerts</c>.</summary>
    internal const int MaxText = 300;

    internal const string Script = """
        $ErrorActionPreference = 'Stop'
        [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
        $title = [System.Security.SecurityElement]::Escape($env:QA_ALERT_TITLE)
        $text = [System.Security.SecurityElement]::Escape($env:QA_ALERT_TEXT)
        $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
        $xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>$title</text><text>$text</text></binding></visual></toast>")
        $toast = New-Object Windows.UI.Notifications.ToastNotification $xml
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($env:QA_ALERT_APPID).Show($toast)
        """;

    public void Show(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        try
        {
            using Process? started = Process.Start(StartInfo(alert));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // No powershell.exe (or not allowed to start it): the alert is still on the console and in state/alerts.jsonl.
        }
    }

    /// <summary>How the notification is started: a fixed script, the alert only in the environment.</summary>
    internal static ProcessStartInfo StartInfo(Alert alert)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
        {
            info.ArgumentList.Add(a);
        }

        string text = alert.Text.Length <= MaxText ? alert.Text : alert.Text[..(MaxText - 1)] + "…";
        info.Environment["QA_ALERT_TITLE"] = $"QuantAnalyst: {alert.Title}";
        info.Environment["QA_ALERT_TEXT"] = text;
        info.Environment["QA_ALERT_APPID"] = AppId;
        return info;
    }
}

/// <summary>Plan 25: the alerter a command raises its alerts through.</summary>
internal static class Alerting
{
    /// <param name="configDir">--config-dir as given (null: ./config or next to qa); without alerts.json the defaults apply.</param>
    public static Alerter Create(AvanzaCliServices services, string? configDir, string stateDir, TextWriter output, Func<string, string> redact, string source)
    {
        ArgumentNullException.ThrowIfNull(services);
        AlertSettings settings;
        try
        {
            settings = AlertSettings.Load(TradingCommands.ResolveConfigDir(configDir));
        }
        catch (Exception ex) when (ex is TradingConfigException or ArgumentException or IOException)
        {
            output.WriteLine($"warning: {ex.Message} Alerts use their defaults (notifications on).");
            settings = AlertSettings.Default;
        }

        return new Alerter(new AlertLog(stateDir), services.Notifier(), settings, redact, services.Time, output, source);
    }
}

/// <summary><c>qa alerts</c> (plan 25): the alerts so far, a test alert, and the settings.</summary>
internal static class AlertCommands
{
    public static Command Create(AvanzaCliServices services)
    {
        var days = new Option<int>("--days") { Description = "How many days back (default 7)", DefaultValueFactory = _ => 7 };
        var stateDir = StateDirOption();
        var command = new Command("alerts", "The alerts so far (kill switch, halts, failed sessions, decisions, imports and backups, day summaries), newest last. Offline.");
        command.Options.Add(days);
        command.Options.Add(stateDir);
        command.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            int n = parse.GetValue(days);
            if (n is < 1 or > 3650)
            {
                throw new ArgumentException("--days must be between 1 and 3650.");
            }

            var log = new AlertLog(parse.GetValue(stateDir)!);
            IReadOnlyList<Alert> alerts = log.Since(services.Time.GetUtcNow().AddDays(-n));
            w.WriteLine(alerts.Count == 0
                ? string.Create(CultureInfo.InvariantCulture, $"No alerts in the last {n} day(s) ({log.Path}).")
                : string.Create(CultureInfo.InvariantCulture, $"{alerts.Count} alert(s) in the last {n} day(s):"));
            foreach (Alert a in alerts)
            {
                w.WriteLine($"  {a.Describe()} [{a.Source}]");
            }

            return 0;
        }));
        command.Subcommands.Add(TestCommand(services));
        command.Subcommands.Add(SetCommand());
        return command;
    }

    private static Option<string> StateDirOption() =>
        new("--state-dir") { Description = "State folder (alerts.jsonl)", DefaultValueFactory = _ => "state" };

    private static Command TestCommand(AvanzaCliServices services)
    {
        var configDir = TradingCommands.ConfigDirOption();
        var stateDir = StateDirOption();
        var command = new Command("test", "Raises a test alert: it shows as a Windows notification (when they are on) and in 'qa alerts'.");
        command.Options.Add(configDir);
        command.Options.Add(stateDir);
        command.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            Alerter alerts = Alerting.Create(services, parse.GetValue(configDir), parse.GetValue(stateDir)!, w, text => text, "qa alerts test");
            alerts.Raise(AlertLevel.Info, "test", "Test alert", string.Create(CultureInfo.InvariantCulture,
                $"Test alert at {MarketTime.ToStockholm(services.Time.GetUtcNow()):HH:mm}: alerts reach you."));
            w.WriteLine(alerts.Settings.Notifications
                ? OperatingSystem.IsWindows()
                    ? "A Windows notification should show now (from Windows PowerShell). None? Check Windows' notification settings: Do not disturb hides them."
                    : "Windows notifications show on Windows only; here the alert is on the console and in 'qa alerts'."
                : "Notifications are off ('qa alerts set --notifications on'): the alert is on the console and in 'qa alerts' only.");
            return 0;
        }));
        return command;
    }

    private static Command SetCommand()
    {
        var configDir = TradingCommands.ConfigDirOption();
        var notifications = new Option<string?>("--notifications") { Description = "on|off: show alerts as Windows notifications" };
        var daySummary = new Option<string?>("--day-summary") { Description = "on|off: an info alert with each Paper day's result" };
        var command = new Command("set", "Changes the alert settings (config/alerts.json); without options shows them.");
        foreach (Option o in new Option[] { configDir, notifications, daySummary })
        {
            command.Options.Add(o);
        }

        command.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            string dir = TradingCommands.ResolveConfigDir(parse.GetValue(configDir));
            AlertSettings settings = AlertSettings.Load(dir);
            string? n = parse.GetValue(notifications), d = parse.GetValue(daySummary);
            if (n is not null || d is not null)
            {
                settings = new AlertSettings(n is null ? settings.Notifications : OnOff(n, "--notifications"), d is null ? settings.DaySummary : OnOff(d, "--day-summary"));
                settings.Save(dir);
                w.WriteLine($"Saved to {Path.Combine(dir, AlertSettings.FileName)}.");
            }

            w.WriteLine($"Notifications: {(settings.Notifications ? "on" : "off")}; day summary: {(settings.DaySummary ? "on" : "off")}.");
            return 0;
        }));
        return command;
    }

    internal static bool OnOff(string value, string option) => value.Trim().ToUpperInvariant() switch
    {
        "ON" or "TRUE" or "YES" => true,
        "OFF" or "FALSE" or "NO" => false,
        _ => throw new ArgumentException($"{option}: '{value}' is not on or off."),
    };
}
