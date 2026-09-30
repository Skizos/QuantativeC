using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Modes;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>A key store in memory: the real one is Windows Credential Manager, which tests never touch.</summary>
internal sealed class MemoryPromotionKeys : IPromotionKeyStore
{
    public byte[]? Key { get; set; }

    public string Name => "test key store";

    public byte[]? Read() => Key;

    public void Create(byte[] key) => Key = Key is null ? key : throw new InvalidOperationException("exists");
}

/// <summary>
/// The end-of-day report and the owner's promotion command, in-process (Claude never runs the promotion command itself:
/// hook rule 6 and the settings deny rules block it). Audit days are written with the real <see cref="AuditLog"/> in the
/// shape a Paper session writes them.
/// </summary>
public sealed class CliPromotionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-cli-promotion", Guid.NewGuid().ToString("N"));
    private readonly MemoryPromotionKeys _keys = new();

    public CliPromotionTests() => Directory.CreateDirectory(_root);

    private string Audit => Path.Combine(_root, "audit");

    private string Reports => Path.Combine(_root, "reports");

    private string PromotionDir => Path.Combine(_root, "promotion");

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

    private (int Code, string Output, string Error) Qa(string input, params string[] args)
    {
        var services = new AvanzaCliServices(
            (_, _, _, _, _) => throw new InvalidOperationException("no Avanza access in these tests"),
            _ => FakeSecrets.Store())
        {
            Input = new StringReader(input),
            PromotionKeys = () => _keys,
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 16, 0, 0, TimeSpan.Zero)),
        };
        var output = new StringWriter();
        var error = new StringWriter();
        int code = QaCli.Run(args, output, error, services);
        return (code, output.ToString(), error.ToString());
    }

    private (int Code, string Output, string Error) Promote(string input, params string[] args) =>
        Qa(input, ["promote", .. args, "--audit-dir", Audit, "--reports-dir", Reports, "--promotion-dir", PromotionDir]);

    /// <summary>Writes one Paper trading day's audit records: a session, one risk-checked order filled at the ask, a clean reconciliation, the close.</summary>
    private void PaperDay(DateOnly date, bool mismatch = false)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(date.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));
        var log = new AuditLog(Audit, time);
        string id = Guid.NewGuid().ToString();
        log.Append("session-start", new { mode = "Paper" });
        log.Append("decision", new { intents = 1 });
        log.Append("intent", new { ticker = "ERIC B" });
        log.Append("risk", new { passed = true, checks = Array.Empty<object>() });
        log.Append("oms-new", new { clientOrderId = id, ticker = "ERIC B", limitPrice = 71.2m });
        log.Append("submit", new { clientOrderId = id });
        log.Append("submit-result", new { clientOrderId = id, outcome = "Accepted" });
        log.Append("sim-fill", new { clientOrderId = id, ticker = "ERIC B", side = "Buy", limit = 71.2m, volume = 7, price = 70.86m, how = "marketable at entry", windowVwap = (decimal?)null, arrivalPrice = 70.85m });
        log.Append("oms-fill", new { clientOrderId = id, to = "Filled" });
        log.Append("reconcile", new { mismatches = mismatch ? ["x"] : Array.Empty<string>() });
        log.Append("end-of-day", new { day = new { startOfDayValue = 5000m, accountValue = 5001m, cash = 4504m, feesPaid = 0m } });
    }

    private void PaperDays(int count, int from = 0)
    {
        var d = new DateOnly(2026, 9, 28);
        int written = 0;
        for (int i = 0; written < count + from; i++, d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            if (written++ >= from)
            {
                PaperDay(d);
            }
        }
    }

    [Fact]
    public void ReportEod_RebuildsADayFromTheAudit_AndGateShowsProgress()
    {
        PaperDays(3);
        (int code, string output, string error) = Qa(string.Empty, "report", "eod", "--date", "2026-09-29", "--audit-dir", Audit, "--reports-dir", Reports);
        Assert.True(code == 0, error);
        Assert.Contains("2026-09-29 CLEAN: 1 sent, 1 accepted", output, StringComparison.Ordinal);
        Assert.Contains("fill Buy 7 ERIC B @ 70.86 (limit 71.2, marketable at entry); arrival 70.85, +1.4 bps", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Reports, "2026-09-29.json")));

        (code, output, _) = Qa(string.Empty, "report", "gate", "--audit-dir", Audit, "--reports-dir", Reports);
        Assert.Equal(0, code);
        Assert.Contains("Confirm gate: not met yet", output, StringComparison.Ordinal);
        Assert.Contains("clean Paper trading days in a row: 3 (need 10)", output, StringComparison.Ordinal);
    }

    /// <summary>Plan 19: a Paper day whose ERIC B buy of 20 at 70.00 filled 5, while the day traded down to 69.90 and closed at 70.50.</summary>
    private void UnfilledDay(DateOnly date)
    {
        var log = new AuditLog(Audit, new FakeTimeProvider(new DateTimeOffset(date.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero)));
        string id = Guid.NewGuid().ToString();
        log.Append("session-start", new { mode = "Paper" });
        log.Append("intent", new { ticker = "ERIC B", decisionPrice = 69.65m });
        log.Append("risk", new { passed = true, checks = Array.Empty<object>() });
        log.Append("oms-new", new { clientOrderId = id, orderbookId = "5240", ticker = "ERIC B", side = "Buy", volume = 20, limitPrice = 70.00m });
        log.Append("submit", new { clientOrderId = id });
        log.Append("submit-result", new { clientOrderId = id, outcome = "Accepted" });
        log.Append("oms-fill", new { clientOrderId = id, volume = 5, price = 70.00m, value = 350m, fees = 0m, to = "PartiallyFilled" });
        log.Append("close-mark", new { orderbookId = "5240", ticker = "ERIC B", currency = "SEK", last = 70.50m, bid = 70.48m, ask = 70.52m, dayHigh = 70.9m, dayLow = 69.9m, sekPerUnit = 1m });
        log.Append("oms-state", new { clientOrderId = id, to = "Cancelled", why = "day order expired at the close" });
        log.Append("reconcile", new { mismatches = Array.Empty<string>() });
        log.Append("end-of-day", new { day = new { startOfDayValue = 5000m, accountValue = 5001m, cash = 4650m, feesPaid = 0m } });
    }

    [Fact]
    public void ReportEod_ShowsWhatEachLimitMissed_AndTheFillRateOverAllDays()
    {
        UnfilledDay(new DateOnly(2026, 9, 29));
        UnfilledDay(new DateOnly(2026, 9, 30));

        (int code, string output, string error) = Qa(string.Empty, "report", "eod", "--date", "2026-09-30", "--audit-dir", Audit, "--reports-dir", Reports);
        Assert.True(code == 0, error);
        Assert.Contains(
            "Limits: 1 limit order(s): 0 filled, 1 partly, 0 not (25 % of the volume); the backtest fills 1; missed vs the backtest +7.50 SEK (+71.4 bps).",
            output, StringComparison.Ordinal);
        Assert.Contains(
            "  limit Buy 20 ERIC B @ 70: filled 5/20 (Cancelled); day low 69.9, high 70.9, close 70.5: the backtest fills it; missed +7.50 SEK (+71.4 bps)",
            output, StringComparison.Ordinal);
        Assert.Contains(
            "All 2 days (2026-09-29 to 2026-09-30): 2 limit order(s): 0 filled, 2 partly, 0 not (25 % of the volume); the backtest fills 2; missed vs the backtest +15.00 SEK (+71.4 bps).",
            output, StringComparison.Ordinal);

        (_, output, _) = Qa(string.Empty, "report", "eod", "--date", "2026-09-30", "--json", "--audit-dir", Audit, "--reports-dir", Reports);
        Assert.DoesNotContain("All 2 days", output, StringComparison.Ordinal); // JSON stays JSON
        Assert.Contains("\"fillRate\"", output, StringComparison.Ordinal);
    }

    /// <summary>One Confirm day: a confirmed ERIC B buy of 10 decided at 70.80, sent when the mid was 70.85, filled at 70.86.</summary>
    private void ConfirmDay(DateOnly date)
    {
        var log = new AuditLog(Audit, new FakeTimeProvider(new DateTimeOffset(date.ToDateTime(new TimeOnly(7, 0)), TimeSpan.Zero)));
        string id = Guid.NewGuid().ToString();
        log.Append("session-start", new { mode = "Confirm", costAssumptionBps = 12m });
        log.Append("recheck", new { passed = true, checks = Array.Empty<object>() });
        log.Append("gate", new { mode = "Confirm", channel = "avanza", decision = "confirmed", simulated = false, decisionPrice = 70.80m, arrivalMid = 70.85m, avanzaFee = 1m, modelFee = 1m });
        log.Append("oms-new", new { clientOrderId = id, ticker = "ERIC B", side = "Buy", volume = 10, limitPrice = 71.2m });
        log.Append("submit", new { clientOrderId = id });
        log.Append("submit-result", new { clientOrderId = id, outcome = "Accepted" });
        log.Append("oms-fill", new { clientOrderId = id, volume = 10, price = 70.86m, value = 708.6m, fees = 1m, source = "reconciliation", to = "Filled" });
        log.Append("reconcile", new { mismatches = Array.Empty<string>() });
        log.Append("end-of-day", new { day = new { accountValue = 5000m, startOfDayValue = 5000m, cash = 4291m } });
    }

    [Fact]
    public void ReportEod_ShowsEachConfirmedOrder_AndGateShowsTheAutoGate()
    {
        ConfirmDay(new DateOnly(2026, 10, 12));

        (int code, string output, string error) = Qa(string.Empty, "report", "eod", "--date", "2026-10-12", "--audit-dir", Audit, "--reports-dir", Reports);
        Assert.True(code == 0, error);
        Assert.Contains("Live: 1 confirmed order(s), 1 filled, slippage +1.4 bps vs the arrival mid (the backtest assumes 12).", output, StringComparison.Ordinal);
        Assert.Contains("  live Buy ERIC B (limit 71.2, Filled): filled 10/10 @ 70.86; +8.5 bps vs the decision, +1.4 bps vs the arrival mid; fee Avanza 1.00 / model 1.00 SEK", output, StringComparison.Ordinal);

        (code, output, _) = Qa(string.Empty, "report", "gate", "--audit-dir", Audit, "--reports-dir", Reports);
        Assert.Equal(0, code);
        Assert.Contains("Auto gate: not met yet (Auto itself arrives in Phase 8)", output, StringComparison.Ordinal);
        Assert.Contains("[--] confirmed live orders: 1 (need 20), over 1 Confirm day(s) from 2026-10-12", output, StringComparison.Ordinal);
        Assert.Contains("[ok] slippage vs the arrival mid: +1.4 bps over 1 filled order(s); the backtest assumes 12 bps", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_OncePromotedToConfirm_CountsTheOrdersTowardsTheAutoGate()
    {
        _keys.Key = Promotion.NewKey();
        Promotion.Append(PromotionDir, Promotion.Sign(
            new PromotionRecord("Confirm", "Paper", new DateTimeOffset(2026, 10, 9, 16, 0, 0, TimeSpan.Zero), "owner", [], Promotion.EvidenceHash([]), ["[ok] test"], string.Empty), _keys.Key));
        ConfirmDay(new DateOnly(2026, 10, 12));

        (int code, string output, string error) = Qa(string.Empty, "status", "--audit-dir", Audit, "--promotion-dir", PromotionDir,
            "--state-dir", Path.Combine(_root, "state"), "--kill-file", Path.Combine(_root, "KILL"));

        Assert.True(code == 0, error);
        Assert.Contains("promoted to Confirm", output, StringComparison.Ordinal);
        Assert.Contains("1 of 20 confirmed live orders; the rest of the gate: qa report gate", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Promote_NeedsTheKey_AndInitKeyCreatesItOnce()
    {
        Assert.Contains("No promotion key", Promote(string.Empty, "--verify").Error, StringComparison.Ordinal);
        Assert.Equal(0, Promote(string.Empty, "--init-key").Code);
        Assert.NotNull(_keys.Key);
        byte[] key = _keys.Key!;
        (int code, _, string error) = Promote(string.Empty, "--init-key");
        Assert.Equal(1, code);
        Assert.Contains("already exists", error, StringComparison.Ordinal);
        Assert.Same(key, _keys.Key);
        Assert.Contains("Promotion state OK: maxAllowed Paper, 0 signed record(s)", Promote(string.Empty, "--verify").Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Promote_ToConfirm_IsRefusedBeforeTenCleanDays_AndWritesNothing()
    {
        _keys.Key = Promotion.NewKey();
        PaperDays(9);
        (int code, string output, _) = Promote("Confirm", "--to", "Confirm");
        Assert.Equal(1, code);
        Assert.Contains("[--] clean Paper trading days in a row: 9 (need 10)", output, StringComparison.Ordinal);
        Assert.Contains("Not met: nothing was written.", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(PromotionDir, PromotionState.StateFile)));
    }

    [Fact]
    public void Promote_ToConfirm_AfterTenCleanDays_NeedsTheExactModeTyped_ThenWritesASignedRecord()
    {
        _keys.Key = Promotion.NewKey();
        PaperDays(10);

        (int code, string output, _) = Promote("confirm", "--to", "Confirm");
        Assert.Equal(1, code);
        Assert.Contains("Cancelled: nothing was written.", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(PromotionDir, PromotionState.StateFile)));

        (code, output, string error) = Promote("Confirm\n", "--to", "Confirm", "--operator", "owner");
        Assert.True(code == 0, output + error);
        Assert.Contains("[ok] clean Paper trading days in a row: 10 (need 10), 2026-09-28 to 2026-10-09", output, StringComparison.Ordinal);
        Assert.Contains("Done: the highest allowed mode is now Confirm (1 signed record(s)", output, StringComparison.Ordinal);
        Assert.Contains("Every Confirm session still runs the startup checks first", output, StringComparison.Ordinal);

        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PromotionDir, PromotionState.StateFile))))
        {
            JsonElement record = doc.RootElement.GetProperty("records")[0];
            Assert.Equal(("Confirm", "Paper", "owner"), (record.GetProperty("to").GetString(), record.GetProperty("from").GetString(), record.GetProperty("operator").GetString()));
            Assert.Equal(10, record.GetProperty("evidence").GetArrayLength());
            Assert.Equal(64, record.GetProperty("hmac").GetString()!.Length);
        }

        Assert.Contains("Promotion state OK: maxAllowed Confirm, 1 signed record(s)", Promote(string.Empty, "--verify").Output, StringComparison.Ordinal);

        // Anyone editing an evidence report afterwards is caught.
        string evidence = Directory.GetFiles(Reports).Order(StringComparer.Ordinal).First();
        File.AppendAllText(evidence, " ");
        (code, output, _) = Promote(string.Empty, "--verify");
        Assert.Equal(1, code);
        Assert.Contains("changed after the promotion", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Promote_TamperedState_IsRefused_AndAutoWaitsForPhase8()
    {
        _keys.Key = Promotion.NewKey();
        PaperDays(10);
        Assert.Equal(0, Promote("Confirm", "--to", "Confirm").Code);

        (int code, _, string error) = Promote("Auto", "--to", "Auto");
        Assert.Equal(1, code);
        Assert.Contains("Auto mode arrives in Phase 8. Its gate", error, StringComparison.Ordinal);

        string state = Path.Combine(PromotionDir, PromotionState.StateFile);
        File.WriteAllText(state, File.ReadAllText(state).Replace("\"operator\": \"", "\"operator\": \"x", StringComparison.Ordinal));
        (code, string output, _) = Promote("Confirm", "--to", "Confirm");
        Assert.Equal(1, code);
        Assert.Contains("INVALID", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Demotion_NeedsNoGate_ButIsTypedAndSigned()
    {
        _keys.Key = Promotion.NewKey();
        PaperDays(10);
        Assert.Equal(0, Promote("Confirm", "--to", "Confirm").Code);

        (int code, string output, _) = Promote("Paper", "--to", "Paper");
        Assert.Equal(0, code);
        Assert.Contains("lowering the mode needs no gate", output, StringComparison.Ordinal);
        Assert.Contains("now Paper (2 signed record(s)", output, StringComparison.Ordinal);
        Assert.Contains("already Paper", Promote(string.Empty, "--to", "Paper").Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AProblemDay_RestartsTheCount()
    {
        _keys.Key = Promotion.NewKey();
        PaperDays(4);
        PaperDay(new DateOnly(2026, 10, 2), mismatch: true); // the 5th day had a reconciliation mismatch
        PaperDays(9, from: 5);
        (int code, string output, _) = Promote("Confirm", "--to", "Confirm");
        Assert.Equal(1, code);
        Assert.Contains("clean Paper trading days in a row: 9 (need 10)", output, StringComparison.Ordinal);
        Assert.Contains("last day that was not clean: 2026-10-02", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Promote_NeedsExactlyOneAction()
    {
        _keys.Key = Promotion.NewKey();
        Assert.Contains("exactly one of", Promote(string.Empty).Error, StringComparison.Ordinal);
        Assert.Contains("exactly one of", Promote(string.Empty, "--verify", "--init-key").Error, StringComparison.Ordinal);
        Assert.Contains("is not Backtest, Paper, Confirm or Auto", Promote(string.Empty, "--to", "Live").Error, StringComparison.Ordinal);
    }
}
