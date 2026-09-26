using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantAnalyst.Analytics.Statistics;

namespace QuantAnalyst.Analytics.Backtesting;

public enum TrialStatus
{
    /// <summary>The backtest ran; metrics are recorded.</summary>
    Ok,

    /// <summary>The leakage check found look-ahead; no metrics are trusted.</summary>
    RejectedLeakage,

    /// <summary>The requested range touches the locked final holdout; nothing ran.</summary>
    RejectedHoldout,

    /// <summary>The run failed for another reason (data, engine).</summary>
    Failed,
}

/// <summary>Headline metrics of one trial. Sharpe values are per period unless named annualised.</summary>
public sealed record TrialMetrics(
    int Observations,
    double SharpePerPeriod,
    double SharpeAnnualised,
    double Skewness,
    double Kurtosis,
    double TotalReturn,
    double Cagr,
    double VolatilityAnnualised,
    double MaxDrawdown,
    double Turnover,
    double TotalCosts,
    double Psr0,
    double? Dsr,
    int DsrTrials,
    double? Pbo);

/// <summary>One evaluation in the <see cref="TrialLedger"/>. Everything needed to judge (and repeat) it.</summary>
public sealed record TrialRecord
{
    public long Sequence { get; init; }

    public required string Id { get; init; }

    public required DateTimeOffset RecordedAtUtc { get; init; }

    /// <summary>Who ran it: the QA_RUNNER environment variable (e.g. "claude", "owner"), else "unspecified".</summary>
    public required string Runner { get; init; }

    public string? GitCommit { get; init; }

    /// <summary>Trials with the same study key are one family for deflation (strategy, universe, dates, data source).</summary>
    public required string Study { get; init; }

    public required string Strategy { get; init; }

    public required IReadOnlyDictionary<string, string> Parameters { get; init; }

    public required IReadOnlyList<string> Universe { get; init; }

    public required string DataSource { get; init; }

    public required bool PointInTime { get; init; }

    public required bool SurvivorshipFree { get; init; }

    public required DateOnly From { get; init; }

    public required DateOnly To { get; init; }

    public required ulong Seed { get; init; }

    public required string CostModel { get; init; }

    public required bool CostsVerified { get; init; }

    /// <summary>True when the run used bars inside the final holdout (only possible after the owner unlocked it).</summary>
    public required bool HoldoutTouched { get; init; }

    public required TrialStatus Status { get; init; }

    public string? Note { get; init; }

    public TrialMetrics? Metrics { get; init; }

    public string PrevHash { get; init; } = string.Empty;
}

/// <summary>Result of <see cref="TrialLedger.Verify"/>.</summary>
public sealed record LedgerVerification(bool Valid, int Records, string? Problem);

