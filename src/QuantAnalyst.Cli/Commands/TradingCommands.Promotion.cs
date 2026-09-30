using System.CommandLine;
using System.Globalization;
using System.Runtime.Versioning;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Avanza.Credentials;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Calendar;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Trading;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Reports;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Cli.Commands;

internal static partial class TradingCommands
{
    public const string DefaultReportsDir = "reports/eod";
    public const string DefaultWeeksDir = "reports/week";
    public const string DefaultPromotionDir = "promotion";

    // ---- qa report eod | gate --------------------------------------------------------------------------

    private static Command ReportCommand(TimeProvider time)
    {
        var command = new Command("report", "End-of-day reports (ADR 0003 §3/§8), rebuilt from the audit log: orders, risk rejections, fills vs the market's VWAP, reconciliation, violations.");

        var date = new Option<string?>("--date") { Description = "Trading date yyyy-MM-dd (default: today if a session ran, else the latest audit day)" };
        var all = new Option<bool>("--all") { Description = "Rebuild every day in the audit log" };
        var json = new Option<bool>("--json") { Description = "Print the report JSON" };
        var auditDir = AuditDirOption();
        var reportsDir = ReportsDirOption();
        var eod = new Command("eod", "Build (or rebuild) the end-of-day report of a day into reports/eod/YYYY-MM-DD.json and print it.");
        foreach (Option o in new Option[] { date, all, json, auditDir, reportsDir })
        {
            eod.Options.Add(o);
        }

        eod.SetAction(parse => Execute(parse, w =>
        {
            string audit = parse.GetValue(auditDir)!;
            string reports = parse.GetValue(reportsDir)!;
            IReadOnlyList<DateOnly> days = AuditDays(audit);
            IEnumerable<DateOnly> selected = parse.GetValue(all) ? days
                : [DataCommands.ParseDate(parse.GetValue(date), "--date") ?? DefaultDay(days)];
            foreach (DateOnly day in selected)
            {
                EodReport report = EodReport.Build(audit, day, TimeProvider.System);
                string path = report.Save(reports);
                if (parse.GetValue(json))
                {
                    w.WriteLine(File.ReadAllText(path));
                    continue;
                }

                WriteReport(w, report, path);
            }

            // Plan 19: the fill rate over every day so far, the evidence for (or against) changing the limit policy.
            if (!parse.GetValue(json) && days.Count > 1
                && EodFillRate.Combine(days.Select(d => EodReport.Build(audit, d, TimeProvider.System))) is { } allDays)
            {
                w.WriteLine($"All {days.Count} days ({days[0]:yyyy-MM-dd} to {days[^1]:yyyy-MM-dd}): {allDays.Describe()}.");
            }

            return 0;
        }));

        var gateAudit = AuditDirOption();
        var gateReports = ReportsDirOption();
        var gate = new Command("gate", "Rebuild every day's report and show the promotion gates: Paper to Confirm (10 clean Paper days), and Confirm to Auto (20 confirmed live orders, slippage within the backtest's assumption). Read-only.");
        gate.Options.Add(gateAudit);
        gate.Options.Add(gateReports);
        gate.SetAction(parse => Execute(parse, w =>
        {
            (IReadOnlyList<EodReport> reports, _) = RebuildAll(parse.GetValue(gateAudit)!, parse.GetValue(gateReports)!);
            foreach (EodReport r in reports)
            {
                w.WriteLine(r.Summary());
            }

            AuditVerification chain = AuditLog.Verify(parse.GetValue(gateAudit)!);
            GateResult result = PromotionGate.Confirm(reports, chain);
            w.WriteLine();
            w.WriteLine($"Confirm gate: {(result.Met ? "MET" : "not met yet")}");
            foreach (string line in result.Lines)
            {
                w.WriteLine("  " + line);
            }

            GateResult auto = PromotionGate.Auto(reports, chain);
            w.WriteLine();
            w.WriteLine($"Auto gate: {(auto.Met ? "MET" : "not met yet")} (Auto itself arrives in Phase 8)");
            foreach (string line in auto.Lines)
            {
                w.WriteLine("  " + line);
            }

            return 0;
        }));

        command.Subcommands.Add(eod);
        command.Subcommands.Add(gate);
        command.Subcommands.Add(WeekCommand(time));
        return command;
    }

