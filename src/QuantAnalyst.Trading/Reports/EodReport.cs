using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantAnalyst.Trading.Audit;

namespace QuantAnalyst.Trading.Reports;

/// <summary>One paper fill as the report checks it: against the market's VWAP over the fill window, or the arrival price.</summary>
/// <param name="DeviationBps">How much worse than the reference the fill was, in basis points (negative = better).</param>
public sealed record EodFill(string Ticker, string Side, long Volume, decimal Price, decimal Limit, string How, decimal? Reference, string ReferenceKind, decimal? DeviationBps);

public sealed record EodAccount(decimal StartOfDayValue, decimal EndValue, decimal Pnl, decimal PnlPct, decimal Cash, decimal FeesPaid);

/// <summary>
/// One order sent in Confirm mode (plan 07 step 6): its fills against the decision price and the mid when it was sent,
/// and Avanza's quoted fee against the model's. Slippage is in basis points, signed so positive is a cost.
/// </summary>
/// <param name="Simulated">True for a rehearsal on a simulated channel; only real orders count towards the Auto gate.</param>
/// <param name="FeesBooked">The fees booked with the fills (from Avanza's deals once they are modelled, plan 07 step 7).</param>
public sealed record EodLiveOrder(
    string Ticker,
    string Side,
    long Volume,
    long Filled,
    decimal Limit,
    decimal? AverageFillPrice,
    decimal? DecisionPrice,
    decimal? ArrivalMid,
    decimal? SlippageVsDecisionBps,
    decimal? SlippageVsArrivalBps,
    decimal? AvanzaFee,
    decimal ModelFee,
    decimal FeesBooked,
    string State,
    bool Simulated);

/// <summary>
/// The day's live execution quality (ADR 0003 §7 "Report"; plan 07 step 6): every Confirm order, and the value-weighted
/// mean slippage of the filled ones against the backtest's cost assumption (half-spread + slippage from the cost file,
/// recorded by the session). It feeds the Auto gate.
/// </summary>
public sealed record EodExecution(
    IReadOnlyList<EodLiveOrder> Orders,
    int Sent,
    int Filled,
    decimal? MeanSlippageVsArrivalBps,
    decimal? MeanSlippageVsDecisionBps,
    decimal? CostAssumptionBps,
    bool? WithinAssumption,
    decimal AvanzaFees,
    decimal ModelFees,
    int FeeFlags)
{
    /// <summary>The value-weighted mean of <paramref name="bps"/> over the filled orders that have it, rounded to 0.1 bps.</summary>
    public static decimal? WeightedMean(IEnumerable<EodLiveOrder> orders, Func<EodLiveOrder, decimal?> bps)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(bps);
        (decimal Weight, decimal Bps)[] points = [.. orders.Where(o => o.AverageFillPrice is not null && bps(o) is not null).Select(o => (o.Filled * o.AverageFillPrice!.Value, bps(o)!.Value))];
        decimal total = points.Sum(p => p.Weight);
        return total > 0 ? decimal.Round(points.Sum(p => p.Weight * p.Bps) / total, 1) : null;
    }
}

/// <summary>
/// The end-of-day report (ADR 0003 §3 and §8). It is rebuilt from the day's audit file alone, the tamper-evident
/// record, never from session memory, so it can be regenerated and checked at any time (<c>qa report eod</c>) and the
/// promotion gate can trust it.
/// <para><b>Violations</b> mean the system misbehaved or was unsafe: an OMS invariant or reconciliation halt, schema drift or a
/// gone endpoint, a refused fill, a fill outside its limit, an order sent without a passing risk check, an order still
/// Unknown at the end of the day, an automatic kill (other than the daily loss stop), a broken audit chain.</para>
/// <para><b>Events</b> are worth knowing but are the rules working: risk rejections, a manual kill, the daily loss stop.</para>
/// </summary>
public sealed record EodReport
{
    public const string Format = "qa-eod-report/1";

    /// <summary>A fill more than this far from its reference (the R5 price collar) fails the fill sanity check.</summary>
    public const decimal SanityLimitBps = 200m;

    public string ReportFormat { get; init; } = Format;

    public required DateOnly Date { get; init; }

