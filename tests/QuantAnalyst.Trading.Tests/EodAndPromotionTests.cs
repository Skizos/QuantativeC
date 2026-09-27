using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Reconciliation;
using QuantAnalyst.Trading.Reports;
using QuantAnalyst.Trading.Risk;
using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Trading.Tests;

public sealed class EodReportTests : IDisposable
{
    private static readonly OrderbookId Eric = RiskEngineTests.Eric;
    private static readonly DateOnly Monday = new(2026, 9, 28);
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 7, 9, 58, TimeSpan.Zero)); // 09:09:58 Stockholm

    public void Dispose() => _dir.Dispose();

    private string AuditDir => _dir.File("audit");

    [Fact]
    public async Task ARealPaperSession_GivesACompleteCleanReport_WithFillsCheckedAgainstTheMarket()
    {
        var quotes = new SettableQuotes();
        var audit = new AuditLog(AuditDir, _time);
        var halts = new HaltController(audit, _time);
        var oms = new OrderManager(audit, halts, _time);
        PaperBook book = PaperBook.InMemory(100_000m, "test-mini", quotes, _time);
        var instruments = new InstrumentCatalog([OrderPreparationTests.Spec()]);
        var channel = new PaperOrderChannel(book, PaperOrderChannelTests.Mini, quotes, instruments, _time);
        MarketCalendar calendar = OrderGatewayTests.Calendar();
        using var gateway = new OrderGateway(channel, new GatewayEnvironment
        {
            Mode = TradingMode.Paper,
            Instruments = instruments,
            Quotes = quotes,
            Account = book,
            Calendar = calendar,
            Universe = new Universe([new UniverseEntry(Eric, "ERIC B", "Ericsson B")]),
            AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { PaperConfig.AccountId },
            Fees = (p, s) => channel.EstimateFees(p.Value, s.Currency),
            CourtageVerified = false,
        }, new PreTradeRiskEngine(RiskLimits.AdrDefaults), oms, halts, audit, _time);
        using var kill = new KillSwitch(gateway, halts, audit, _time, _dir.File("KILL"), _dir.File("state"), book, RiskLimits.AdrDefaults, watch: false);
        var reports = new List<EodReport>();
        var session = new PaperSession(gateway, channel, book, kill, new Reconciler(oms, halts, audit, _time, book.Account), halts,
            new TradingSchedule(calendar, RiskLimits.AdrDefaults, new TimeOnly(9, 10)), audit, _time, Decide, new StringWriter(), day =>
            {
                EodReport r = EodReport.Build(AuditDir, day, _time);
                reports.Add(r);
                return r.Summary();
            });

        decimal total = 10_000;
        decimal last = 100.5m;
        async Task Steps(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                quotes.Set(Eric, _time.GetUtcNow(), 100.4m, 500, 100.6m, 700, last, total);
                channel.OnQuote(quotes.Latest(Eric)!);
                await session.StepAsync(CancellationToken.None);
                _time.Advance(TimeSpan.FromSeconds(1));
            }
        }

        await Steps(45); // decision at 09:10, three intents paced 13 s apart
        (last, total) = (100.1m, 10_300m); // a print through the resting 100.3 buy
        await Steps(2);
        _time.SetUtcNow(new DateTimeOffset(2026, 9, 28, 15, 30, 0, TimeSpan.Zero));
        await Steps(1);

        EodReport report = Assert.Single(reports);
        Assert.True(report.Clean, string.Join("; ", report.Violations));
        Assert.True(report.Complete);
        Assert.Equal(["Paper"], report.Modes);
        Assert.Equal((2, 2, 1), (report.Submitted, report.Accepted, report.RiskRejected));
        Assert.Contains("R6", report.RiskRejectionsByCheck.Keys);
        Assert.Equal(2, report.Fills.Count);

        EodFill atEntry = report.Fills[0];
        Assert.Equal(("marketable at entry", "arrival", 100.5m), (atEntry.How, atEntry.ReferenceKind, atEntry.Reference));
        Assert.Equal(10.0m, atEntry.DeviationBps); // bought at the 100.6 ask vs the 100.5 mid

        EodFill resting = report.Fills[1];
        Assert.Equal(("window VWAP", 100.1m), (resting.ReferenceKind, resting.Reference));
        Assert.Equal(20.0m, resting.DeviationBps); // filled at its 100.3 limit; the market printed at 100.1

        Assert.Equal(0, report.FillSanityOutliers);
        Assert.True(report.ReconciliationRuns > 0);
        Assert.Equal(0, report.ReconciliationMismatchRuns);
        Assert.NotNull(report.Account);
        Assert.True(report.AuditChainValid);
        Assert.Contains(report.Events, e => e.StartsWith("risk rejections: R", StringComparison.Ordinal));

        string saved = report.Save(_dir.File("reports"));
        Assert.Equal(report.Fills, EodReport.Load(saved).Fills);
        Assert.Equal(report.Clean, EodReport.Load(saved).Clean);
    }

    private Task<PlanResult> Decide(CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();
        OrderIntent Buy(long qty, decimal limit) => new(Eric, "ERIC B", OrderSide.Buy, qty, limit, "test", limit, now, "test");
        return Task.FromResult(new PlanResult([Buy(10, 100.8m), Buy(20, 100.3m), Buy(5_000, 100.3m)], []));
    }

    private static readonly string[] OneMismatch = ["stranger"];

    private AuditLog Synthetic() => new(AuditDir, _time);

    private static object Fill(string side, decimal price, decimal limit, decimal? vwap, decimal? arrival = null) =>
        new { clientOrderId = Guid.NewGuid(), ticker = "ERIC B", side, limit, volume = 10, price, courtage = 0m, fxFee = 0m, how = "test", windowVwap = vwap, arrivalPrice = arrival };

    [Fact]
    public void EveryKindOfViolation_IsFound_AndEventsAreKeptApart()
    {
        AuditLog a = Synthetic();
        a.Append("session-start", new { mode = "Paper" });
        a.Append("oms-new", new { clientOrderId = "o1", ticker = "ERIC B", limitPrice = 100m }); // no risk check before it
        a.Append("risk", new { passed = true, checks = Array.Empty<object>() });
        a.Append("oms-new", new { clientOrderId = "o2", ticker = "ERIC B", limitPrice = 100m });
        a.Append("oms-state", new { clientOrderId = "o2", to = "Unknown" });
        a.Append("sim-fill", Fill("Buy", 100.5m, 100m, 100.4m)); // above its buy limit
        a.Append("sim-fill", Fill("Sell", 97m, 96m, 100m)); // 300 bps worse than the window VWAP
        a.Append("halt", new { reason = "Reconciliation", detail = "book and OMS disagree" });
        a.Append("halt", new { reason = "StaleData", detail = "stream down" });
        a.Append("kill", new { source = "automatic", reason = "3 consecutive broker rejects" });
        a.Append("kill", new { source = "automatic", reason = "daily loss stop: 97 vs 100" });
        a.Append("kill", new { source = "file KILL", reason = "owner" });
        a.Append("fill-refused", new { reason = "overfill" });
        a.Append("reconcile", new { mismatches = OneMismatch });
        a.Append("reconcile", new { mismatches = Array.Empty<string>() });

        EodReport r = EodReport.Build(AuditDir, Monday, _time);
        Assert.False(r.Complete);
        Assert.False(r.Clean);
        Assert.Collection(r.Violations,
            v => Assert.Contains("o1 created without a passing risk check", v, StringComparison.Ordinal),
            v => Assert.Contains("filled at 100.5, outside its limit 100", v, StringComparison.Ordinal),
            v => Assert.Contains("halt Reconciliation", v, StringComparison.Ordinal),
            v => Assert.Contains("kill switch (automatic): 3 consecutive", v, StringComparison.Ordinal),
            v => Assert.Contains("fill refused", v, StringComparison.Ordinal),
            v => Assert.Contains("o2 is still Unknown", v, StringComparison.Ordinal));
        Assert.Contains(r.Events, e => e.Contains("halt StaleData", StringComparison.Ordinal));
        Assert.Contains(r.Events, e => e.Contains("daily loss stop", StringComparison.Ordinal));
        Assert.Contains(r.Events, e => e.Contains("kill switch (file KILL)", StringComparison.Ordinal));
        Assert.Equal(1, r.FillSanityOutliers);
        Assert.Equal(300.0m, r.Fills[1].DeviationBps);
        Assert.Equal((2, 1, true), (r.ReconciliationRuns, r.ReconciliationMismatchRuns, r.ReconciliationCleanAtEnd));
    }

    [Fact]
    public void ABrokenAuditChain_IsAViolation_AndAMissingDayIsAnError()
    {
        AuditLog a = Synthetic();
        a.Append("session-start", new { mode = "Paper" });
        a.Append("end-of-day", new { day = new { startOfDayValue = 100m, accountValue = 101m, cash = 50m, feesPaid = 0m } });
        string file = Directory.GetFiles(AuditDir).Single();
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"accountValue\":101", "\"accountValue\":201", StringComparison.Ordinal));

        EodReport r = EodReport.Build(AuditDir, Monday, _time);
        Assert.False(r.AuditChainValid);
        Assert.Contains(r.Violations, v => v.StartsWith("audit chain broken", StringComparison.Ordinal));
        Assert.False(r.Clean);
        Assert.Throws<FileNotFoundException>(() => EodReport.Build(AuditDir, Monday.AddDays(1), _time));
    }
}

