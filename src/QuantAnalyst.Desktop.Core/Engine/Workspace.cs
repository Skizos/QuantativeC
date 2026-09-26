using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Cli.Commands;

namespace QuantAnalyst.Desktop.Core.Engine;

/// <summary>
/// Where the app works: the repository folder the CLI uses too, so both share config\, data\, state\, audit\ and
/// reports\. Every path is absolute and passed to each command explicitly, never through the current directory.
/// </summary>
public sealed class Workspace
{
    public Workspace(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string ConfigDir => At("config");

    public string Store => At(DataCommands.DefaultStore);

    public string StateDir => At(TradingCommands.DefaultStateDir);

    public string AuditDir => At(TradingCommands.DefaultAuditDir);

    public string ReportsDir => At(TradingCommands.DefaultReportsDir);

    public string PromotionDir => At(TradingCommands.DefaultPromotionDir);

    public string KillFile => At(TradingCommands.DefaultKillFile);

    public string Ledger => At(TrialLedger.DefaultPath);

    internal StatusCommand.StatusPaths StatusPaths => new(ConfigDir, Store, StateDir, AuditDir, KillFile, PromotionDir, Ledger);

    /// <summary>The repository above <paramref name="start"/> (the folder with QuantAnalyst.sln), or null.</summary>
    public static Workspace? Find(string start)
    {
        for (DirectoryInfo? d = new(Path.GetFullPath(start)); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "QuantAnalyst.sln")))
            {
                return new Workspace(d.FullName);
            }
        }

        return null;
    }

    private string At(string relative) => Path.GetFullPath(Path.Combine(Root, relative));
}
