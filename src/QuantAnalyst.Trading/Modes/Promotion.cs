using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Reports;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Modes;

/// <summary>
/// Where the owner's promotion key lives: Windows Credential Manager (<c>QuantAnalyst:Promotion</c>), created once by
/// <c>qa promote --init-key</c>. The key signs promotion records; it is never printed, logged or stored in the repo.
/// </summary>
public interface IPromotionKeyStore
{
    string Name { get; }

    /// <summary>Returns the key, or null when none was created.</summary>
    byte[]? Read();

    /// <summary>Stores a new key; throws when one exists (replacing it would invalidate every record).</summary>
    void Create(byte[] key);
}

public sealed record PromotionEvidence(string File, string Sha256);

/// <summary>One promotion (or demotion) of the highest allowed mode, signed with the owner's key (ADR 0003 §3).</summary>
public sealed record PromotionRecord(
    string To,
    string From,
    DateTimeOffset At,
    string Operator,
    IReadOnlyList<PromotionEvidence> Evidence,
    string EvidenceSha256,
    IReadOnlyList<string> Gate,
    string Hmac);

public sealed record GateResult(bool Met, IReadOnlyList<string> Lines, IReadOnlyList<EodReport> Evidence);

public sealed record PromotionVerification(bool Valid, TradingMode MaxAllowed, int Records, IReadOnlyList<string> Problems);

/// <summary>The automatic gate checks of <c>qa promote</c> (ADR 0003 §3), from end-of-day reports.</summary>
public static class PromotionGate
{
    /// <summary>ADR 0003 §3: at least this many Paper trading days before Confirm.</summary>
    public const int MinPaperDays = 10;

    /// <summary>
    /// Paper → Confirm. The evidence is every complete Paper day <b>after the last day that was not clean</b> (a violation,
    /// a reconciliation mismatch or an insane fill), so an early problem is not held against later clean weeks, but no
    /// problem day can be inside the evidence. Met when the evidence has ≥ 10 days, at least one order was sent in it,
    /// and the audit chain is intact.
    /// </summary>
    public static GateResult Confirm(IReadOnlyList<EodReport> reports, AuditVerification audit)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(audit);
        CultureInfo c = CultureInfo.InvariantCulture;
        EodReport[] paper = [.. reports.Where(r => r.Complete && r.Modes.Contains("Paper") && !r.Modes.Any(m => m is "Confirm" or "Auto")).OrderBy(r => r.Date)];
        EodReport? lastProblem = paper.LastOrDefault(r => !r.Clean);
        EodReport[] evidence = lastProblem is null ? paper : [.. paper.Where(r => r.Date > lastProblem.Date)];
        int sent = evidence.Sum(r => r.Submitted);
        int fills = evidence.Sum(r => r.Fills.Count);
        decimal worst = evidence.SelectMany(r => r.Fills).Select(f => Math.Abs(f.DeviationBps ?? 0m)).DefaultIfEmpty(0m).Max();

        var lines = new List<string>
        {
            string.Create(c, $"{Mark(evidence.Length >= MinPaperDays)} clean Paper trading days in a row: {evidence.Length} (need {MinPaperDays}){(evidence.Length > 0 ? $", {evidence[0].Date:yyyy-MM-dd} to {evidence[^1].Date:yyyy-MM-dd}" : string.Empty)}"),
            string.Create(c, $"{Mark(true)} zero violations and reconciliation always matched on those days"),
            string.Create(c, $"{Mark(sent > 0)} orders sent on those days: {sent} ({fills} fill(s), worst fill {worst:0.#} bps from its reference; limit ±{EodReport.SanityLimitBps:0})"),
            string.Create(c, $"{Mark(audit.Valid)} audit chain: {(audit.Valid ? $"intact ({audit.Records} records, {audit.Files} files)" : audit.Problem)}"),
        };
        if (lastProblem is not null)
        {
            string why = (lastProblem.Violations.Count > 0 ? lastProblem.Violations[0] : null)
                         ?? (lastProblem.ReconciliationMismatchRuns > 0 ? $"{lastProblem.ReconciliationMismatchRuns} reconciliation mismatch run(s)" : $"{lastProblem.FillSanityOutliers} fill(s) outside ±{EodReport.SanityLimitBps:0} bps");
            lines.Add(string.Create(c, $"  last day that was not clean: {lastProblem.Date:yyyy-MM-dd} ({why}); the count restarts after it"));
        }

