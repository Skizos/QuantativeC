using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Reports;

namespace QuantAnalyst.Trading.Tests;

/// <summary>
/// Phase 7 step 6: the live execution-quality section of the end-of-day report and the Auto gate. The audit days are
/// written in the shape the gateway, the OMS and the Confirm session write them; the numbers are chosen so every
/// slippage is easy to check by hand (1 bp = 0.01 %).
/// </summary>
public sealed class LiveReportTests : IDisposable
{
    private static readonly DateOnly Monday = new(2026, 10, 12);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Audit => _dir.File("audit");

    /// <summary>One Confirm order: the checks, the confirmed gate with the market at the send, the OMS order and its fills.</summary>
    internal static void ConfirmedOrder(
        AuditLog log, string ticker, string side, long volume, decimal limit, decimal decision, decimal arrivalMid, (long Volume, decimal Price)[] fills,
        string end = "Filled", decimal avanzaFee = 0m, decimal modelFee = 0m, bool simulated = false)
    {
        string id = Guid.NewGuid().ToString();
        log.Append("intent", new { ticker });
        log.Append("risk", new { passed = true, checks = Array.Empty<object>() });
        log.Append("confirm-answer", new { verdict = "Confirmed" });
        log.Append("recheck", new { passed = true, checks = Array.Empty<object>() });
        log.Append("gate", new
        {
            mode = "Confirm",
            channel = simulated ? "fake-sim" : "avanza",
            decision = "confirmed",
            simulated,
            decisionPrice = decision,
            arrivalBid = arrivalMid - 0.05m,
            arrivalAsk = arrivalMid + 0.05m,
            arrivalMid,
            avanzaFee,
            modelFee,
        });
        log.Append("oms-new", new { clientOrderId = id, orderbookId = "5240", ticker, side, volume, limitPrice = limit });
        log.Append("submit", new { clientOrderId = id });
        log.Append("submit-result", new { clientOrderId = id, outcome = "Accepted" });
        log.Append("oms-state", new { clientOrderId = id, from = "Sent", to = "Working" });
        long filled = 0;
        foreach ((long v, decimal p) in fills)
        {
            filled += v;
            log.Append("oms-fill", new { clientOrderId = id, volume = v, price = p, value = v * p, fees = 0m, source = "reconciliation", to = filled < volume ? "PartiallyFilled" : "Filled" });
        }

        if (end != "Filled" && filled < volume)
        {
            log.Append("oms-state", new { clientOrderId = id, from = filled > 0 ? "PartiallyFilled" : "Working", to = end });
        }
    }

    private AuditLog Day(DateOnly date, decimal? costAssumptionBps = 12m)
    {
        var log = new AuditLog(Audit, new FakeTimeProvider(new DateTimeOffset(date.ToDateTime(new TimeOnly(7, 0)), TimeSpan.Zero)));
        log.Append("session-start", new { mode = "Confirm", costAssumptionBps });
        log.Append("gateway-start", new { mode = "Confirm", channel = "avanza" });
        return log;
    }

    private static void Close(AuditLog log) =>
        log.Append("end-of-day", new { day = new { cards = 3, accountValue = 12_400m, startOfDayValue = 12_345.67m, cash = 9_000m, killed = false } });

