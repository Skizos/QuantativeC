using System.CommandLine;
using System.Globalization;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Alerts;
using QuantAnalyst.Trading.Backup;
using QuantAnalyst.Trading.Kill;

namespace QuantAnalyst.Cli.Commands;

/// <summary>Where the backed-up things are (plan 25): the same defaults as the commands that write them.</summary>
/// <param name="Ledger">The trial ledger; null when it can't be found (outside the repository): left out.</param>
internal sealed record BackupPaths(string ConfigDir, string StateDir, string AuditDir, string Store, string? Ledger, string PromotionDir)
{
    /// <summary>The fixed list: the audit, the Paper book, the config, the promotion records, the ledger, the store. Nothing else.</summary>
    public IReadOnlyList<BackupSource> Sources() =>
    [
        new("audit", AuditDir, BackupKind.Folder),
        new("paper", Path.Combine(StateDir, TradingCommands.PaperDirName), BackupKind.Folder),
        new("config", ConfigDir, BackupKind.Folder),
        new("promotion", PromotionDir, BackupKind.Folder),
        .. Ledger is null ? Array.Empty<BackupSource>() : [new BackupSource("ledger", Ledger, BackupKind.File)],
        new("store", Store, BackupKind.Store),
    ];

    /// <param name="ledger">--ledger as given; null: the repository's, when there is one.</param>
    public static BackupPaths Of(string? configDir, string stateDir, string auditDir, string store, string? ledger, string promotionDir)
    {
        string? ledgerPath;
        try
        {
            ledgerPath = BacktestCommands.ResolveLedger(ledger);
        }
        catch (ArgumentException)
        {
            ledgerPath = null;
        }

        return new BackupPaths(TradingCommands.ResolveConfigDir(configDir), stateDir, auditDir, store, ledgerPath, promotionDir);
    }
}