    // ---- qa report week (plan 20) ------------------------------------------------------------------------

    private static Command WeekCommand(TimeProvider time)
    {
        var week = new Option<string?>("--week") { Description = "ISO week, e.g. 2026-W40 (default: the week of the last day a session ran)" };
        var date = new Option<string?>("--date") { Description = "Any day of the week, yyyy-MM-dd" };
        var json = new Option<bool>("--json") { Description = "Print the summary JSON" };
        var auditDir = AuditDirOption();
        var weeksDir = new Option<string>("--weeks-dir") { Description = "Weekly summaries folder", DefaultValueFactory = _ => DefaultWeeksDir };
        var store = DataCommands.StoreOption();
        var configDir = ConfigDirOption();
        var ledger = new Option<string?>("--ledger") { Description = $"Trial ledger (default: <repository>/{TrialLedger.DefaultPath})" };
        var command = new Command(
            "week",
            "The weekly summary (plan 20): the week's Paper days, Paper's return against the saved strategy's recorded backtest (this week and since the start), the limit fill rate, and the intraday collection. Offline; saves reports/week/YYYY-Www.json.");
        foreach (Option o in new Option[] { week, date, json, auditDir, weeksDir, store, configDir, ledger })
        {
            command.Options.Add(o);
        }

        command.SetAction(parse => Execute(parse, w =>
        {
            string audit = parse.GetValue(auditDir)!;
            string config = ResolveConfigDir(parse.GetValue(configDir));
            string storePath = parse.GetValue(store)!;
            IReadOnlyList<DateOnly> days = AuditDays(audit);
            if (parse.GetValue(week) is not null && parse.GetValue(date) is not null)
            {
                throw new ArgumentException("Give --week or --date, not both.");
            }

            DateTimeOffset now = time.GetUtcNow();
            DateOnly today = OrderGateway.StockholmDate(now);
            DateOnly day = parse.GetValue(week) is { } iso ? WeeklyReport.ParseWeek(iso)
                : DataCommands.ParseDate(parse.GetValue(date), "--date") ?? (days.Count > 0 ? days[^1] : today);
            MarketCalendar calendar = MarketCalendarLoader.LoadDirectory(config);
            DateOnly monday = WeeklyReport.MondayOf(day);

            // Up to today: a later day has had no session yet, and today's intraday bars come in the evening.
            DateOnly[] trading = [.. Enumerable.Range(0, 5).Select(monday.AddDays).Where(d => d <= today && calendar.Years.Contains(d.Year) && calendar.Classify(d).IsTradingDay)];

            List<EodReport> reports = [.. days.Select(d => EodReport.Build(audit, d, TimeProvider.System))];
            GateResult gate = PromotionGate.Confirm(reports, AuditLog.Verify(audit));
            WeeklyReport report = WeeklyReport.Build(day, trading, reports, gate, Expectation(config, parse.GetValue(ledger)),
                IntradayWeek(storePath, config, [.. trading.Where(d => d < today)], out int? needed), needed, now);
            string path = report.Save(parse.GetValue(weeksDir)!);
            if (parse.GetValue(json))
            {
                w.WriteLine(File.ReadAllText(path));
                return 0;
            }

            foreach (string line in report.Lines())
            {
                w.WriteLine(line);
            }

            w.WriteLine($"  saved to {path}");
            return 0;
        }));
        return command;
    }

    /// <summary>The saved strategy's recorded backtest, on today's allowlist if there is one (plan 20); null without either.</summary>
    private static BacktestExpectation? Expectation(string configDir, string? ledgerPath)
    {
        if (PaperConfig.Load(Path.Combine(configDir, PaperConfig.FileName)).Strategy is not { } saved)
        {
            return null;
        }

        var ledger = new TrialLedger(BacktestCommands.ResolveLedger(ledgerPath));
        if (!File.Exists(ledger.Path))
        {
            return null;
        }

        StrategySpec spec = StrategyCatalog.Create(saved.Name, saved.Parameters).Spec;
        string[] allowlist = [.. Universe.Load(Path.Combine(configDir, Universe.FileName)).Entries.Select(e => e.Ticker)];
        return BacktestExpectation.Find(ledger.ReadAll(), spec, allowlist);
    }