    public required IReadOnlyList<string> Modes { get; init; }

    /// <summary>Gets a value indicating whether the session reached the close (an <c>end-of-day</c> record exists).</summary>
    public required bool Complete { get; init; }

    public required int Sessions { get; init; }

    public required int Decisions { get; init; }

    public required int Intents { get; init; }

    public required int Submitted { get; init; }

    public required int Accepted { get; init; }

    public required int BrokerRejected { get; init; }

    public required int Unknown { get; init; }

    public required int RiskRejected { get; init; }

    /// <summary>Gets the failed risk checks, by id, with counts.</summary>
    public required IReadOnlyDictionary<string, int> RiskRejectionsByCheck { get; init; }

    public required IReadOnlyList<EodFill> Fills { get; init; }

    public required int FillSanityOutliers { get; init; }

    public required int ReconciliationRuns { get; init; }

    public required int ReconciliationMismatchRuns { get; init; }

    public required bool ReconciliationCleanAtEnd { get; init; }

    public required EodAccount? Account { get; init; }

    /// <summary>Gets the Confirm orders and their execution quality, or null on a day without any.</summary>
    public EodExecution? Live { get; init; }

    /// <summary>Gets how many orders were still Unknown when the day's audit ended (each is also a violation).</summary>
    public int UnknownAtEnd { get; init; }

    public required bool AuditChainValid { get; init; }

    public required IReadOnlyList<string> Violations { get; init; }

    public required IReadOnlyList<string> Events { get; init; }

    public required string AuditFile { get; init; }

    public required DateTimeOffset GeneratedUtc { get; init; }