        return new GateResult(evidence.Length >= MinPaperDays && sent > 0 && audit.Valid, lines, evidence);
    }

    /// <summary>ADR 0003 §3: at least this many confirmed live orders before Auto.</summary>
    public const int MinConfirmedOrders = 20;

    /// <summary>
    /// Confirm → Auto (ADR 0003 §3; plan 07 step 6), from every Confirm day's report: at least 20 confirmed live orders
    /// (rehearsals on a simulated channel don't count), no order Unknown at the end of any day, the value-weighted mean
    /// slippage of the filled ones against the arrival mid no worse than the backtest's cost assumption, no violations
    /// on Confirm days (stricter than the ADR's list: a live day with a violation is no evidence), and an intact audit
    /// chain. Auto itself arrives in Phase 8; this only shows how far Confirm is.
    /// </summary>
    public static GateResult Auto(IReadOnlyList<EodReport> reports, AuditVerification audit)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(audit);
        CultureInfo c = CultureInfo.InvariantCulture;
        EodReport[] days = [.. reports.Where(r => r.Modes.Contains("Confirm")).OrderBy(r => r.Date)];
        EodLiveOrder[] live = [.. days.SelectMany(r => r.Live?.Orders ?? []).Where(o => !o.Simulated)];
        EodLiveOrder[] filled = [.. live.Where(o => o.Filled > 0 && o.AverageFillPrice is not null)];
        decimal? mean = EodExecution.WeightedMean(filled, o => o.SlippageVsArrivalBps);
        decimal[] assumptions = [.. days.Select(r => r.Live?.CostAssumptionBps).OfType<decimal>()];
        decimal? assumption = assumptions.Length > 0 ? assumptions.Min() : null; // the strictest one recorded
        bool slippageOk = mean is { } m && assumption is { } a && m <= a;
        int unknown = days.Sum(r => r.UnknownAtEnd);
        int violations = days.Sum(r => r.Violations.Count);
        decimal quoted = live.Sum(o => o.AvanzaFee ?? 0m);
        decimal model = live.Sum(o => o.ModelFee);

        var lines = new List<string>
        {
            string.Create(c, $"{Mark(live.Length >= MinConfirmedOrders)} confirmed live orders: {live.Length} (need {MinConfirmedOrders}){(days.Length > 0 ? $", over {days.Length} Confirm day(s) from {days[0].Date:yyyy-MM-dd}" : string.Empty)}"),
            string.Create(c, $"{Mark(unknown == 0)} orders still Unknown at the end of a day: {unknown} (need 0)"),
            string.Create(c, $"{Mark(slippageOk)} slippage vs the arrival mid: {(mean is { } s ? $"{s:+0.0;-0.0} bps" : "none yet")} over {filled.Length} filled order(s); the backtest assumes {(assumption is { } x ? $"{x:0.#} bps" : "(not recorded)")} (half-spread + slippage)"),
            string.Create(c, $"{Mark(violations == 0)} violations on Confirm days: {violations}"),
            string.Create(c, $"{Mark(audit.Valid)} audit chain: {(audit.Valid ? $"intact ({audit.Records} records, {audit.Files} files)" : audit.Problem)}"),
            string.Create(c, $"  fees: Avanza quoted {quoted:N2} SEK, the model {model:N2} SEK"),
        };
        return new GateResult(live.Length >= MinConfirmedOrders && unknown == 0 && slippageOk && violations == 0 && audit.Valid, lines, days);
    }

    private static string Mark(bool ok) => ok ? "[ok]" : "[--]";
}