    /// <summary>
    /// The research shares' intraday bars over the week's trading days, and the days the go/no-go needs (plan 17: 120
    /// before the intraday holdout's days). Null before any collection.
    /// </summary>
    private static IntradayCoverage? IntradayWeek(string storePath, string configDir, IReadOnlyList<DateOnly> trading, out int? needed)
    {
        needed = null;
        if (!File.Exists(storePath))
        {
            return null;
        }

        IReadOnlyList<IntradayName> names = AvanzaCommands.CollectedShares(storePath, configDir, TextWriter.Null);
        IntradayCoverage coverage;
        using (HistoryStore history = HistoryStore.Open(storePath))
        {
            coverage = IntradayCoverage.Measure(history, names.Select(n => (n.Id, n.Ticker)), trading, AvanzaChartImporter.AvanzaPriceChart.Name);
        }

        if (coverage.CollectedDays == 0 && names.Count == 0)
        {
            return null;
        }

        try
        {
            needed = IntradayReport.MinDays + IntradayHoldout.Load(Path.Combine(configDir, IntradayHoldout.FileName)).Days;
        }
        catch (BacktestConfigException)
        {
            // Without the holdout policy the need is unknown; the line says only what was collected.
        }

        return coverage;
    }

    // ---- qa promote (the owner's command; Claude is blocked by hook rule 6 and the settings deny rules) ---------