/// <summary><c>qa backup</c> (plan 25): a checked copy of the audit, the Paper book, the config, the ledger and the store.</summary>
internal static class BackupCommands
{
    public static Command Create(AvanzaCliServices services)
    {
        var o = new PathOptions();
        var to = new Option<string?>("--to") { Description = "The backup folder (default: the one set with 'qa backup setup')" };
        var command = new Command(
            "backup",
            "Makes a backup now: the audit log, the Paper book, the config, the promotion records, the trial ledger and the price store, each copy checked. Never the login state or secrets.");
        o.AddTo(command);
        command.Options.Add(to);
        command.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            BackupPaths paths = o.Paths(parse);
            BackupSettings? settings = BackupSettings.Load(paths.ConfigDir);
            string destination = parse.GetValue(to) ?? settings?.To
                ?? throw new ArgumentException("No backup folder: set one with 'qa backup setup --to <folder>' (OneDrive or another disk), or pass --to.");
            if (SessionLock.Holder(paths.StateDir) is { } holder)
            {
                throw new ArgumentException($"A session is running ({holder}): it backs up when it ends (with automatic on). Run 'qa backup' after it.");
            }

            BackupResult result = Backups.Run(paths.Sources(), destination, settings?.Keep ?? BackupSettings.DefaultKeep, services.Time);
            Report(result, w);
            return result.Manifest.Complete ? 0 : 1;
        }));
        command.Subcommands.Add(Setup());
        command.Subcommands.Add(Verify(o));
        return command;
    }

    /// <summary>
    /// The automatic backup after a Paper session or an evening import: silent unless one is set up with automatic on; a
    /// failure is a warning alert, never a failed session.
    /// </summary>
    /// <param name="ownLock">True when this process holds the session lock (the Paper session backs up before it lets go).</param>
    public static void After(string what, BackupPaths paths, TextWriter output, Alerter? alerts, TimeProvider time, bool ownLock)
    {
        BackupSettings? settings;
        try
        {
            settings = BackupSettings.Load(paths.ConfigDir);
        }
        catch (Trading.Risk.TradingConfigException ex)
        {
            output.WriteLine($"Backup skipped: {ex.Message}");
            alerts?.Raise(AlertLevel.Warning, "backup-failed", "Backup skipped", $"The backup after the {what} was skipped: {ex.Message}");
            return;
        }

        if (settings is not { Automatic: true })
        {
            return;
        }

        if (!ownLock && SessionLock.Holder(paths.StateDir) is not null)
        {
            output.WriteLine("Backup skipped: a session is running; it makes one when it ends.");
            return;
        }

        try
        {
            BackupResult result = Backups.Run(paths.Sources(), settings.To, settings.Keep, time);
            Report(result, output);
            if (!result.Manifest.Complete)
            {
                alerts?.Raise(AlertLevel.Warning, "backup-failed", "Backup incomplete",
                    $"The backup after the {what} is incomplete: {string.Join("; ", result.Manifest.Items.Where(i => !i.Ok).Select(i => $"{i.Name}: {i.Check}"))}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            output.WriteLine($"Backup failed: {ex.Message}");
            alerts?.Raise(AlertLevel.Warning, "backup-failed", "Backup failed", $"The backup after the {what} to {settings.To} failed: {ex.Message}");
        }
    }

    private static void Report(BackupResult result, TextWriter w)
    {
        w.WriteLine($"Backup {(result.Manifest.Complete ? "made and checked" : "INCOMPLETE")}: {result.Folder}");
        foreach (BackupItem item in result.Manifest.Items)
        {
            w.WriteLine($"  {item.Name}: {(item.Ok ? item.Check : "NOT COPIED: " + item.Check)}");
        }

        foreach (string old in result.Removed)
        {
            w.WriteLine($"  removed the old backup {Path.GetFileName(old)}");
        }
    }

    private static Command Setup()
    {
        var configDir = TradingCommands.ConfigDirOption();
        var to = new Option<string>("--to") { Description = "The backup folder: OneDrive or another disk is best", Required = true };
        var keep = new Option<int>("--keep") { Description = "How many backups stay (default 7)", DefaultValueFactory = _ => BackupSettings.DefaultKeep };
        var automatic = new Option<string>("--automatic") { Description = "on|off: a backup after each Paper session and evening import (default on)", DefaultValueFactory = _ => "on" };
        var command = new Command("setup", "Sets the backup folder (config/backup.json).");
        foreach (Option opt in new Option[] { configDir, to, keep, automatic })
        {
            command.Options.Add(opt);
        }

        command.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            string dir = TradingCommands.ResolveConfigDir(parse.GetValue(configDir));
            int n = parse.GetValue(keep);
            if (n is < 1 or > 365)
            {
                throw new ArgumentException("--keep must be 1 to 365.");
            }

            string folder = Path.GetFullPath(parse.GetValue(to)!);
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".qa-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            var settings = new BackupSettings(folder, n, AlertCommands.OnOff(parse.GetValue(automatic)!, "--automatic"));
            settings.Save(dir);
            w.WriteLine($"Backups go to {folder} (the newest {n} stay; automatic {(settings.Automatic ? "on: after each Paper session and evening import" : "off")}). Saved to {Path.Combine(dir, BackupSettings.FileName)}.");
            if (string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(Path.GetFullPath(dir)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && !folder.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
            {
                w.WriteLine("Note: that folder is on the same disk as the workspace, so a broken disk takes both. OneDrive or another disk is safer.");
            }

            w.WriteLine("Make the first one now with 'qa backup'.");
            return 0;
        }));
        return command;
    }

    private static Command Verify(PathOptions o)
    {
        var path = new Option<string?>("--path") { Description = "A backup folder (default: the newest in the configured backup folder)" };
        var command = new Command("verify", "Checks a backup again: every file against its SHA-256, the audit and ledger chains, the store's rows.");
        command.Options.Add(path);
        command.Options.Add(o.ConfigDir);
        command.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            string folder = parse.GetValue(path)
                ?? (BackupSettings.Load(TradingCommands.ResolveConfigDir(parse.GetValue(o.ConfigDir))) is { } s
                    ? Backups.Latest(s.To) ?? throw new ArgumentException($"No backup in {s.To} yet: make one with 'qa backup'.")
                    : throw new ArgumentException("No backup folder set up: pass --path, or set one with 'qa backup setup --to <folder>'."));
            BackupCheck check = Backups.Verify(folder);
            BackupManifest? manifest = BackupManifest.Load(folder);
            w.WriteLine(manifest is null
                ? folder
                : string.Create(CultureInfo.InvariantCulture, $"{folder} (made {MarketTime.ToStockholm(manifest.CreatedUtc):yyyy-MM-dd HH:mm} Stockholm)"));
            foreach (string line in check.Lines)
            {
                w.WriteLine("  " + line);
            }

            w.WriteLine(check.Ok ? "The backup is intact." : "The backup is NOT intact: make a new one ('qa backup') and keep this one aside.");
            return check.Ok ? 0 : 1;
        }));
        return command;
    }

    /// <summary>The source options, with the same defaults as the commands that write each thing.</summary>
    private sealed class PathOptions
    {
        public Option<string?> ConfigDir { get; } = TradingCommands.ConfigDirOption();

        public Option<string> StateDir { get; } = new("--state-dir") { Description = "State folder (the Paper book is in state/paper)", DefaultValueFactory = _ => "state" };

        public Option<string> AuditDir { get; } = new("--audit-dir") { Description = "Audit folder", DefaultValueFactory = _ => TradingCommands.DefaultAuditDir };

        public Option<string> Store { get; } = DataCommands.StoreOption();

        public Option<string?> Ledger { get; } = new("--ledger") { Description = $"Trial ledger (default: {TrialLedger.DefaultPath} in the repository)" };

        public Option<string> PromotionDir { get; } = new("--promotion-dir") { Description = "Promotion records folder", DefaultValueFactory = _ => "promotion" };

        public void AddTo(Command command)
        {
            foreach (Option opt in new Option[] { ConfigDir, StateDir, AuditDir, Store, Ledger, PromotionDir })
            {
                command.Options.Add(opt);
            }
        }

        public BackupPaths Paths(ParseResult parse) => BackupPaths.Of(
            parse.GetValue(ConfigDir), parse.GetValue(StateDir)!, parse.GetValue(AuditDir)!, parse.GetValue(Store)!, parse.GetValue(Ledger), parse.GetValue(PromotionDir)!);
    }
}