/// <summary>
/// Append-only log of <b>every</b> backtest evaluation (CLAUDE.md "TrialLedger logs EVERY evaluation, including yours").
/// One JSON line per trial: <c>{"hash": h, "record": {…, "prevHash": h₋₁}}</c> with h = SHA-256(h₋₁ + "\n" + the exact
/// UTF-8 text of "record"). Editing, deleting or reordering any line breaks the chain, which <see cref="Verify"/> reports.
/// The file is committed (research/trial-ledger.jsonl) so every run, the owner's and Claude's, is visible.
/// </summary>
public sealed class TrialLedger(string path)
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
    public const string DefaultPath = "research/trial-ledger.jsonl";

    /// <summary>How long a reader or writer waits for another process's lock on the file before giving up.</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    public string Path { get; } = path;

    /// <summary>Appends a record (sequence, prevHash and id are assigned here) under an exclusive file lock.</summary>
    public TrialRecord Append(TrialRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
        }

        return WithLockRetry(() =>
        {
            using var stream = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            (long lastSequence, string lastHash) = Tail(stream);
            TrialRecord stamped = record with
            {
                Sequence = lastSequence + 1,
                Id = string.Create(CultureInfo.InvariantCulture, $"T{lastSequence + 1:000000}"),
                PrevHash = lastHash,
            };
            string recordJson = JsonSerializer.Serialize(stamped, LedgerJsonContext.Default.TrialRecord);
            string hash = Hash(lastHash, recordJson);
            string line = $"{{\"hash\":\"{hash}\",\"record\":{recordJson}}}\n";
            stream.Seek(0, SeekOrigin.End);
            stream.Write(Encoding.UTF8.GetBytes(line));
            stream.Flush(flushToDisk: true);
            return stamped;
        });
    }

    public IReadOnlyList<TrialRecord> ReadAll()
    {
        var records = new List<TrialRecord>();
        foreach (string line in ReadLines().Where(l => l.Length > 0))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            records.Add(doc.RootElement.GetProperty("record").Deserialize(LedgerJsonContext.Default.TrialRecord)
                        ?? throw new InvalidDataException("Empty ledger record."));
        }

        return records;
    }

    /// <summary>Checks every line's hash, the chain of prevHash values and the sequence numbers.</summary>
    public LedgerVerification Verify()
    {
        string previous = GenesisHash;
        int n = 0;
        foreach (string line in ReadLines())
        {
            n++;
            if (line.Length == 0)
            {
                return new LedgerVerification(false, n - 1, $"line {n} is empty");
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                string recordText = doc.RootElement.GetProperty("record").GetRawText();
                string stored = doc.RootElement.GetProperty("hash").GetString() ?? string.Empty;
                TrialRecord record = JsonSerializer.Deserialize(recordText, LedgerJsonContext.Default.TrialRecord)!;
                if (record.PrevHash != previous)
                {
                    return new LedgerVerification(false, n - 1, $"line {n}: prevHash does not match line {n - 1}");
                }

                if (record.Sequence != n)
                {
                    return new LedgerVerification(false, n - 1, $"line {n}: sequence {record.Sequence}, expected {n}");
                }

                if (Hash(previous, recordText) != stored)
                {
                    return new LedgerVerification(false, n - 1, $"line {n}: the record does not match its hash (edited?)");
                }

                previous = stored;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return new LedgerVerification(false, n - 1, $"line {n} is not a ledger record ({ex.GetType().Name})");
            }
        }

        return new LedgerVerification(true, n, null);
    }

    /// <summary>Number of completed trials in a study and the standard deviation of their per-period Sharpe ratios.</summary>
    public (int Trials, double SharpeStd) StudyStats(string study)
    {
        double[] sharpes = StudySharpes(study);
        return (sharpes.Length, sharpes.Length < 2 ? double.NaN : StdDev(sharpes));
    }

    /// <summary>Per-period Sharpe ratios of the completed trials of a study, in ledger order.</summary>
    public double[] StudySharpes(string study) =>
        [.. ReadAll()
            .Where(r => r.Study == study && r.Status == TrialStatus.Ok && r.Metrics is { } m && double.IsFinite(m.SharpePerPeriod))
            .Select(r => r.Metrics!.SharpePerPeriod)];

    /// <summary>
    /// True when the file is locked by another writer or reader: Windows ERROR_SHARING_VIOLATION/ERROR_LOCK_VIOLATION,
    /// Unix EWOULDBLOCK from .NET's advisory lock (11 on Linux, 35 on macOS).
    /// </summary>
    internal static bool IsLockContention(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return OperatingSystem.IsWindows() ? code is 32 or 33 : code is 11 or 35;
    }

    internal static string Hash(string previousHash, string recordJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(previousHash + "\n" + recordJson)));

    internal static double StdDev(IReadOnlyList<double> values)
    {
        double mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }

    // A missing file is an empty ledger.
    private string[] ReadLines() => WithLockRetry(() => File.Exists(Path) ? File.ReadAllLines(Path) : []);

    /// <summary>Retries while another process holds the file (appends fsync, so a busy ledger can take a while).</summary>
    private static T WithLockRetry<T>(Func<T> action)
    {
        long deadline = Environment.TickCount64 + (long)LockTimeout.TotalMilliseconds;
        while (true)
        {
            try
            {
                return action();
            }
            catch (IOException ex) when (IsLockContention(ex) && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(Random.Shared.Next(5, 40));
            }
        }
    }

    private static (long Sequence, string Hash) Tail(FileStream stream)
    {
        if (stream.Length == 0)
        {
            return (0, GenesisHash);
        }

        // Read the last line (ledger lines are small; read backwards in 64 KB chunks).
        long end = stream.Length;
        var buffer = new List<byte>();
        long position = end;
        bool started = false;
        while (position > 0)
        {
            int chunk = (int)Math.Min(65536, position);
            position -= chunk;
            byte[] bytes = new byte[chunk];
            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(bytes);
            for (int i = chunk - 1; i >= 0; i--)
            {
                if (bytes[i] == (byte)'\n')
                {
                    if (started)
                    {
                        position = -1;
                        break;
                    }

                    continue;
                }

                started = true;
                buffer.Add(bytes[i]);
            }

            if (position < 0)
            {
                break;
            }
        }

        buffer.Reverse();
        using JsonDocument doc = JsonDocument.Parse(buffer.ToArray());
        string hash = doc.RootElement.GetProperty("hash").GetString() ?? throw new InvalidDataException("Ledger line without hash.");
        long sequence = doc.RootElement.GetProperty("record").GetProperty("sequence").GetInt64();
        return (sequence, hash);
    }
}

/// <summary>Stable property order and naming for ledger lines (the hash covers the exact text).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TrialRecord))]
internal sealed partial class LedgerJsonContext : JsonSerializerContext;