    [Fact]
    public void EveryConfirmedOrder_IsMeasured_AgainstTheDecisionAndTheArrivalMid()
    {
        AuditLog log = Day(Monday);
        // A buy filled in two parts at 100.10 on average: 10 bps above the decision (100), 5 bps above the arrival mid (100.05).
        ConfirmedOrder(log, "ERIC B", "Buy", 10, 100.2m, 100m, 100.05m, [(4, 100.05m), (6, 100.1333333333333333333333333m)], avanzaFee: 39m, modelFee: 1m);
        // A sell filled at 199.80: 10 bps below the decision (200), 5 bps below the arrival mid (199.90).
        ConfirmedOrder(log, "TEST B", "Sell", 5, 199.7m, 200m, 199.9m, [(5, 199.8m)]);
        // A buy that never filled and was cancelled at the close.
        ConfirmedOrder(log, "VOLV B", "Buy", 3, 250m, 250m, 250.1m, [], end: "Cancelled");
        Close(log);

        EodReport r = EodReport.Build(Audit, Monday, TimeProvider.System);

        EodExecution live = Assert.IsType<EodExecution>(r.Live);
        Assert.Equal((3, 2), (live.Sent, live.Filled));
        EodLiveOrder buy = live.Orders[0];
        Assert.Equal((10L, 100.1m, 10.0m, 5.0m), (buy.Filled, decimal.Round(buy.AverageFillPrice!.Value, 4), buy.SlippageVsDecisionBps, buy.SlippageVsArrivalBps));
        EodLiveOrder sell = live.Orders[1];
        Assert.Equal((199.8m, 10.0m, 5.0m), (sell.AverageFillPrice, sell.SlippageVsDecisionBps, sell.SlippageVsArrivalBps));
        EodLiveOrder unfilled = live.Orders[2];
        Assert.Equal((0L, (decimal?)null, "Cancelled"), (unfilled.Filled, unfilled.SlippageVsArrivalBps, unfilled.State));

        Assert.Equal((5.0m, 10.0m, 12m, true), (live.MeanSlippageVsArrivalBps, live.MeanSlippageVsDecisionBps, live.CostAssumptionBps, live.WithinAssumption));
        Assert.Equal((39m, 1m, 1), (live.AvanzaFees, live.ModelFees, live.FeeFlags)); // 38 SEK apart: flagged
        Assert.Contains("Live: 3 confirmed order(s), 2 filled, slippage +5.0 bps vs the arrival mid (the backtest assumes 12).", r.Summary(), StringComparison.Ordinal);
        Assert.Empty(r.Violations);
    }

    [Fact]
    public void SlippageAboveTheAssumption_IsNotWithinIt_AndABetterFillIsNegative()
    {
        AuditLog log = Day(Monday, costAssumptionBps: 1m);
        ConfirmedOrder(log, "ERIC B", "Buy", 10, 100.2m, 100m, 100m, [(10, 100.06m)]); // +6 bps: a cost
        ConfirmedOrder(log, "TEST B", "Buy", 10, 100.2m, 100m, 100m, [(10, 99.98m)]); // -2 bps: better than the mid
        Close(log);

        EodExecution live = EodReport.Build(Audit, Monday, TimeProvider.System).Live!;

        Assert.Equal([6.0m, -2.0m], live.Orders.Select(o => o.SlippageVsArrivalBps!.Value));
        Assert.Equal((2.0m, false), (live.MeanSlippageVsArrivalBps, live.WithinAssumption)); // value-weighted: (1000.6×6 − 999.8×2) / 2000.4
    }

    [Fact]
    public void TheConfirmSessionsCloseRecord_BuildsAReport_WithWhateverItCouldRead()
    {
        AuditLog log = Day(Monday);
        Close(log);
        EodReport r = EodReport.Build(Audit, Monday, TimeProvider.System);
        Assert.True(r.Complete);
        Assert.Equal((12_345.67m, 12_400m, 9_000m, 0m), (r.Account!.StartOfDayValue, r.Account.EndValue, r.Account.Cash, r.Account.FeesPaid));
        Assert.Null(r.Live);

        DateOnly tuesday = Monday.AddDays(1);
        Day(tuesday).Append("end-of-day", new { day = new { cards = 0, accountValue = (decimal?)null, startOfDayValue = (decimal?)null } });
        EodReport unread = EodReport.Build(Audit, tuesday, TimeProvider.System);
        Assert.True(unread.Complete);
        Assert.Null(unread.Account);
    }

    [Fact]
    public void AnOrderUnknownAtTheEnd_IsCounted()
    {
        AuditLog log = Day(Monday);
        ConfirmedOrder(log, "ERIC B", "Buy", 10, 100.2m, 100m, 100m, [], end: "Unknown");
        EodReport r = EodReport.Build(Audit, Monday, TimeProvider.System);
        Assert.Equal(1, r.UnknownAtEnd);
        Assert.Contains(r.Violations, v => v.Contains("still Unknown at the end of the day", StringComparison.Ordinal));
    }

    // ---- The Auto gate ------------------------------------------------------------------------------------------

