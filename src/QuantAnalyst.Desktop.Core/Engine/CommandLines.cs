namespace QuantAnalyst.Desktop.Core.Engine;

/// <summary>
/// The exact <c>qa</c> arguments each button runs, with every path from the <see cref="Workspace"/> spelled out, so
/// the app never depends on its current folder. Each is what you would type in the terminal.
/// </summary>
public static class CommandLines
{
    public static IReadOnlyList<string> HistoryImport(Workspace w, string ticker, string login) =>
        ["history", "import", ticker, "--store", w.Store, "--config-dir", w.ConfigDir, "--state-dir", w.StateDir, "--login", login];

    public static IReadOnlyList<string> UniverseAdd(Workspace w, string ticker) =>
        ["universe", "add", ticker, "--config-dir", w.ConfigDir, "--store", w.Store];

    public static IReadOnlyList<string> UniverseRemove(Workspace w, string ticker) =>
        ["universe", "remove", ticker, "--config-dir", w.ConfigDir, "--state-dir", w.StateDir];

    /// <summary>A backtest on the allowlist (no <c>--tickers</c>), logged to the trial ledger like every run.</summary>
    public static IReadOnlyList<string> Backtest(Workspace w, string strategy, IEnumerable<KeyValuePair<string, string>> parameters) =>
        ["backtest", "run", "--strategy", strategy, .. Params(parameters), "--config-dir", w.ConfigDir, "--store", w.Store, "--ledger", w.Ledger];

    public static IReadOnlyList<string> SaveStrategy(Workspace w, string strategy, IEnumerable<KeyValuePair<string, string>> parameters) =>
        ["paper", "strategy", strategy, .. Params(parameters), "--config-dir", w.ConfigDir, "--ledger", w.Ledger];

    /// <summary>The day's Paper session with the saved strategy. Paper only: the app has no other trading mode.</summary>
    public static IReadOnlyList<string> PaperRun(Workspace w, string login) =>
        ["paper", "run", "--config-dir", w.ConfigDir, "--store", w.Store, "--state-dir", w.StateDir, "--audit-dir", w.AuditDir,
         "--kill-file", w.KillFile, "--promotion-dir", w.PromotionDir, "--reports-dir", w.ReportsDir, "--login", login];

    public static IReadOnlyList<string> KillReset(Workspace w, string reason) =>
        ["kill", "--reset", "--reason", reason, "--kill-file", w.KillFile, "--state-dir", w.StateDir, "--audit-dir", w.AuditDir];

    /// <summary>How a command line reads in the terminal (paths shortened to the repository), for the activity log.</summary>
    public static string Display(Workspace w, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(args);
        return "qa " + string.Join(' ', args.Select(a =>
        {
            string shown = a.StartsWith(w.Root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(w.Root, a) : a;
            return shown.Contains(' ', StringComparison.Ordinal) ? $"\"{shown}\"" : shown;
        }));
    }

    private static IEnumerable<string> Params(IEnumerable<KeyValuePair<string, string>> parameters) =>
        parameters.SelectMany(p => new[] { "--param", $"{p.Key}={p.Value}" });
}