/// <summary>
/// Signing, verifying and writing promotion records (ADR 0003 §3). A record's <c>hmac</c> is HMAC-SHA256, keyed by the
/// owner's promotion key, over the record's canonical JSON (fixed field order, no whitespace, <c>hmac</c> excluded).
/// Confirm and Auto (Phases 7–8) start only when <see cref="Verify"/> passes.
/// </summary>
public static class Promotion
{
    public const int KeyBytes = 32;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    public static string Sha256File(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>SHA-256 over the sorted "file sha256" lines: one value that pins every evidence file.</summary>
    public static string EvidenceHash(IEnumerable<PromotionEvidence> evidence) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(
            evidence.OrderBy(e => e.File, StringComparer.Ordinal).Select(e => $"{e.File} {e.Sha256}\n")))));

    public static byte[] Canonical(PromotionRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("to", r.To);
            w.WriteString("from", r.From);
            w.WriteString("at", r.At.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            w.WriteString("operator", r.Operator);
            w.WriteStartArray("evidence");
            foreach (PromotionEvidence e in r.Evidence)
            {
                w.WriteStartObject();
                w.WriteString("file", e.File);
                w.WriteString("sha256", e.Sha256);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteString("evidenceSha256", r.EvidenceSha256);
            w.WriteStartArray("gate");
            foreach (string line in r.Gate)
            {
                w.WriteStringValue(line);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static PromotionRecord Sign(PromotionRecord record, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return record with { Hmac = Convert.ToHexStringLower(HMACSHA256.HashData(key, Canonical(record))) };
    }

    public static bool IsSignedBy(PromotionRecord r, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(r);
        byte[] expected = HMACSHA256.HashData(key, Canonical(r));
        byte[] actual;
        try
        {
            actual = Convert.FromHexString(r.Hmac);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static IReadOnlyList<PromotionRecord> ReadRecords(string promotionDirectory)
    {
        string path = Path.Combine(promotionDirectory, PromotionState.StateFile);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("records", out JsonElement records))
            {
                return [];
            }

            return [.. records.EnumerateArray().Select(r => new PromotionRecord(
                r.GetProperty("to").GetString()!,
                r.GetProperty("from").GetString()!,
                DateTimeOffset.Parse(r.GetProperty("at").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                r.GetProperty("operator").GetString()!,
                [.. r.GetProperty("evidence").EnumerateArray().Select(e => new PromotionEvidence(e.GetProperty("file").GetString()!, e.GetProperty("sha256").GetString()!))],
                r.GetProperty("evidenceSha256").GetString()!,
                [.. r.GetProperty("gate").EnumerateArray().Select(g => g.GetString()!)],
                r.GetProperty("hmac").GetString()!))];
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new TradingConfigException($"{path} is not a valid promotion state: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Checks the local promotion state: every record signed by <paramref name="key"/>, the modes chained (each record's
    /// <c>from</c> is the previous <c>to</c>, starting at Paper), <c>maxAllowed</c> equal to the last record's <c>to</c>, and
    /// every evidence file present and unchanged (paths relative to <paramref name="baseDirectory"/>).
    /// </summary>
    public static PromotionVerification Verify(string promotionDirectory, byte[] key, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(key);
        PromotionState state = PromotionState.Load(promotionDirectory);
        IReadOnlyList<PromotionRecord> records = ReadRecords(promotionDirectory);
        var problems = new List<string>();
        string expectedFrom = nameof(TradingMode.Paper);
        for (int i = 0; i < records.Count; i++)
        {
            PromotionRecord r = records[i];
            if (!IsSignedBy(r, key))
            {
                problems.Add($"record {i + 1} ({r.From} to {r.To}, {r.At:yyyy-MM-dd}): the signature does not match; the record was changed or signed with another key");
            }

            if (r.From != expectedFrom)
            {
                problems.Add($"record {i + 1}: from {r.From}, but the previous state was {expectedFrom}");
            }

            if (EvidenceHash(r.Evidence) != r.EvidenceSha256)
            {
                problems.Add($"record {i + 1}: the evidence list does not match its hash");
            }

            foreach (PromotionEvidence e in r.Evidence)
            {
                string path = Path.Combine(baseDirectory, e.File);
                if (!File.Exists(path))
                {
                    problems.Add($"record {i + 1}: evidence {e.File} is missing");
                }
                else if (Sha256File(path) != e.Sha256)
                {
                    problems.Add($"record {i + 1}: evidence {e.File} changed after the promotion");
                }
            }

            expectedFrom = r.To;
        }

        string last = records.Count > 0 ? records[^1].To : nameof(TradingMode.Paper);
        if (state.MaxAllowed.ToString() != last)
        {
            problems.Add($"maxAllowed is {state.MaxAllowed}, but the last signed record says {last}");
        }

        return new PromotionVerification(problems.Count == 0, state.MaxAllowed, records.Count, problems);
    }

    /// <summary>Appends a signed record to <c>promotion/state.json</c> and sets <c>maxAllowed</c> to its target (atomic replace).</summary>
    public static void Append(string promotionDirectory, PromotionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        List<PromotionRecord> records = [.. ReadRecords(promotionDirectory), record];
        Directory.CreateDirectory(promotionDirectory);
        string path = Path.Combine(promotionDirectory, PromotionState.StateFile);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("note", "Written by the owner's 'qa promote' only (ADR 0003 section 3). Every record is HMAC-signed; editing this file invalidates it.");
            w.WriteString("maxAllowed", record.To);
            w.WritePropertyName("records");
            w.WriteStartArray();
            foreach (PromotionRecord r in records)
            {
                using JsonDocument canonical = JsonDocument.Parse(Canonical(r));
                w.WriteStartObject();
                foreach (JsonProperty p in canonical.RootElement.EnumerateObject())
                {
                    p.WriteTo(w);
                }

                w.WriteString("hmac", r.Hmac);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        string temp = path + ".tmp";
        File.WriteAllBytes(temp, stream.ToArray());
        File.Move(temp, path, overwrite: true);
    }
}