    /// <summary>Confirm days with <paramref name="orders"/> filled buys in all, each at the given slippage vs the arrival mid.</summary>
    private List<EodReport> ConfirmDays(int orders, decimal slippageBps = 5m, int perDay = 5, bool simulated = false, decimal? assumption = 12m)
    {
        var reports = new List<EodReport>();
        DateOnly date = Monday;
        for (int done = 0; done < orders; date = date.AddDays(1))
        {
            AuditLog log = Day(date, assumption);
            for (int i = 0; i < perDay && done < orders; i++, done++)
            {
                ConfirmedOrder(log, "ERIC B", "Buy", 10, 101m, 100m, 100m, [(10, 100m * (1 + (slippageBps / 10_000m)))], simulated: simulated);
            }

            Close(log);
            reports.Add(EodReport.Build(Audit, date, TimeProvider.System));
        }

        return reports;
    }

    private GateResult Gate(IReadOnlyList<EodReport> reports) => PromotionGate.Auto(reports, AuditLog.Verify(Audit));

    [Fact]
    public void TwentyConfirmedOrders_WithinTheAssumption_MeetTheAutoGate()
    {
        GateResult g = Gate(ConfirmDays(20));

        Assert.True(g.Met, string.Join("\n", g.Lines));
        Assert.Equal("[ok] confirmed live orders: 20 (need 20), over 4 Confirm day(s) from 2026-10-12", g.Lines[0]);
        Assert.Equal("[ok] slippage vs the arrival mid: +5.0 bps over 20 filled order(s); the backtest assumes 12 bps (half-spread + slippage)", g.Lines[2]);
        Assert.Equal(4, g.Evidence.Count);
    }

    [Theory]
    [InlineData("19 orders", "[--] confirmed live orders: 19 (need 20)")]
    [InlineData("slippage above the assumption", "[--] slippage vs the arrival mid: +15.0 bps")]
    [InlineData("rehearsals only", "[--] confirmed live orders: 0 (need 20)")]
    [InlineData("no assumption recorded", "the backtest assumes (not recorded)")]
    public void AutoGate_IsNotMet(string label, string line)
    {
        IReadOnlyList<EodReport> reports = label switch
        {
            "19 orders" => ConfirmDays(19),
            "slippage above the assumption" => ConfirmDays(20, slippageBps: 15m),
            "rehearsals only" => ConfirmDays(20, simulated: true),
            _ => ConfirmDays(20, assumption: null),
        };

        GateResult g = Gate(reports);

        Assert.False(g.Met);
        Assert.Contains(g.Lines, l => l.StartsWith(line, StringComparison.Ordinal) || l.Contains(line, StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownAtTheEnd_OrAViolation_KeepsTheGateClosed()
    {
        List<EodReport> reports = [.. ConfirmDays(20)];
        DateOnly extra = reports[^1].Date.AddDays(1);
        AuditLog log = Day(extra);
        ConfirmedOrder(log, "ERIC B", "Buy", 10, 101m, 100m, 100m, [], end: "Unknown");
        reports.Add(EodReport.Build(Audit, extra, TimeProvider.System));

        GateResult g = Gate(reports);

        Assert.False(g.Met);
        Assert.Contains("[--] orders still Unknown at the end of a day: 1 (need 0)", g.Lines);
        Assert.Contains("[--] violations on Confirm days: 1", g.Lines);
    }

    [Fact]
    public void PaperDays_AreNotEvidenceForAuto()
    {
        var log = new AuditLog(Audit, new FakeTimeProvider(new DateTimeOffset(Monday.ToDateTime(new TimeOnly(7, 0)), TimeSpan.Zero)));
        log.Append("session-start", new { mode = "Paper" });
        log.Append("end-of-day", new { day = new { startOfDayValue = 5000m, accountValue = 5001m, cash = 4504m, feesPaid = 0m } });

        GateResult g = Gate([EodReport.Build(Audit, Monday, TimeProvider.System)]);

        Assert.False(g.Met);
        Assert.Empty(g.Evidence);
        Assert.Equal("[--] confirmed live orders: 0 (need 20)", g.Lines[0]);
    }
}