public sealed class PromotionGateTests
{
    private static readonly DateOnly Start = new(2026, 9, 28);

    internal static EodReport Day(int n, bool clean = true, int sent = 1, bool complete = true, string mode = "Paper") => new()
    {
        Date = Start.AddDays(n),
        Modes = [mode],
        Complete = complete,
        Sessions = 1,
        Decisions = 1,
        Intents = sent,
        Submitted = sent,
        Accepted = sent,
        BrokerRejected = 0,
        Unknown = 0,
        RiskRejected = 0,
        RiskRejectionsByCheck = new Dictionary<string, int>(),
        Fills = [],
        FillSanityOutliers = 0,
        ReconciliationRuns = 10,
        ReconciliationMismatchRuns = 0,
        ReconciliationCleanAtEnd = true,
        Account = null,
        AuditChainValid = true,
        Violations = clean ? [] : ["halt OmsInvariant: test"],
        Events = [],
        AuditFile = "x.jsonl",
        GeneratedUtc = DateTimeOffset.UnixEpoch,
    };

    private static readonly AuditVerification Intact = new(true, 100, 10, null);

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(15, true)]
    public void TenCleanPaperDays_AreNeeded(int days, bool met)
    {
        GateResult g = PromotionGate.Confirm([.. Enumerable.Range(0, days).Select(i => Day(i))], Intact);
        Assert.Equal(met, g.Met);
        Assert.Equal(days, g.Evidence.Count);
    }

    [Fact]
    public void AProblemDay_RestartsTheCount_AndIsNeverEvidence()
    {
        EodReport[] days = [.. Enumerable.Range(0, 14).Select(i => Day(i, clean: i != 5))];
        GateResult g = PromotionGate.Confirm(days, Intact);
        Assert.False(g.Met);
        Assert.Equal(8, g.Evidence.Count);
        Assert.DoesNotContain(g.Evidence, r => r.Date <= Start.AddDays(5));
        Assert.Contains(g.Lines, l => l.Contains("last day that was not clean: 2026-10-03", StringComparison.Ordinal));

        Assert.True(PromotionGate.Confirm([.. days, .. Enumerable.Range(14, 2).Select(i => Day(i))], Intact).Met);
    }

    [Fact]
    public void IncompleteDays_ConfirmDays_NoOrders_OrABrokenAudit_DoNotPass()
    {
        EodReport[] ten = [.. Enumerable.Range(0, 10).Select(i => Day(i))];
        Assert.False(PromotionGate.Confirm([.. ten.Take(9), Day(9, complete: false)], Intact).Met);
        Assert.False(PromotionGate.Confirm([.. ten.Take(9), Day(9, mode: "Confirm")], Intact).Met);
        Assert.False(PromotionGate.Confirm([.. Enumerable.Range(0, 10).Select(i => Day(i, sent: 0))], Intact).Met);
        Assert.False(PromotionGate.Confirm(ten, new AuditVerification(false, 5, 1, "edited")).Met);
    }
}