    private static Command PromoteCommand(AvanzaCliServices services)
    {
        var to = new Option<string?>("--to") { Description = "Target mode: Confirm (from Paper), or a lower mode to demote" };
        var initKey = new Option<bool>("--init-key") { Description = "Create your promotion key in Windows Credential Manager (once)" };
        var verify = new Option<bool>("--verify") { Description = "Check the promotion state: signatures, mode chain, evidence files" };
        var op = new Option<string>("--operator") { Description = "Who promotes (stored in the record)", DefaultValueFactory = _ => Environment.UserName };
        var auditDir = AuditDirOption();
        var reportsDir = ReportsDirOption();
        var promotionDir = new Option<string>("--promotion-dir") { Description = "Promotion state folder", DefaultValueFactory = _ => DefaultPromotionDir };
        var command = new Command(
            "promote",
            "YOUR command (ADR 0003 §3): raise or lower the highest allowed trading mode. Checks the gate from the audit-built end-of-day reports, asks you to type the mode, and writes an HMAC-signed record to promotion/state.json.");
        foreach (Option o in new Option[] { to, initKey, verify, op, auditDir, reportsDir, promotionDir })
        {
            command.Options.Add(o);
        }

        command.SetAction(parse => Execute(parse, w =>
        {
            IPromotionKeyStore keys = services.PromotionKeys();
            string promotion = parse.GetValue(promotionDir)!;
            int modes = (parse.GetValue(initKey) ? 1 : 0) + (parse.GetValue(verify) ? 1 : 0) + (parse.GetValue(to) is null ? 0 : 1);
            if (modes != 1)
            {
                throw new ArgumentException("Use exactly one of --to <mode>, --verify or --init-key.");
            }

            if (parse.GetValue(initKey))
            {
                if (keys.Read() is not null)
                {
                    throw new ArgumentException($"A promotion key already exists in {keys.Name}. Replacing it would invalidate every record; delete it yourself only if you mean to start over.");
                }

                keys.Create(Promotion.NewKey());
                w.WriteLine($"Created your promotion key in {keys.Name}. It is never shown or written anywhere else.");
                return 0;
            }

            byte[] key = keys.Read() ?? throw new ArgumentException($"No promotion key in {keys.Name}. Create it once with --init-key.");
            PromotionVerification state = Promotion.Verify(promotion, key, Environment.CurrentDirectory);
            if (parse.GetValue(verify) || !state.Valid)
            {
                w.WriteLine(state.Valid
                    ? $"Promotion state OK: maxAllowed {state.MaxAllowed}, {state.Records} signed record(s)."
                    : $"Promotion state INVALID (maxAllowed {state.MaxAllowed}, {state.Records} record(s)):");
                foreach (string problem in state.Problems)
                {
                    w.WriteLine("  " + problem);
                }

                return state.Valid ? 0 : 1;
            }

            TradingMode target = Enum.TryParse(parse.GetValue(to), ignoreCase: true, out TradingMode m) && Enum.IsDefined(m)
                ? m
                : throw new ArgumentException($"--to: '{parse.GetValue(to)}' is not Backtest, Paper, Confirm or Auto.");
            TradingMode from = state.MaxAllowed;
            if (target == from)
            {
                w.WriteLine($"The highest allowed mode is already {from}.");
                return 0;
            }

            IReadOnlyList<string> gateLines;
            IReadOnlyList<PromotionEvidence> evidence;
            if (target > from)
            {
                if (target != from + 1)
                {
                    throw new ArgumentException($"Promote one step at a time: {from} can go to {from + 1} only.");
                }

                if (target != TradingMode.Confirm)
                {
                    throw new ArgumentException($"{target} mode arrives in Phase 8. Its gate (ADR 0003 §3: 20 confirmed live orders, slippage within the backtest's assumption) is in 'qa report gate' already.");
                }

                (IReadOnlyList<EodReport> reports, IReadOnlyDictionary<DateOnly, string> paths) = RebuildAll(parse.GetValue(auditDir)!, parse.GetValue(reportsDir)!);
                GateResult gate = PromotionGate.Confirm(reports, AuditLog.Verify(parse.GetValue(auditDir)!));
                w.WriteLine($"Gate for {from} to {target}:");
                foreach (string line in gate.Lines)
                {
                    w.WriteLine("  " + line);
                }

                if (!gate.Met)
                {
                    w.WriteLine("Not met: nothing was written.");
                    return 1;
                }

                gateLines = gate.Lines;
                evidence = [.. gate.Evidence.Select(r => new PromotionEvidence(Relative(paths[r.Date]), Promotion.Sha256File(paths[r.Date])))];
            }
            else
            {
                gateLines = [$"demotion from {from} to {target}: lowering the mode needs no gate"];
                evidence = [];
                w.WriteLine(gateLines[0] + ".");
            }

            string verb = target > from ? "promote" : "demote";
            w.Write($"Type {target} to {verb} (anything else cancels): ");
            w.Flush();
            string? typed = services.Input.ReadLine()?.Trim();
            w.WriteLine();
            if (!string.Equals(typed, target.ToString(), StringComparison.Ordinal))
            {
                w.WriteLine("Cancelled: nothing was written.");
                return 1;
            }

            var record = new PromotionRecord(target.ToString(), from.ToString(), services.Time.GetUtcNow(), parse.GetValue(op)!, evidence,
                Promotion.EvidenceHash(evidence), gateLines, string.Empty);
            Promotion.Append(promotion, Promotion.Sign(record, key));
            PromotionVerification after = Promotion.Verify(promotion, key, Environment.CurrentDirectory);
            w.WriteLine(after.Valid
                ? $"Done: the highest allowed mode is now {after.MaxAllowed} ({after.Records} signed record(s) in {Path.Combine(promotion, PromotionState.StateFile)})."
                : "The record was written but does not verify: " + string.Join("; ", after.Problems));
            if (after.Valid && target == TradingMode.Confirm)
            {
                w.WriteLine("Every Confirm session still runs the startup checks first (the signed promotion, verified constants, kill switch, account, order format) and refuses to start if any fails.");
            }

            return after.Valid ? 0 : 1;
        }));
        return command;
    }

    private static Option<string> AuditDirOption() =>
        new("--audit-dir") { Description = "Audit folder", DefaultValueFactory = _ => DefaultAuditDir };

    private static Option<string> ReportsDirOption() =>
        new("--reports-dir") { Description = "End-of-day reports folder", DefaultValueFactory = _ => DefaultReportsDir };

