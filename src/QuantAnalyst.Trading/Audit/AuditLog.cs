using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Trading.Audit;

/// <summary>Result of <see cref="AuditLog.Verify"/>.</summary>
public sealed record AuditVerification(bool Valid, int Records, int Files, string? Problem);

/// <summary>
/// Append-only, tamper-evident record of every pipeline step (ADR 0003 §2): <c>audit/YYYY-MM-DD.jsonl</c>, one file per
/// Stockholm trading date. Each line is <c>{"hash": h, "record": {seq, atUtc, kind, data, prevHash}}</c> with
/// h = SHA-256(prevHash + "\n" + exact record text). The chain continues across files, so a deleted day is detected too.
/// Values registered as sensitive (full account ids) are replaced by their masked form before anything is written.
/// </summary>
public sealed class AuditLog
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly List<(string Value, string Masked)> _sensitive = [];
    private long _seq;
    private string _lastHash;

    public AuditLog(string directory, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        System.IO.Directory.CreateDirectory(directory);
        (_seq, _lastHash) = Tail(directory);
    }

    public string Directory { get; }

    /// <summary>Registers a value that must never appear in the log (e.g. a full account id) and its replacement.</summary>
    public void AddSensitive(string value, string masked)
    {
        if (!string.IsNullOrEmpty(value))
        {
            lock (_lock)
            {
                _sensitive.Add((value, masked));
            }
        }
    }

    /// <summary>Appends one record. <paramref name="data"/> is serialised with camelCase names.</summary>
    public long Append(string kind, object? data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        lock (_lock)
        {
            DateTimeOffset now = _time.GetUtcNow();
            var record = new JsonObject
            {
                ["seq"] = _seq + 1,
                ["atUtc"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["kind"] = kind,
                ["data"] = data is null ? null : JsonSerializer.SerializeToNode(data, data.GetType(), Json),
                ["prevHash"] = _lastHash,
            };
            string text = Redact(record.ToJsonString());
            string hash = Hash(_lastHash, text);
            string file = Path.Combine(Directory, FileName(now));
            File.AppendAllText(file, $"{{\"hash\":\"{hash}\",\"record\":{text}}}\n", Encoding.UTF8);
            _seq++;
            _lastHash = hash;
            return _seq;
        }
    }

    /// <summary>Checks every file in order: hashes, the chain across files and the sequence numbers.</summary>
    public static AuditVerification Verify(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return new AuditVerification(true, 0, 0, null);
        }

        string previous = GenesisHash;
        long expectedSeq = 1;
        int records = 0;
        string[] files = Files(directory);
        foreach (string file in files)
        {
            int line = 0;
            foreach (string text in File.ReadLines(file))
            {
                line++;
                string where = $"{Path.GetFileName(file)} line {line}";
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(text);
                    string recordText = doc.RootElement.GetProperty("record").GetRawText();
                    string stored = doc.RootElement.GetProperty("hash").GetString() ?? string.Empty;
                    JsonElement record = doc.RootElement.GetProperty("record");
                    if (record.GetProperty("prevHash").GetString() != previous)
                    {
                        return new AuditVerification(false, records, files.Length, $"{where}: prevHash does not match the previous record (deleted or reordered?)");
                    }

                    if (record.GetProperty("seq").GetInt64() != expectedSeq)
                    {
                        return new AuditVerification(false, records, files.Length, $"{where}: seq {record.GetProperty("seq").GetInt64()}, expected {expectedSeq}");
                    }

                    if (Hash(previous, recordText) != stored)
                    {
                        return new AuditVerification(false, records, files.Length, $"{where}: the record does not match its hash (edited?)");
                    }

                    previous = stored;
                    expectedSeq++;
                    records++;
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    return new AuditVerification(false, records, files.Length, $"{where} is not an audit record ({ex.GetType().Name})");
                }
            }
        }

        return new AuditVerification(true, records, files.Length, null);
    }

    /// <summary>The records of one file, oldest first (for reports).</summary>
    public static IReadOnlyList<JsonElement> Read(string file) =>
        [.. File.ReadLines(file).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("record").Clone())];

    internal static string FileName(DateTimeOffset utc) =>
        DateOnly.FromDateTime(MarketTime.ToStockholm(utc).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl";

    private static string[] Files(string directory) =>
        [.. System.IO.Directory.EnumerateFiles(directory, "????-??-??.jsonl").Order(StringComparer.Ordinal)];

    private static (long Seq, string Hash) Tail(string directory)
    {
        string? last = Files(directory).LastOrDefault();
        string? line = last is null ? null : File.ReadLines(last).LastOrDefault(l => l.Length > 0);
        if (line is null)
        {
            return (0, GenesisHash);
        }

        using JsonDocument doc = JsonDocument.Parse(line);
        return (doc.RootElement.GetProperty("record").GetProperty("seq").GetInt64(), doc.RootElement.GetProperty("hash").GetString()!);
    }

    private static string Hash(string previous, string recordText) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(previous + "\n" + recordText)));

    private string Redact(string text)
    {
        foreach ((string value, string masked) in _sensitive)
        {
            text = text.Replace(value, masked, StringComparison.Ordinal);
        }

        return text;
    }
}