public sealed class PromotionSigningTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly byte[] _key = Promotion.NewKey();

    public void Dispose() => _dir.Dispose();

    private PromotionRecord Record(string from = "Paper", string to = "Confirm", IReadOnlyList<PromotionEvidence>? evidence = null)
    {
        evidence ??= [];
        return Promotion.Sign(
            new PromotionRecord(to, from, new DateTimeOffset(2026, 10, 12, 16, 0, 0, TimeSpan.Zero), "owner", evidence, Promotion.EvidenceHash(evidence), ["[ok] test"], string.Empty),
            _key);
    }

    [Fact]
    public void ARecord_VerifiesWithItsKey_AndNotAfterAnyChange()
    {
        PromotionRecord r = Record();
        Assert.Equal(64, r.Hmac.Length);
        Assert.True(Promotion.IsSignedBy(r, _key));
        Assert.Equal(Promotion.Canonical(r), Promotion.Canonical(r with { }));

        Assert.False(Promotion.IsSignedBy(r, Promotion.NewKey()));
        Assert.False(Promotion.IsSignedBy(r with { To = "Auto" }, _key));
        Assert.False(Promotion.IsSignedBy(r with { Operator = "someone" }, _key));
        Assert.False(Promotion.IsSignedBy(r with { At = r.At.AddSeconds(1) }, _key));
        Assert.False(Promotion.IsSignedBy(r with { Gate = ["[ok] edited"] }, _key));
        Assert.False(Promotion.IsSignedBy(r with { Hmac = "not-hex" }, _key));
    }

    [Fact]
    public void TheStateFile_Verifies_AndEveryKindOfTamperingIsReported()
    {
        string evidenceFile = _dir.File("2026-10-09.json");
        File.WriteAllText(evidenceFile, "{\"clean\":true}");
        PromotionEvidence e = new("2026-10-09.json", Promotion.Sha256File(evidenceFile));
        string promotion = _dir.File("promotion");

        Promotion.Append(promotion, Record(evidence: [e]));
        PromotionVerification ok = Promotion.Verify(promotion, _key, _dir.Path);
        Assert.True(ok.Valid, string.Join("; ", ok.Problems));
        Assert.Equal((TradingMode.Confirm, 1), (ok.MaxAllowed, ok.Records));
        Assert.Equal(TradingMode.Confirm, PromotionState.Load(promotion).MaxAllowed);

        string state = Path.Combine(promotion, PromotionState.StateFile);
        string text = File.ReadAllText(state);
        File.WriteAllText(state, text.Replace("\"maxAllowed\": \"Confirm\"", "\"maxAllowed\": \"Auto\"", StringComparison.Ordinal));
        Assert.Contains(Promotion.Verify(promotion, _key, _dir.Path).Problems, p => p.Contains("maxAllowed is Auto", StringComparison.Ordinal));

        File.WriteAllText(state, text.Replace("\"operator\": \"owner\"", "\"operator\": \"mallory\"", StringComparison.Ordinal));
        Assert.Contains(Promotion.Verify(promotion, _key, _dir.Path).Problems, p => p.Contains("signature does not match", StringComparison.Ordinal));

        File.WriteAllText(state, text);
        File.WriteAllText(evidenceFile, "{\"clean\":false}");
        Assert.Contains(Promotion.Verify(promotion, _key, _dir.Path).Problems, p => p.Contains("changed after the promotion", StringComparison.Ordinal));
        File.Delete(evidenceFile);
        Assert.Contains(Promotion.Verify(promotion, _key, _dir.Path).Problems, p => p.Contains("is missing", StringComparison.Ordinal));
        Assert.Contains(Promotion.Verify(promotion, Promotion.NewKey(), _dir.Path).Problems, p => p.Contains("signature", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordsMustChain_FromPaper()
    {
        string promotion = _dir.File("promotion");
        Promotion.Append(promotion, Record(from: "Confirm", to: "Auto"));
        Assert.Contains(Promotion.Verify(promotion, _key, _dir.Path).Problems, p => p.Contains("from Confirm, but the previous state was Paper", StringComparison.Ordinal));
    }

    [Fact]
    public void NoStateFile_IsAValidPaperState()
    {
        PromotionVerification v = Promotion.Verify(_dir.File("promotion"), _key, _dir.Path);
        Assert.True(v.Valid);
        Assert.Equal((TradingMode.Paper, 0), (v.MaxAllowed, v.Records));
    }

    [Fact]
    public void APromotionToConfirm_AllowsConfirm_ButNotAuto()
    {
        string promotion = _dir.File("promotion");
        Promotion.Append(promotion, Record());
        PromotionState s = PromotionState.Load(promotion);
        Assert.Equal(TradingMode.Paper, s.Effective(TradingMode.Paper));
        Assert.Equal(TradingMode.Confirm, s.Effective(TradingMode.Confirm)); // still only through ConfirmStartup's checks
        ModeNotAllowedException ex = Assert.Throws<ModeNotAllowedException>(() => s.Effective(TradingMode.Auto));
        Assert.Contains("above the promotion state", ex.Message, StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(promotion, PromotionState.StateFile)));
        Assert.Equal("Confirm", doc.RootElement.GetProperty("maxAllowed").GetString());
    }
}
