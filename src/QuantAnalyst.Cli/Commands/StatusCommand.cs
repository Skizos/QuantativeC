using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Calendar;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Native;
using QuantAnalyst.Trading;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Reports;
using QuantAnalyst.Trading.Risk;
using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Cli.Commands;

/// <summary>
/// <c>qa status</c>: one look at everything a trading day depends on, each line marked ok / todo / warn / FAIL, then the
/// next steps in the order to take them. Offline and read-only: no login, no network, nothing written.
/// </summary>
internal static class StatusCommand
{
    internal enum Mark
    {
        Ok,
        Todo,
        Warn,
        Fail,
    }

    public static Command Create(AvanzaCliServices services)
    {
        var configDir = TradingCommands.ConfigDirOption();
        var store = DataCommands.StoreOption();
        var stateDir = new Option<string>("--state-dir") { Description = "State folder", DefaultValueFactory = _ => TradingCommands.DefaultStateDir };
        var auditDir = new Option<string>("--audit-dir") { Description = "Audit folder", DefaultValueFactory = _ => TradingCommands.DefaultAuditDir };
        var killFile = new Option<string>("--kill-file") { Description = "The kill flag file", DefaultValueFactory = _ => TradingCommands.DefaultKillFile };
        var promotionDir = new Option<string>("--promotion-dir") { Description = "Promotion state folder", DefaultValueFactory = _ => TradingCommands.DefaultPromotionDir };
        var ledger = new Option<string?>("--ledger") { Description = $"Trial ledger (default: <repository>/{TrialLedger.DefaultPath})" };
        var command = new Command("status", "Start here: what is set up, what is missing, and the next steps. Offline and read-only.");
        foreach (Option o in new Option[] { configDir, store, stateDir, auditDir, killFile, promotionDir, ledger })
        {
            command.Options.Add(o);
        }

        command.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            var paths = new StatusPaths(
                parse.GetValue(configDir), parse.GetValue(store)!, parse.GetValue(stateDir)!, parse.GetValue(auditDir)!, parse.GetValue(killFile)!,
                parse.GetValue(promotionDir)!, parse.GetValue(ledger));
            Write(w, Build(paths, services.Time, services.GetVariable, services.PromotionKeys));
            return 0;
        }));
        return command;
    }

    internal sealed record StatusPaths(string? ConfigDir, string Store, string StateDir, string AuditDir, string KillFile, string PromotionDir, string? Ledger);

    internal sealed class StatusReport(DateTimeOffset now)
    {
        private readonly List<(int Priority, string Text)> _steps = [];

        public DateTimeOffset Now { get; } = now;

        public List<(Mark Mark, string Label, string Text)> Lines { get; } = [];

        /// <summary>Gets the next steps, most urgent first.</summary>
        public IReadOnlyList<string> Steps => [.. _steps.OrderBy(s => s.Priority).Select(s => s.Text).Distinct()];

        public bool Blocked => _steps.Any(s => s.Priority < Priority.Session);

        public void Add(Mark mark, string label, string text) => Lines.Add((mark, label, text));

        public void Step(int priority, string text) => _steps.Add((priority, text));
    }

    /// <summary>Step priorities: everything below <see cref="Session"/> must be fixed before a session is worth starting.</summary>
    private static class Priority
    {
        public const int Native = 0;
        public const int Config = 1;
        public const int Kill = 2;
        public const int Audit = 3;
        public const int Allowlist = 4;
        public const int Strategy = 5;
        public const int Session = 6;
        public const int Report = 7;
        public const int Gate = 8;
        public const int Advice = 9;
    }

    /// <param name="getVariable">Environment variables (the Confirm checks); the real environment when null.</param>
    /// <param name="promotionKeys">The owner's promotion key store (the Confirm checks); Windows Credential Manager when null.</param>
    internal static StatusReport Build(StatusPaths paths, TimeProvider time, Func<string, string?>? getVariable = null, Func<IPromotionKeyStore>? promotionKeys = null)
    {
        var r = new StatusReport(time.GetUtcNow());
        CheckNative(r);

        string configDir;
        try
        {
            configDir = TradingCommands.ResolveConfigDir(paths.ConfigDir);
        }
        catch (ArgumentException ex)
        {
            r.Add(Mark.Fail, "Config", ex.Message);
            r.Step(Priority.Config, "Run qa from the repository folder (the .\\qa launcher does this), or pass --config-dir.");
            return r;
        }

        PromotionState? promotion = Try(r, "Mode", () => PromotionState.Load(paths.PromotionDir));
        if (promotion is not null)
        {
            if (promotion.MaxAllowed < TradingMode.Paper)
            {
                r.Add(Mark.Fail, "Mode", $"the promotion state allows only {promotion.MaxAllowed} ({promotion.Source}): Paper refuses to start");
                r.Step(Priority.Config, $"Paper is above the promotion state; raising it is your step (see {promotion.Source}).");
            }
            else
            {
                string records = promotion.Records > 0 ? $"; {promotion.Records} signed promotion record(s), check them with: qa promote --verify" : string.Empty;
                r.Add(Mark.Ok, "Mode", $"Paper (simulated fills, nothing sent to Avanza); highest allowed: {promotion.MaxAllowed}{records}");
            }
        }

        string? session = CheckKillAndSession(r, paths);
        RiskLimits? limits = Try(r, "Risk limits", () => RiskLimits.Load(Path.Combine(configDir, RiskLimits.FileName)));
        PaperConfig? paper = Try(r, "Paper account", () => PaperConfig.Load(Path.Combine(configDir, PaperConfig.FileName)));
        PaperBook? book = CheckBook(r, paths, paper, time);
        if (paper is not null)
        {
            CheckPaperAccount(r, configDir, paper, limits, book);
            CheckStrategy(r, paper, paths.Ledger);
        }

        MarketCalendar? calendar = Try(r, "Calendar", () => MarketCalendarLoader.LoadDirectory(configDir));
        Universe? universe = Try(r, "Allowlist", () => Universe.Load(Path.Combine(configDir, Universe.FileName)));
        IReadOnlyList<MarketInfo> foreign = universe is null ? [] : CheckAllowlist(r, universe, paths.Store, calendar);

        if (universe is { Entries.Count: > 0 } && limits is not null && universe.Entries.Count * limits.MaxPositionPctOfAccount < 1m)
        {
            decimal cap = universe.Entries.Count * limits.MaxPositionPctOfAccount;
            r.Add(Mark.Warn, "Invested", string.Create(CultureInfo.InvariantCulture,
                $"at most {cap:P0} of the account: R7 allows {limits.MaxPositionPctOfAccount:P0} per name and the allowlist has {universe.Entries.Count}"));
            r.Step(Priority.Advice, string.Create(CultureInfo.InvariantCulture,
                $"With {universe.Entries.Count} name(s) Paper can invest only {cap:P0} (R7: {limits.MaxPositionPctOfAccount:P0} per name), whatever the backtest held. Add names (up to 5) to use more of the account."));
        }

        if (calendar is not null)
        {
            CheckCalendar(r, calendar);
        }

        // ADR 0005: a US or Canadian share on the list adds its market's calendar and decision to the session.
        var foreignSchedules = new List<TradingSchedule>();
        foreach (MarketInfo market in foreign)
        {
            MarketCalendar other;
            try
            {
                other = MarketCalendarLoader.LoadDirectory(configDir, market.Mic);
            }
            catch (CalendarConfigException ex)
            {
                // The session still runs; it skips this market's shares (a warning, not a blocker).
                r.Add(Mark.Warn, $"Calendar {market.Mic}", $"not loaded ({ex.Message}): Paper skips the {market.Currency} shares");
                continue;
            }

            r.Add(other.IsVerified ? Mark.Ok : Mark.Warn, $"Calendar {market.Mic}",
                other.IsVerified ? "verified" : $"{string.Join(", ", other.UnverifiedYears)} not verified: Paper runs (foreign shares trade on paper only)");
            if (calendar is not null && limits is not null && paper is not null
                && Try(r, "Paper account", () => new TradingSchedule(other, limits, paper.DecisionTime, calendar)) is { } schedule)
            {
                foreignSchedules.Add(schedule);
            }
        }

        CheckAuditAndGate(r, paths, promotion, time);
        if (promotion is { MaxAllowed: >= TradingMode.Confirm } && calendar is not null && paper is not null)
        {
            CheckConfirm(r, paths, configDir, calendar, paper, getVariable ?? Environment.GetEnvironmentVariable, promotionKeys ?? PromotionKeyStores.Default, time);
        }

        if (session is not null)
        {
            r.Step(Priority.Session, "A session is running: its window shows what it does. Stop it with Ctrl+C there, or from anywhere with: qa kill");
        }
        else if (calendar is not null && limits is not null && paper is not null && !r.Blocked)
        {
            r.Step(Priority.Session, NextSession(r.Now, Try(r, "Paper account", () => new TradingSchedule(calendar, limits, paper.DecisionTime)), foreignSchedules));
        }

        return r;
    }

    internal static void Write(TextWriter w, StatusReport r)
    {
        w.WriteLine($"QuantAnalyst status, {MarketTime.ToStockholm(r.Now):dddd yyyy-MM-dd HH:mm} Stockholm time");
        w.WriteLine();
        int width = r.Lines.Max(l => l.Label.Length);
        foreach ((Mark mark, string label, string text) in r.Lines)
        {
            string tag = mark switch { Mark.Ok => "ok", Mark.Todo => "todo", Mark.Warn => "warn", _ => "FAIL" };
            w.WriteLine($"  {tag,-4}  {label.PadRight(width)}  {text}");
        }

        w.WriteLine();
        w.WriteLine("Next steps:");
        int n = 1;
        foreach (string step in r.Steps)
        {
            w.WriteLine($"  {n++}. {step}");
        }

        w.WriteLine();
        w.WriteLine("The whole routine: docs/guide.md. Every command: qa --help.");
    }

    private static void CheckNative(StatusReport r)
    {
        try
        {
            QeAbi.EnsureCompatible();
            r.Add(Mark.Ok, "Native engine", $"qe ABI {QeAbi.NativeVersion.Major}.{QeAbi.NativeVersion.Minor}");
        }
        catch (Exception ex) when (ex is NativeAbiMismatchException or QeException || DataCommands.IsStoreFailure(ex))
        {
            r.Add(Mark.Fail, "Native engine", ex is NativeAbiMismatchException ? $"out of date (ABI {QeAbi.NativeVersion.Major}.{QeAbi.NativeVersion.Minor})" : "the qe library is missing");
            r.Step(Priority.Native, "Rebuild the native engine: start qa through the .\\qa launcher (it rebuilds what changed), or run .\\build.ps1.");
        }
    }

    private static string? CheckKillAndSession(StatusReport r, StatusPaths paths)
    {
        KillRecord? kill = Try(r, "Kill switch", () => KillSwitch.RecordedKill(paths.KillFile, paths.StateDir));
        string? session = Try(r, "Session", () => SessionLock.Holder(paths.StateDir));
        if (kill is not null)
        {
            r.Add(Mark.Fail, "Kill switch", $"ACTIVE since {Local(kill.SinceUtc)} ({kill.Source}: {kill.Reason}); nothing trades");
            r.Step(Priority.Kill, "Find out why the kill switch is on (qa report eod, the audit log), then clear it: qa kill --reset");
        }
        else
        {
            r.Add(Mark.Ok, "Kill switch", "off (stop everything at any time with: qa kill)");
        }

        r.Add(Mark.Ok, "Session", session is null ? "none running" : $"running ({session})");
        return session;
    }

    private static PaperBook? CheckBook(StatusReport r, StatusPaths paths, PaperConfig? paper, TimeProvider time)
    {
        string dir = Path.Combine(paths.StateDir, "paper");
        if (!File.Exists(Path.Combine(dir, PaperBook.FileName)))
        {
            return null;
        }

        return Try(r, "Paper book", () => PaperBook.OpenOrCreate(dir, paper ?? new PaperConfig("?", 1m, new TimeOnly(9, 10)), null, time, out _));
    }

    private static void CheckPaperAccount(StatusReport r, string configDir, PaperConfig paper, RiskLimits? limits, PaperBook? book)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        CostModel? costs = Try(r, "Paper account", () => CostModel.Load(Path.Combine(configDir, $"costs.{paper.Costs}.json")));
        if (costs is not null)
        {
            r.Add(costs.Verified ? Mark.Ok : Mark.Warn, "Paper account", string.Create(c,
                $"{costs.DisplayName ?? costs.Name} courtage{(costs.Verified ? string.Empty : " (NOT verified)")}, {paper.Cash:N0} SEK to start, decides at {paper.DecisionTime:HH\\:mm} (config/paper.json)"));
        }

        if (book is null)
        {
            r.Add(Mark.Ok, "Paper book", string.Create(c, $"not started; the first 'qa paper run' opens it with {paper.Cash:N0} SEK"));
        }
        else
        {
            decimal atCost = book.Positions.Sum(p => p.CostBasis);
            r.Add(Mark.Ok, "Paper book", string.Create(c,
                $"cash {book.Cash:N2} SEK, {book.Positions.Count} position(s) costing {atCost:N2}, realised P&L {book.RealizedPnl:N2}, fees {book.FeesPaid:N2} (qa paper status)"));
        }

        if (limits is not null)
        {
            decimal value = book is null ? paper.Cash : book.Cash + book.Positions.Sum(p => p.CostBasis);
            decimal sized = limits.SizingValue(value);
            decimal perOrder = Math.Min(limits.MaxOrderValueSek, limits.MaxOrderValuePctOfAccount * sized);
            string capped = limits.Capped(value) ? string.Create(c, $" (sized on the {limits.MaxAccountValueSek:N0} SEK account cap)") : string.Empty;
            r.Add(Mark.Ok, "Risk limits", string.Create(c,
                $"for {value:N0} SEK{capped}: orders up to {perOrder:N0} SEK, {limits.MaxPositionPctOfAccount * sized:N0} SEK per name, loss stop at -{limits.DailyLossLimitSek(value):N0} SEK a day (qa risk-limits)"));
        }
    }

    private static void CheckStrategy(StatusReport r, PaperConfig paper, string? ledger)
    {
        const string Example = "qa paper strategy ma-cross --param fast=20 --param slow=100";
        if (paper.Strategy is not { } saved)
        {
            r.Add(Mark.Todo, "Strategy", "none saved: 'qa paper run' does not know what to trade");
            r.Step(Priority.Strategy, $"Choose the strategy Paper trades. Try it on your history first (qa backtest run --strategy ma-cross --param fast=20 --param slow=100), then save it: {Example}");
            return;
        }

        StrategyDefinition? definition = Try(r, "Strategy", () => StrategyCatalog.Create(saved.Name, saved.Parameters));
        if (definition is null)
        {
            r.Step(Priority.Strategy, $"The saved strategy is not valid; save it again, e.g.: {Example}");
            return;
        }

        int? trials = AvanzaCommands.BacktestedTrials(definition.Spec, ledger);
        if (trials == 0)
        {
            r.Add(Mark.Warn, "Strategy", $"{definition.Spec.Describe()}, not yet backtested on imported history");
            r.Step(Priority.Advice, $"See how the saved strategy did on your allowlist's history: qa backtest run --strategy {saved.CommandLine()}");
        }
        else
        {
            r.Add(Mark.Ok, "Strategy", $"{definition.Spec.Describe()}{(trials is { } t ? $"; {t} backtest(s) of it in the trial ledger" : string.Empty)}");
        }
    }

    /// <summary>The allowlist and its history; returns the foreign markets on it (ADR 0005), from the stored instruments.</summary>
    private static List<MarketInfo> CheckAllowlist(StatusReport r, Universe universe, string storePath, MarketCalendar? calendar)
    {
        var foreign = new List<MarketInfo>();
        if (universe.Entries.Count == 0)
        {
            r.Add(Mark.Todo, "Allowlist", "empty: every order would be rejected (R2)");
            r.Step(Priority.Allowlist, "Choose what may be traded (SEK shares): qa history import ERIC-B, then qa universe add ERIC-B. Repeat per name.");
            return foreign;
        }

        r.Add(Mark.Ok, "Allowlist", $"{universe.Entries.Count}: {string.Join(", ", universe.Entries.Select(e => e.Ticker))} (qa universe list)");
        if (!File.Exists(storePath))
        {
            r.Add(Mark.Todo, "History", $"no history store at {storePath}; 'qa paper run' imports a year per name when it starts");
            return foreign;
        }

        HistoryStore? history = Try(r, "History", () => HistoryStore.Open(storePath));
        if (history is null)
        {
            return foreign;
        }

        using (history)
        {
            string source = AvanzaChartImporter.AvanzaPriceChart.Name;
            bool hasSource = history.GetSource(source) is not null;
            DateOnly? wanted = null;
            try
            {
                wanted = calendar is null ? null : AvanzaCommands.PreviousTradingDay(calendar, Trading.OrderGateway.StockholmDate(r.Now));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // No calendar for the days before today (early January): the line shows the dates without judging them.
            }

            var parts = new List<string>();
            bool behind = false;
            foreach (UniverseEntry e in universe.Entries)
            {
                if (Markets.ForCurrency(history.GetInstrument(e.OrderbookId)?.Instrument.Currency) is { } market && Markets.IsForeign(market.Currency) && !foreign.Contains(market))
                {
                    foreign.Add(market);
                }

                IReadOnlyList<StoredBar> bars = hasSource ? history.GetDailyBars(e.OrderbookId, source) : [];
                DateOnly? last = bars.Count > 0 ? bars[^1].Bar.Date : null;
                behind |= last is null || last < wanted;
                parts.Add(last is { } l ? string.Create(CultureInfo.InvariantCulture, $"{e.Ticker} {bars.Count} bars to {l:yyyy-MM-dd}") : $"{e.Ticker} none");
            }

            r.Add(Mark.Ok, "History", string.Join("; ", parts) + (behind ? " ('qa paper run' tops it up when it starts)" : string.Empty));
        }

        return foreign;
    }

    private static void CheckCalendar(StatusReport r, MarketCalendar calendar)
    {
        if (calendar.IsVerified)
        {
            r.Add(Mark.Ok, "Calendar", "verified");
            return;
        }

        r.Add(Mark.Warn, "Calendar", $"{string.Join(", ", calendar.UnverifiedYears)} not verified: Paper runs, live trading will refuse it");
        r.Step(Priority.Advice, "Before any live trading: compare config/market-calendar.XSTO.<year>.json with Nasdaq Stockholm's calendar and set verified_on (docs/guide.md).");
    }

    private static void CheckAuditAndGate(StatusReport r, StatusPaths paths, PromotionState? promotion, TimeProvider time)
    {
        IReadOnlyList<DateOnly> days = Directory.Exists(paths.AuditDir)
            ? [.. Directory.EnumerateFiles(paths.AuditDir, "*.jsonl")
                .Select(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) ? d : (DateOnly?)null)
                .OfType<DateOnly>()
                .Order()]
            : [];
        if (days.Count == 0)
        {
            r.Add(Mark.Ok, "Audit log", "empty: no session has run yet");
            r.Add(Mark.Todo, "Confirm gate", $"0 of {PromotionGate.MinPaperDays} clean Paper days");
            return;
        }

        AuditVerification audit = AuditLog.Verify(paths.AuditDir);
        if (!audit.Valid)
        {
            r.Add(Mark.Fail, "Audit log", $"BROKEN: {audit.Problem}");
            r.Step(Priority.Audit, "The audit chain is broken: do not trade until you know why (qa audit verify says where).");
        }
        else
        {
            r.Add(Mark.Ok, "Audit log", $"intact: {audit.Records} records over {audit.Files} day(s)");
        }

        List<EodReport>? reports = Try(r, "Reports", () => days.Select(d => EodReport.Build(paths.AuditDir, d, time)).ToList());
        if (reports is null)
        {
            return;
        }

        EodReport last = reports[^1];
        if (last.Complete && !last.Clean)
        {
            r.Add(Mark.Warn, "Last session", $"{last.Date:yyyy-MM-dd} was not clean");
            r.Step(Priority.Report, $"Read what went wrong on {last.Date:yyyy-MM-dd}: qa report eod --date {last.Date:yyyy-MM-dd}");
        }
        else
        {
            r.Add(Mark.Ok, "Last session", $"{last.Date:yyyy-MM-dd}: {(last.Complete ? "clean" : "incomplete (stopped early or still running)")}, {last.Submitted} order(s), {last.Fills.Count} fill(s)");
        }

        GateResult gate = PromotionGate.Confirm(reports, audit);
        if (promotion is { MaxAllowed: >= TradingMode.Confirm })
        {
            r.Add(Mark.Ok, "Confirm gate", "promoted to Confirm");
            GateResult auto = PromotionGate.Auto(reports, audit);
            int confirmed = reports.Where(d => d.Modes.Contains("Confirm")).SelectMany(d => d.Live?.Orders ?? []).Count(o => !o.Simulated);
            r.Add(auto.Met ? Mark.Ok : Mark.Todo, "Auto gate", auto.Met
                ? "MET (Auto arrives in Phase 8)"
                : $"{confirmed} of {PromotionGate.MinConfirmedOrders} confirmed live orders; the rest of the gate: qa report gate");
        }
        else if (gate.Met)
        {
            r.Add(Mark.Ok, "Confirm gate", $"MET: {gate.Evidence.Count} clean Paper days in a row");
            r.Step(Priority.Gate, "The Confirm gate is met. Promoting is your decision and your command: qa promote --to Confirm (docs/guide.md).");
        }
        else
        {
            int sent = gate.Evidence.Sum(e => e.Submitted);
            r.Add(Mark.Todo, "Confirm gate", $"{gate.Evidence.Count} of {PromotionGate.MinPaperDays} clean Paper days{(sent == 0 ? ", no order sent in them yet" : string.Empty)} (qa report gate)");
        }
    }

    /// <summary>
    /// The Confirm startup checks that can run offline (plan 07 step 5). The session lock, R1 and the channel itself are
    /// checked when a session starts; the order format's readiness is known without a connection.
    /// </summary>
    private static void CheckConfirm(
        StatusReport r, StatusPaths paths, string configDir, MarketCalendar calendar, PaperConfig paper, Func<string, string?> getVariable, Func<IPromotionKeyStore> keys, TimeProvider time)
    {
        CostModel? costs = Try(r, "Confirm checks", () => CostModel.Load(Path.Combine(configDir, $"costs.{paper.Costs}.json")));
        if (costs is null)
        {
            return;
        }

        IPromotionKeyStore store;
        try
        {
            store = keys();
        }
        catch (ArgumentException ex)
        {
            store = new UnreadableKeys(ex.Message);
        }

        ConfirmStartupResult result = ConfirmStartup.Check(new ConfirmStartupInputs
        {
            PromotionDirectory = paths.PromotionDir,
            PromotionKeys = store,
            EvidenceBaseDirectory = Environment.CurrentDirectory,
            Calendar = calendar,
            Costs = costs,
            KillFile = paths.KillFile,
            StateDirectory = paths.StateDir,
            SessionLock = null,
            AuditDirectory = paths.AuditDir,
            Account = null,
            Channel = null,
            GetVariable = getVariable,
            Time = time,
        });
        List<StartupCheck> open = [.. result.Failures.Where(f => f.Name != "one session" && !ConfirmStartup.NeedAConnection.Contains(f.Name))];
        if (Avanza.Orders.AvanzaOrderChannel.FormatNotFinal is { } format)
        {
            open.Add(new StartupCheck("order channel", false, format));
        }

        if (open.Count == 0)
        {
            r.Add(Mark.Ok, "Confirm checks", "every check that runs offline passes; the account (R1) and the connection are checked when the session starts");
            r.Step(Priority.Gate, "Confirm can start. You start it yourself, at the computer before the decision time: .\\qa trade run --mode confirm (qa accounts shows R1 now).");
            return;
        }

        r.Add(Mark.Todo, "Confirm checks", $"{open.Count} not ready: {string.Join(", ", open.Select(c => c.Name))}");
        foreach (StartupCheck c in open)
        {
            r.Step(Priority.Gate, $"Before Confirm, {c.Name}: {c.Detail}");
        }
    }

    private sealed class UnreadableKeys(string why) : IPromotionKeyStore
    {
        public string Name => "the promotion key store";

        public byte[]? Read() => throw new InvalidOperationException(why);

        public void Create(byte[] key) => throw new InvalidOperationException(why);
    }

    internal static string NextSession(DateTimeOffset now, TradingSchedule? schedule, IReadOnlyList<TradingSchedule>? foreign = null)
    {
        if (schedule is null)
        {
            return "Fix the paper account settings above, then start a session: qa paper run";
        }

        if (foreign is { Count: > 0 })
        {
            return NextSessionAcrossMarkets(now, [schedule, .. foreign]);
        }

        SessionPlan? today = schedule.Plan(Trading.OrderGateway.StockholmDate(now));
        if (today is not null && now < today.WindowCloseUtc)
        {
            return now < today.DecisionUtc
                ? $"Start today's session before {Clock(today.DecisionUtc)}: qa paper run. It waits for {Clock(today.DecisionUtc)}, trades, and ends after the {Clock(today.CloseUtc)} close; keep its window open."
                : $"Today's session can still start (it decides at once): qa paper run. It ends after the {Clock(today.CloseUtc)} close; keep its window open.";
        }

        SessionPlan next = schedule.NextDecision(now);
        return $"Next session: {MarketTime.ToStockholm(next.DecisionUtc):dddd yyyy-MM-dd}. Start it that morning before {Clock(next.DecisionUtc)}: qa paper run";
    }

    /// <summary>
    /// With US or Canadian shares on the list (ADR 0005): one session decides per market, e.g. "XSTO at 09:10, XNYS at
    /// 15:40", and ends after the last close.
    /// </summary>
    private static string NextSessionAcrossMarkets(DateTimeOffset now, IReadOnlyList<TradingSchedule> schedules)
    {
        (TradingSchedule Schedule, SessionPlan Plan)[] today =
            [.. schedules.Select(s => (s, s.Plan(s.Calendar.LocalDate(now)))).Where(x => x.Item2 is not null).Select(x => (x.s, x.Item2!))];
        (TradingSchedule Schedule, SessionPlan Plan)[] open = [.. today.Where(x => now < x.Plan.WindowCloseUtc)];
        if (open.Length > 0)
        {
            string decides = string.Join(", ", open.Select(x => now >= x.Plan.DecisionUtc ? $"{x.Schedule.Mic} at once" : $"{x.Schedule.Mic} at {Clock(x.Plan.DecisionUtc)}"));
            string end = Clock(today.Max(x => x.Plan.CloseUtc));
            DateTimeOffset first = open.Min(x => x.Plan.DecisionUtc);
            return now < first
                ? $"Start today's session before {Clock(first)}: qa paper run. It decides per market ({decides}), trades, and ends after the {end} close; keep its window open."
                : $"Today's session can still start: qa paper run. It decides per market ({decides}) and ends after the {end} close; keep its window open.";
        }

        DateTimeOffset next = schedules.Min(s => s.NextDecision(now).DecisionUtc);
        return $"Next session: {MarketTime.ToStockholm(next):dddd yyyy-MM-dd}. Start it that morning before {Clock(next)}: qa paper run";
    }

    /// <summary>Runs a check; an expected failure becomes a FAIL line (and a config step) instead of stopping the report.</summary>
    private static T? Try<T>(StatusReport r, string label, Func<T> check)
        where T : class?
    {
        try
        {
            return check();
        }
        catch (Exception ex) when (ex is TradingConfigException or ArgumentException or CalendarConfigException or InvalidDataException or IOException
                                       or JsonException or UnauthorizedAccessException or PaperBookException or HistoryStoreException or InvalidOperationException
                                   || DataCommands.IsStoreFailure(ex))
        {
            r.Add(Mark.Fail, label, ex.Message);
            r.Step(Priority.Config, $"Fix {label.ToLowerInvariant()}: {ex.Message}");
            return null;
        }
    }

    private static string Clock(DateTimeOffset utc) => MarketTime.ToStockholm(utc).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Local(DateTimeOffset utc) => MarketTime.ToStockholm(utc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
