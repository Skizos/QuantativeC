using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Backup;

/// <summary>What a backup item is (plan 25).</summary>
public enum BackupKind
{
    /// <summary>A folder, copied file by file.</summary>
    Folder,

    /// <summary>One file.</summary>
    File,

    /// <summary>The DuckDB price store, copied by DuckDB itself.</summary>
    Store,
}

/// <summary>One thing the backup copies; <paramref name="Name"/> is its folder in the backup, e.g. "audit".</summary>
public sealed record BackupSource(string Name, string Path, BackupKind Kind);

/// <summary>The owner's backup settings, <c>config/backup.json</c> (plan 25).</summary>
/// <param name="To">The folder backups go to (each in a folder of its own).</param>
/// <param name="Keep">How many backups stay; older ones are removed.</param>
/// <param name="Automatic">A backup after each Paper session and each evening import.</param>
public sealed record BackupSettings(string To, int Keep, bool Automatic)
{
    public const string FileName = "backup.json";
    public const string Format = "qa-backup/1";
    public const int DefaultKeep = 7;

    /// <summary>The settings, or null when no backup is set up.</summary>
    public static BackupSettings? Load(string configDirectory)
    {
        ArgumentNullException.ThrowIfNull(configDirectory);
        string path = System.IO.Path.Combine(configDirectory, FileName);
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
            JsonElement r = doc.RootElement;
            if (r.GetProperty("format").GetString() != Format)
            {
                throw new TradingConfigException($"{path}: format must be {Format}.");
            }

            string to = r.GetProperty("to").GetString() ?? string.Empty;
            int keep = r.TryGetProperty("keep", out JsonElement k) ? k.GetInt32() : DefaultKeep;
            bool automatic = !r.TryGetProperty("automatic", out JsonElement a) || a.GetBoolean();
            return to.Length == 0 || keep is < 1 or > 365
                ? throw new TradingConfigException($"{path}: 'to' must name a folder and 'keep' must be 1 to 365.")
                : new BackupSettings(to, keep, automatic);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new TradingConfigException($"{path} is not a valid backup config: {ex.Message}", ex);
        }
    }

    public void Save(string configDirectory)
    {
        ArgumentNullException.ThrowIfNull(configDirectory);
        var json = new JsonObject { ["format"] = Format, ["to"] = To, ["keep"] = Keep, ["automatic"] = Automatic };
        System.IO.File.WriteAllText(System.IO.Path.Combine(configDirectory, FileName), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}

/// <summary>A copied file: its path in the backup ('/'-separated), size and SHA-256 (hex).</summary>
public sealed record BackupFile(string Path, long Bytes, string Sha256);

/// <summary>One item of a backup and how its check went.</summary>
/// <param name="Rows">The store's row count per table (the store only).</param>
/// <param name="Check">E.g. "12 file(s), audit chain intact (1,204 records)", or why it failed.</param>
public sealed record BackupItem(string Name, BackupKind Kind, bool Ok, IReadOnlyList<BackupFile> Files, IReadOnlyDictionary<string, long>? Rows, string Check);

/// <summary><c>manifest.json</c>: what a backup holds and how it was checked.</summary>
public sealed record BackupManifest(string Format, DateTimeOffset CreatedUtc, IReadOnlyList<BackupItem> Items)
{
    public const string FileName = "manifest.json";
    public const string CurrentFormat = "qa-backup-manifest/1";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public bool Complete => Items.All(i => i.Ok);

    public void Save(string folder) => System.IO.File.WriteAllText(System.IO.Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Json) + "\n");

    /// <summary>The manifest of <paramref name="folder"/>; null when it has none of this program's.</summary>
    public static BackupManifest? Load(string folder)
    {
        string path = System.IO.Path.Combine(folder, FileName);
        try
        {
            return System.IO.File.Exists(path) && JsonSerializer.Deserialize<BackupManifest>(System.IO.File.ReadAllText(path), Json) is { Format: CurrentFormat } m ? m : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What a backup run did.</summary>
/// <param name="Folder">The new backup's folder.</param>
/// <param name="Removed">Older backups removed (keep).</param>
public sealed record BackupResult(string Folder, BackupManifest Manifest, IReadOnlyList<string> Removed);

/// <summary>A backup checked again (<c>qa backup verify</c>).</summary>
public sealed record BackupCheck(string Folder, bool Ok, IReadOnlyList<string> Lines);

/// <summary>
/// Plan 25: copies a fixed list of sources into a new folder <c>qa-backup-yyyy-MM-dd_HHmmss</c> under the destination
/// and checks the copy: every file re-read against its SHA-256, the audit and ledger hash chains verified, the store's
/// row counts compared. The folder is written as <c>….partial</c> and renamed when done; only folders with this
/// program's manifest are ever removed.
/// </summary>
public static class Backups
{
    public const string Prefix = "qa-backup-";
    public const string PartialSuffix = ".partial";

    /// <summary>Makes a backup of <paramref name="sources"/> under <paramref name="destination"/> and keeps the newest <paramref name="keep"/>.</summary>
    public static BackupResult Run(IReadOnlyList<BackupSource> sources, string destination, int keep, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(time);
        if (keep < 1)
        {
            throw new ArgumentException("keep must be at least 1.");
        }

        string root = System.IO.Path.GetFullPath(destination);
        foreach (BackupSource s in sources.Where(s => s.Kind == BackupKind.Folder))
        {
            if (IsInside(root, s.Path))
            {
                throw new ArgumentException($"The backup folder {root} is inside {System.IO.Path.GetFullPath(s.Path)}, which it backs up: choose a folder elsewhere (another disk, or OneDrive).");
            }
        }

        Directory.CreateDirectory(root);
        foreach (string stale in Directory.EnumerateDirectories(root, Prefix + "*" + PartialSuffix))
        {
            Directory.Delete(stale, recursive: true); // a backup that stopped half-way
        }

        DateTimeOffset now = time.GetUtcNow();
        string stamp = MarketTime.ToStockholm(now).ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        string folder = System.IO.Path.Combine(root, Prefix + stamp);
        for (int n = 2; Directory.Exists(folder); n++)
        {
            folder = System.IO.Path.Combine(root, string.Create(CultureInfo.InvariantCulture, $"{Prefix}{stamp}-{n}"));
        }

        string partial = folder + PartialSuffix;
        Directory.CreateDirectory(partial);
        var items = new List<BackupItem>();
        foreach (BackupSource s in sources)
        {
            items.Add(Copy(s, partial));
        }

        var manifest = new BackupManifest(BackupManifest.CurrentFormat, now, items);
        manifest.Save(partial);
        Directory.Move(partial, folder);
        IReadOnlyList<string> removed = manifest.Complete ? Prune(root, keep) : [];
        return new BackupResult(folder, manifest, removed);
    }

    /// <summary>The newest backup under <paramref name="destination"/> (complete or not); null when there is none.</summary>
    public static string? Latest(string destination) => List(destination) is { Count: > 0 } all ? all[0] : null;

    /// <summary>This program's backups under <paramref name="destination"/>, newest first.</summary>
    public static IReadOnlyList<string> List(string destination) =>
        !Directory.Exists(destination)
            ? []
            : [.. Directory.EnumerateDirectories(destination, Prefix + "*")
                .Where(d => !d.EndsWith(PartialSuffix, StringComparison.Ordinal) && BackupManifest.Load(d) is not null)
                .OrderByDescending(d => System.IO.Path.GetFileName(d), StringComparer.Ordinal)];

    /// <summary>Checks a backup again: every file against the manifest, the chains, the store's rows.</summary>
    public static BackupCheck Verify(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (BackupManifest.Load(folder) is not { } manifest)
        {
            return new BackupCheck(folder, false, [$"{folder} has no backup manifest ({BackupManifest.FileName})."]);
        }

        var lines = new List<string>();
        bool ok = true;
        foreach (BackupItem item in manifest.Items)
        {
            if (!item.Ok)
            {
                lines.Add($"{item.Name}: not in this backup ({item.Check})");
                ok = false;
                continue;
            }

            string? problem = item.Files.Select(f => FileProblem(folder, f)).FirstOrDefault(p => p is not null);
            problem ??= ChainProblem(folder, item);
            if (problem is null && item.Kind == BackupKind.Store && item.Rows is { } rows)
            {
                string copy = System.IO.Path.Combine(folder, item.Files[0].Path);
                problem = SameRows(rows, HistoryStore.RowCountsOf(copy));
            }

            lines.Add(problem is null ? $"{item.Name}: ok ({item.Check})" : $"{item.Name}: FAILED: {problem}");
            ok &= problem is null;
        }

        return new BackupCheck(folder, ok, lines);
    }

    private static BackupItem Copy(BackupSource s, string partial)
    {
        string target = System.IO.Path.Combine(partial, s.Name);
        try
        {
            switch (s.Kind)
            {
                case BackupKind.Folder:
                    {
                        if (!Directory.Exists(s.Path))
                        {
                            return new BackupItem(s.Name, s.Kind, true, [], null, "nothing yet (no folder)");
                        }

                        List<BackupFile> files = [.. Directory.EnumerateFiles(s.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                        .Select(f => CopyFile(f, partial, s.Name + "/" + System.IO.Path.GetRelativePath(s.Path, f).Replace('\\', '/')))];
                        return Checked(s, partial, files);
                    }

                case BackupKind.File:
                    {
                        if (!System.IO.File.Exists(s.Path))
                        {
                            return new BackupItem(s.Name, s.Kind, true, [], null, "nothing yet (no file)");
                        }

                        return Checked(s, partial, [CopyFile(s.Path, partial, s.Name + "/" + System.IO.Path.GetFileName(s.Path))]);
                    }

                default:
                    {
                        if (!System.IO.File.Exists(s.Path))
                        {
                            return new BackupItem(s.Name, s.Kind, true, [], null, "nothing yet (no store)");
                        }

                        Directory.CreateDirectory(target);
                        string copy = System.IO.Path.Combine(target, System.IO.Path.GetFileName(s.Path));
                        IReadOnlyDictionary<string, long> rows;
                        using (HistoryStore store = HistoryStore.Open(s.Path))
                        {
                            store.CopyTo(copy);
                            rows = store.RowCounts();
                        }

                        if (SameRows(rows, HistoryStore.RowCountsOf(copy)) is { } problem)
                        {
                            throw new IOException("the copy differs: " + problem);
                        }

                        var file = new BackupFile(s.Name + "/" + System.IO.Path.GetFileName(s.Path), new FileInfo(copy).Length, Sha256(copy));
                        return new BackupItem(s.Name, s.Kind, true, [file], rows, string.Create(CultureInfo.InvariantCulture,
                            $"{rows.Count} table(s), {rows.Values.Sum():N0} rows, the same in the copy"));
                    }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HistoryStoreException or System.Data.Common.DbException)
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            string why = s.Kind == BackupKind.Store && ex is System.Data.Common.DbException or HistoryStoreException
                ? $"the store could not be copied ({ex.Message}); is a session or the app using it?"
                : ex.Message;
            return new BackupItem(s.Name, s.Kind, false, [], null, why);
        }
    }

    /// <summary>A copied folder or file whose chain (audit, ledger) verifies; an <see cref="IOException"/> when it does not.</summary>
    private static BackupItem Checked(BackupSource s, string partial, List<BackupFile> files)
    {
        if (ChainProblem(partial, s.Name, s.Kind, files) is { } broken)
        {
            throw new IOException(broken);
        }

        string chain = (s.Name, s.Kind) switch
        {
            ("audit", BackupKind.Folder) when files.Count > 0 => string.Create(CultureInfo.InvariantCulture,
                $", audit chain intact ({AuditLog.Verify(System.IO.Path.Combine(partial, "audit")).Records:N0} records)"),
            ("ledger", BackupKind.File) when files.Count > 0 => string.Create(CultureInfo.InvariantCulture,
                $", ledger chain intact ({new TrialLedger(System.IO.Path.Combine(partial, files[0].Path)).Verify().Records:N0} trials)"),
            _ => string.Empty,
        };
        return new BackupItem(s.Name, s.Kind, true, files, null,
            string.Create(CultureInfo.InvariantCulture, $"{files.Count} file(s), {files.Sum(f => f.Bytes):N0} bytes{chain}"));
    }

    /// <summary>Copies one file (shared read: a session may still append to it) and re-reads the copy: both hashes must agree.</summary>
    private static BackupFile CopyFile(string source, string partial, string relative)
    {
        string target = System.IO.Path.Combine(partial, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        string sourceHash;
        long bytes;
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            byte[] buffer = new byte[81920];
            int read;
            bytes = 0;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
                bytes += read;
            }

            sourceHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        string copyHash = Sha256(target);
        return copyHash == sourceHash
            ? new BackupFile(relative, bytes, sourceHash)
            : throw new IOException($"{relative}: the copy reads back differently from what was written.");
    }

    private static string Sha256(string path)
    {
        using FileStream f = System.IO.File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// <summary>The audit and the ledger carry hash chains: their copies must verify.</summary>
    private static string? ChainProblem(string folder, string name, BackupKind kind, IReadOnlyList<BackupFile> files) => (name, kind) switch
    {
        ("audit", BackupKind.Folder) when files.Count > 0 => AuditLog.Verify(System.IO.Path.Combine(folder, "audit")) is { Valid: false } v ? $"the audit copy's chain is broken: {v.Problem}" : null,
        ("ledger", BackupKind.File) when files.Count > 0 => new TrialLedger(System.IO.Path.Combine(folder, files[0].Path)).Verify() is { Valid: false } l ? $"the ledger copy's chain is broken: {l.Problem}" : null,
        _ => null,
    };

    private static string? ChainProblem(string folder, BackupItem item) => ChainProblem(folder, item.Name, item.Kind, item.Files);

    private static string? FileProblem(string folder, BackupFile f)
    {
        string path = System.IO.Path.Combine(folder, f.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return !System.IO.File.Exists(path) ? $"{f.Path} is missing"
            : new FileInfo(path).Length != f.Bytes ? $"{f.Path} has changed size"
            : Sha256(path) != f.Sha256 ? $"{f.Path} has changed (SHA-256)"
            : null;
    }

    private static string? SameRows(IReadOnlyDictionary<string, long> expected, IReadOnlyDictionary<string, long> actual)
    {
        foreach ((string table, long rows) in expected)
        {
            if (actual.GetValueOrDefault(table, -1) != rows)
            {
                return string.Create(CultureInfo.InvariantCulture, $"table {table} has {actual.GetValueOrDefault(table, 0)} rows, not {rows}");
            }
        }

        return actual.Keys.FirstOrDefault(t => !expected.ContainsKey(t)) is { } extra ? $"table {extra} is not in the original" : null;
    }

    /// <summary>Removes the backups after the newest <paramref name="keep"/> (only this program's, by their manifest).</summary>
    private static List<string> Prune(string root, int keep)
    {
        var removed = new List<string>();
        foreach (string old in List(root).Skip(keep))
        {
            Directory.Delete(old, recursive: true);
            removed.Add(old);
        }

        return removed;
    }

    private static bool IsInside(string candidate, string folder)
    {
        StringComparison c = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string f = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder)) + System.IO.Path.DirectorySeparatorChar;
        string x = System.IO.Path.TrimEndingDirectorySeparator(candidate) + System.IO.Path.DirectorySeparatorChar;
        return x.StartsWith(f, c);
    }
}