    /// <summary>Gets a value indicating whether the day counts towards the Confirm gate: complete, Paper, no violations, reconciliation always matched, fills sane.</summary>
    [JsonIgnore]
    public bool Clean => Complete && Violations.Count == 0 && ReconciliationMismatchRuns == 0 && FillSanityOutliers == 0;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string FileName(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json";

    /// <summary>Writes <c>&lt;dir&gt;/YYYY-MM-DD.json</c> (replacing an earlier build of the same day) and returns its path.</summary>
    public string Save(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName(Date));
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + "\n");
        return path;
    }

    public static EodReport Load(string path) =>
        JsonSerializer.Deserialize<EodReport>(File.ReadAllText(path), Json) ?? throw new JsonException($"{path} is empty.");

    /// <summary>Builds the report of one Stockholm trading date from <c>&lt;auditDir&gt;/YYYY-MM-DD.jsonl</c>.</summary>
    public static EodReport Build(string auditDirectory, DateOnly date, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        string file = Path.Combine(auditDirectory, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"No audit file for {date:yyyy-MM-dd} ({file}): no session ran that day.", file);
        }

        AuditVerification chain = AuditLog.Verify(auditDirectory);
        var b = new Builder();
        foreach (JsonElement record in AuditLog.Read(file))
        {
            b.Add(record.GetProperty("kind").GetString() ?? string.Empty, record.TryGetProperty("data", out JsonElement d) ? d : default);
        }

        if (!chain.Valid)
        {
            b.Violations.Add($"audit chain broken: {chain.Problem}");
        }

        return b.Finish(date, chain.Valid, Path.GetFileName(file), time.GetUtcNow());
    }

    /// <summary>One line for the console.</summary>
    public string Summary()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string account = Account is { } a
            ? string.Create(c, $"value {a.EndValue:N2} SEK ({a.PnlPct:+0.00%;-0.00%}), fees {a.FeesPaid:N2}")
            : "no end-of-day values";
        string state = Clean ? "CLEAN" : !Complete ? "INCOMPLETE" : "NOT CLEAN";
        string live = Live is { } l
            ? string.Create(c, $" Live: {l.Sent} confirmed order(s), {l.Filled} filled{(l.MeanSlippageVsArrivalBps is { } m ? $", slippage {m:+0.0;-0.0} bps vs the arrival mid" : string.Empty)}{(l.CostAssumptionBps is { } assumed ? $" (the backtest assumes {assumed:0.#})" : string.Empty)}.")
            : string.Empty;
        return string.Create(c,
            $"{Date:yyyy-MM-dd} {state}: {Submitted} sent, {Accepted} accepted, {RiskRejected} risk-rejected, {Fills.Count} fill(s) ({FillSanityOutliers} outside ±{SanityLimitBps:0} bps), reconciliation {ReconciliationRuns - ReconciliationMismatchRuns}/{ReconciliationRuns} clean, {Violations.Count} violation(s); {account}.{live}");
    }

    private sealed class Builder
    {
        private readonly Dictionary<string, decimal> _limits = [];
        private readonly Dictionary<string, string> _lastState = [];
        private readonly Dictionary<string, string> _tickers = [];
        private readonly SortedDictionary<string, int> _rejections = new(StringComparer.Ordinal);
        private readonly List<EodFill> _fills = [];
        private readonly HashSet<string> _modes = [];
        private bool? _lastRiskPassed;
        private bool _complete;
        private int _sessions, _decisions, _intents, _submitted, _accepted, _brokerRejected, _unknown, _riskRejected, _reconRuns, _reconMismatch, _skipped;
        private bool _reconCleanAtEnd = true;
        private EodAccount? _account;
        private readonly Dictionary<string, LiveOrder> _live = [];
        private LiveOrder? _pendingLive;
        private decimal? _costAssumption;

        public List<string> Violations { get; } = [];

        public List<string> Events { get; } = [];

        public void Add(string kind, JsonElement d)
        {
            switch (kind)
            {
                case "gateway-start":
                case "session-start":
                    if (Num(d, "costAssumptionBps") is { } assumed)
                    {
                        _costAssumption = _costAssumption is { } seen ? Math.Min(seen, assumed) : assumed;
                    }

                    if (Str(d, "mode") is { } mode)
                    {
                        _modes.Add(mode);
                    }

                    if (kind == "session-start")
                    {
                        _sessions++;
                    }

                    break;
                case "decision":
                case "decision-skipped":
                case "decision-failed":
                    _decisions++;
                    if (kind == "decision-failed")
                    {
                        Events.Add("decision failed: " + Str(d, "reason"));
                    }

                    break;
                case "intent":
                    _intents++;
                    break;
                case "risk":
                    _lastRiskPassed = d.GetProperty("passed").GetBoolean();
                    if (_lastRiskPassed == false)
                    {
                        _riskRejected++;
                        foreach (JsonElement check in d.GetProperty("checks").EnumerateArray().Where(x => !x.GetProperty("passed").GetBoolean()))
                        {
                            string id = check.GetProperty("id").GetString()!;
                            _rejections[id] = _rejections.GetValueOrDefault(id) + 1;
                        }
                    }

                    break;
                case "recheck":
                    // Confirm: the re-check after the typed answer decides whether the order is created. A failed
                    // re-check is a skip (audited as confirm-skip), not a risk rejection.
                    _lastRiskPassed = d.GetProperty("passed").GetBoolean();
                    break;
                case "confirm-skip":
                    _skipped++;
                    _lastRiskPassed = null;
                    break;
                case "account-unavailable":
                    Events.Add("account state unavailable: " + Str(d, "message"));
                    break;
                case "gate":
                    _pendingLive = Str(d, "decision") == "confirmed"
                        ? new LiveOrder
                        {
                            Simulated = d.TryGetProperty("simulated", out JsonElement sim) && sim.ValueKind == JsonValueKind.True,
                            DecisionPrice = Num(d, "decisionPrice"),
                            ArrivalMid = Num(d, "arrivalMid"),
                            AvanzaFee = Num(d, "avanzaFee"),
                            ModelFee = Num(d, "modelFee") ?? 0m,
                        }
                        : null;
                    break;
                case "oms-new":
                    string newId = Str(d, "clientOrderId")!;
                    _limits[newId] = d.GetProperty("limitPrice").GetDecimal();
                    _tickers[newId] = Str(d, "ticker") ?? "?";
                    if (_pendingLive is { } live)
                    {
                        _live[newId] = live with
                        {
                            Ticker = _tickers[newId],
                            Side = Str(d, "side") ?? "?",
                            Volume = d.GetProperty("volume").GetInt64(),
                            Limit = _limits[newId],
                        };
                        _pendingLive = null;
                    }

                    if (_lastRiskPassed != true)
                    {
                        Violations.Add($"{_tickers[newId]}: order {newId} created without a passing risk check just before it");
                    }

                    _lastRiskPassed = null;
                    break;
                case "submit":
                    _submitted++;
                    break;
                case "submit-result":
                    switch (Str(d, "outcome"))
                    {
                        case "Accepted":
                            _accepted++;
                            break;
                        case "Rejected":
                            _brokerRejected++;
                            break;
                        default:
                            _unknown++;
                            break;
                    }

                    break;
                case "oms-state":
                    _lastState[Str(d, "clientOrderId")!] = Str(d, "to")!;
                    break;
                case "oms-fill":
                    string filledId = Str(d, "clientOrderId")!;
                    _lastState[filledId] = Str(d, "to")!;
                    if (_live.TryGetValue(filledId, out LiveOrder? order))
                    {
                        order.Filled += d.GetProperty("volume").GetInt64();
                        order.Value += Num(d, "value") ?? 0m;
                        order.Fees += Num(d, "fees") ?? 0m;
                    }

                    break;
                case "sim-fill":
                    AddFill(d);
                    break;
                case "fill-refused":
                    Violations.Add("fill refused by the OMS: " + Str(d, "reason"));
                    break;
                case "halt":
                    string reason = Str(d, "reason") ?? "?";
                    string detail = Str(d, "detail") ?? string.Empty;
                    if (reason is "OmsInvariant" or "Reconciliation" or "SchemaDrift" or "EndpointGone")
                    {
                        Violations.Add($"halt {reason}: {detail}");
                    }
                    else if (reason != "KillSwitch")
                    {
                        Events.Add($"halt {reason}: {detail}");
                    }

                    break;
                case "kill":
                    string source = Str(d, "source") ?? "?";
                    string why = Str(d, "reason") ?? string.Empty;
                    if (source == "automatic" && !why.StartsWith("daily loss stop", StringComparison.Ordinal))
                    {
                        Violations.Add("kill switch (automatic): " + why);
                    }
                    else
                    {
                        Events.Add($"kill switch ({source}): {why}");
                    }

                    break;
                case "reconcile":
                    _reconRuns++;
                    bool mismatch = d.GetProperty("mismatches").GetArrayLength() > 0;
                    if (mismatch)
                    {
                        _reconMismatch++;
                    }

                    _reconCleanAtEnd = !mismatch;
                    break;
                case "end-of-day":
                    _complete = true;

                    // Paper books every value; a Confirm session records what it could read from the live account.
                    JsonElement day = d.GetProperty("day");
                    if (Num(day, "startOfDayValue") is { } start && Num(day, "accountValue") is { } end)
                    {
                        _account = new EodAccount(start, end, end - start, start > 0 ? decimal.Round((end - start) / start, 6) : 0m,
                            Num(day, "cash") ?? 0m, Num(day, "feesPaid") ?? 0m);
                    }

                    break;
            }
        }

        public EodReport Finish(DateOnly date, bool chainValid, string auditFile, DateTimeOffset now)
        {
            int unknownAtEnd = 0;
            foreach ((string id, string state) in _lastState.Where(s => s.Value == "Unknown"))
            {
                unknownAtEnd++;
                Violations.Add($"{_tickers.GetValueOrDefault(id, "?")}: order {id} is still Unknown at the end of the day");
            }

            if (_rejections.Count > 0)
            {
                Events.Add("risk rejections: " + string.Join(", ", _rejections.Select(r => $"{r.Key}×{r.Value}")));
            }

            if (_skipped > 0)
            {
                Events.Add($"{_skipped} order card(s) skipped at the confirmation (not rejections)");
            }

            return new EodReport
            {
                Date = date,
                Modes = [.. _modes.Order(StringComparer.Ordinal)],
                Complete = _complete,
                Sessions = _sessions,
                Decisions = _decisions,
                Intents = _intents,
                Submitted = _submitted,
                Accepted = _accepted,
                BrokerRejected = _brokerRejected,
                Unknown = _unknown,
                RiskRejected = _riskRejected,
                RiskRejectionsByCheck = new Dictionary<string, int>(_rejections),
                Fills = _fills,
                FillSanityOutliers = _fills.Count(f => f.DeviationBps is { } dev && Math.Abs(dev) > SanityLimitBps),
                ReconciliationRuns = _reconRuns,
                ReconciliationMismatchRuns = _reconMismatch,
                ReconciliationCleanAtEnd = _reconCleanAtEnd,
                Account = _account,
                Live = _live.Count == 0 ? null : Execution(),
                UnknownAtEnd = unknownAtEnd,
                AuditChainValid = chainValid,
                Violations = Violations,
                Events = Events,
                AuditFile = auditFile,
                GeneratedUtc = now,
            };
        }

        private EodExecution Execution()
        {
            List<EodLiveOrder> orders = [.. _live.Select(kv => kv.Value.ToReport(_lastState.GetValueOrDefault(kv.Key, "?")))];
            EodLiveOrder[] filled = [.. orders.Where(o => o.Filled > 0 && o.AverageFillPrice is not null)];
            decimal? mean = EodExecution.WeightedMean(filled, o => o.SlippageVsArrivalBps);
            return new EodExecution(
                orders,
                orders.Count,
                filled.Length,
                mean,
                EodExecution.WeightedMean(filled, o => o.SlippageVsDecisionBps),
                _costAssumption,
                mean is { } m && _costAssumption is { } a ? m <= a : null,
                orders.Sum(o => o.AvanzaFee ?? 0m),
                orders.Sum(o => o.ModelFee),
                orders.Count(o => o.AvanzaFee is { } fee && Math.Abs(fee - o.ModelFee) > Pipeline.FeeComparison.FlagAboveSek));
        }

        private void AddFill(JsonElement d)
        {
            string side = Str(d, "side") ?? "?";
            decimal price = d.GetProperty("price").GetDecimal();
            decimal limit = d.GetProperty("limit").GetDecimal();
            string ticker = Str(d, "ticker") ?? "?";
            if ((side == "Buy" && price > limit) || (side == "Sell" && price < limit))
            {
                Violations.Add(string.Create(CultureInfo.InvariantCulture, $"{ticker}: {side} filled at {price}, outside its limit {limit}"));
            }

            decimal? vwap = Num(d, "windowVwap");
            decimal? arrival = Num(d, "arrivalPrice");
            (decimal? reference, string kind) = vwap is { } v ? (v, "window VWAP") : arrival is { } a ? (a, "arrival") : ((decimal?)null, "none");
            decimal? deviation = reference is { } r && r > 0
                ? decimal.Round((side == "Buy" ? price - r : r - price) / r * 10_000m, 1)
                : null;
            _fills.Add(new EodFill(ticker, side, d.GetProperty("volume").GetInt64(), price, limit, Str(d, "how") ?? string.Empty, reference, kind, deviation));
        }

        /// <summary>A Confirm order while its day's audit is read.</summary>
        private sealed record LiveOrder
        {
            public string Ticker { get; init; } = "?";

            public string Side { get; init; } = "?";

            public long Volume { get; init; }

            public decimal Limit { get; init; }

            public bool Simulated { get; init; }

            public decimal? DecisionPrice { get; init; }

            public decimal? ArrivalMid { get; init; }

            public decimal? AvanzaFee { get; init; }

            public decimal ModelFee { get; init; }

            public long Filled { get; set; }

            public decimal Value { get; set; }

            public decimal Fees { get; set; }

            public EodLiveOrder ToReport(string state)
            {
                decimal? average = Filled > 0 ? decimal.Round(Value / Filled, 6) : null;
                return new EodLiveOrder(Ticker, Side, Volume, Filled, Limit, average, DecisionPrice, ArrivalMid,
                    Slippage(average, DecisionPrice), Slippage(average, ArrivalMid), AvanzaFee, ModelFee, Fees, state, Simulated);
            }

            // Positive is a cost: a buy above, or a sell below, the reference.
            private decimal? Slippage(decimal? fill, decimal? reference) =>
                fill is { } f && reference is { } r && r > 0 ? decimal.Round((Side == "Buy" ? f - r : r - f) / r * 10_000m, 1) : null;
        }

        private static string? Str(JsonElement d, string name) =>
            d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static decimal? Num(JsonElement d, string name) =>
            d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
    }
}