    private static IReadOnlyList<DateOnly> AuditDays(string auditDir) =>
        Directory.Exists(auditDir)
            ? [.. Directory.EnumerateFiles(auditDir, "*.jsonl")
                .Select(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) ? d : (DateOnly?)null)
                .OfType<DateOnly>()
                .Order()]
            : throw new ArgumentException($"No audit folder at '{auditDir}': no session has run yet.");

    private static DateOnly DefaultDay(IReadOnlyList<DateOnly> days)
    {
        DateOnly today = OrderGateway.StockholmDate(DateTimeOffset.UtcNow);
        return days.Contains(today) ? today : days.Count > 0 ? days[^1] : throw new ArgumentException("The audit folder is empty: no session has run yet.");
    }

    /// <summary>Rebuilds every day's report from the audit log (the promotion gate never trusts report files as they are).</summary>
    private static (IReadOnlyList<EodReport> Reports, IReadOnlyDictionary<DateOnly, string> Paths) RebuildAll(string auditDir, string reportsDir)
    {
        var reports = new List<EodReport>();
        var paths = new Dictionary<DateOnly, string>();
        foreach (DateOnly day in AuditDays(auditDir))
        {
            EodReport r = EodReport.Build(auditDir, day, TimeProvider.System);
            paths[day] = r.Save(reportsDir);
            reports.Add(r);
        }

        return (reports, paths);
    }

    internal static void WriteReport(TextWriter w, EodReport r, string path)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        w.WriteLine(r.Summary());
        foreach (EodLiveOrder o in r.Live?.Orders ?? [])
        {
            string fill = o.AverageFillPrice is { } avg ? string.Create(c, $"filled {o.Filled}/{o.Volume} @ {avg:0.####}") : $"filled 0/{o.Volume}";
            string vsDecision = o.SlippageVsDecisionBps is { } d ? string.Create(c, $"{d:+0.0;-0.0} bps vs the decision") : "no decision price";
            string vsArrival = o.SlippageVsArrivalBps is { } a ? string.Create(c, $"{a:+0.0;-0.0} bps vs the arrival mid") : "no arrival mid";
            string fees = string.Create(c, $"fee Avanza {(o.AvanzaFee is { } f ? f.ToString("N2", c) : "-")} / model {o.ModelFee:N2} SEK");
            w.WriteLine($"  live {o.Side} {o.Ticker} (limit {o.Limit.ToString("0.####", c)}, {o.State}{(o.Simulated ? ", rehearsal" : string.Empty)}): {fill}; {vsDecision}, {vsArrival}; {fees}");
        }

        foreach (EodFill f in r.Fills)
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  fill {f.Side} {f.Volume} {f.Ticker} @ {f.Price} (limit {f.Limit}, {f.How}); {f.ReferenceKind} {(f.Reference is { } x ? x.ToString("0.####", CultureInfo.InvariantCulture) : "-")}, {(f.DeviationBps is { } d ? $"{d:+0.0;-0.0} bps" : "no reference")}"));
        }

        foreach (EodLimitOrder o in r.FillRate?.Orders ?? [])
        {
            w.WriteLine("  limit " + o.Describe());
        }

        foreach (string v in r.Violations)
        {
            w.WriteLine("  VIOLATION: " + v);
        }

        foreach (string e in r.Events)
        {
            w.WriteLine("  event: " + e);
        }

        w.WriteLine($"  saved to {path}");
    }

    private static string Relative(string path) => Path.GetRelativePath(Environment.CurrentDirectory, path).Replace('\\', '/');
}

internal static class PromotionKeyStores
{
    /// <summary>The owner's key store: Windows Credential Manager only (ADR 0003 §3).</summary>
    public static IPromotionKeyStore Default() =>
        OperatingSystem.IsWindows()
            ? new CredentialManagerPromotionKeyStore()
            : throw new ArgumentException("The promotion key lives in Windows Credential Manager (ADR 0003 §3); run this on your Windows machine.");
}

/// <summary>The promotion key in Windows Credential Manager, as base64 in the generic credential <see cref="Target"/>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class CredentialManagerPromotionKeyStore : IPromotionKeyStore
{
    public const string Target = "QuantAnalyst:Promotion";

    public string Name => $"Windows Credential Manager ({Target})";

    public byte[]? Read() => WindowsCredentialStore.ReadSecret(Target) is { } s ? Convert.FromBase64String(s.Reveal()) : null;

    public void Create(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Read() is not null)
        {
            throw new InvalidOperationException($"{Target} exists already.");
        }

        WindowsCredentialStore.Write(Target, "owner", new Secret(Convert.ToBase64String(key)));
    }
}
