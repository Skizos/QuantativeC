using System.Collections.Concurrent;
using System.CommandLine;
using System.Globalization;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Avanza;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Calendar;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Live;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Trading;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Reconciliation;
using QuantAnalyst.Trading.Risk;
using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Cli.Commands;

internal static partial class AvanzaCommands
{
    // ---- qa paper run | status ------------------------------------------------------------------------

    private static Command Paper(AvanzaCliServices services)
    {
        var command = new Command("paper", "Paper trading (Phase 6): the full order pipeline with simulated fills on live quotes. Nothing is sent to Avanza.");
        command.Subcommands.Add(PaperRun(services));
        command.Subcommands.Add(PaperStrategyCommand());
        command.Subcommands.Add(PaperStatus());
        return command;
    }

    private static Command PaperRun(AvanzaCliServices services)
    {
        var common = new Common();
        var strategy = new Option<string?>("--strategy") { Description = $"Strategy: {string.Join(", ", StrategyCatalog.Names)} (default: the one saved with 'qa paper strategy')" };
        var param = new Option<string[]>("--param") { Description = "Strategy parameter key=value (repeatable; with --strategy)", AllowMultipleArgumentsPerToken = false };
        var duration = new Option<double?>("--duration") { Description = "Seconds to run (default: until two minutes after today's close)" };
        var configDir = TradingCommands.ConfigDirOption();
        var store = DataCommands.StoreOption();
        var auditDir = new Option<string>("--audit-dir") { Description = "Audit folder", DefaultValueFactory = _ => TradingCommands.DefaultAuditDir };
        var killFile = new Option<string>("--kill-file") { Description = "The kill flag file", DefaultValueFactory = _ => TradingCommands.DefaultKillFile };
        var promotionDir = new Option<string>("--promotion-dir") { Description = "Promotion state folder", DefaultValueFactory = _ => "promotion" };
        var reportsDir = new Option<string>("--reports-dir") { Description = "End-of-day reports folder", DefaultValueFactory = _ => TradingCommands.DefaultReportsDir };
        var command = new Command(
            "run",
            "Run one Paper session: bring the allowlist's daily history up to yesterday, decide after the open, place day limit orders through the risk engine, fill them on live quotes, end them at the close. Read-only towards Avanza.");
        common.AddTo(command, json: false);
        foreach (Option o in new Option[] { strategy, param, duration, configDir, store, auditDir, killFile, promotionDir, reportsDir })
        {
            command.Options.Add(o);
        }

        command.SetAction(parse => Run(parse, services, common, record: null, async (ctx, output) =>
        {
            TimeProvider time = services.Time;
            string stateDir = parse.GetValue(common.StateDir)!;
            PaperSetup setup = PaperSetup.Load(TradingCommands.ResolveConfigDir(parse.GetValue(configDir)), parse.GetValue(promotionDir)!);
            StrategyDefinition definition = ChooseStrategy(parse.GetValue(strategy), parse.GetValue(param), setup.Paper);
            string storePath = parse.GetValue(store)!;

            output.WriteLine($"Paper session: {definition.Spec.Describe()} on {string.Join(", ", setup.Universe.Entries.Select(e => e.Ticker))}; courtage class {setup.Costs.DisplayName ?? setup.Costs.Name}; mode {setup.Mode} (promotion: {setup.Promotion.MaxAllowed}).");
            foreach (string warning in setup.Warnings)
            {
                output.WriteLine("WARNING: " + warning);
            }

            using SessionLock sessionLock = SessionLock.Acquire(stateDir, time);
            var audit = new AuditLog(parse.GetValue(auditDir)!, time);
            var halts = new HaltController(audit, time);

            await ctx.Connection.Authenticator.LoginAsync(ctx.Ct).ConfigureAwait(false);
            var specs = new List<InstrumentSpec>();
            foreach (UniverseEntry entry in setup.Universe.Entries)
            {
                InstrumentTradingParams p = await ctx.Connection.Gateway.GetTradingParamsAsync(entry.OrderbookId, ctx.Ct).ConfigureAwait(false);
                specs.Add(new InstrumentSpec(entry.OrderbookId, entry.Ticker, p.Name, p.Currency, Math.Max(1, p.TradingUnit), p.TickSizes, TickTableVerified: true, p.Isin, p.MarketPlace));
            }

            try
            {
                await RefreshHistoryAsync(ctx, storePath, setup, time, output).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is BrokerException or HistoryImportException)
            {
                // Informational data (ADR 0002 Tier B): the session still runs; the decision refuses stale history itself.
                output.WriteLine($"WARNING: the history could not be brought up to date ({ex.Message}); the decision uses what is stored.");
            }

            var catalog = new InstrumentCatalog(specs);
            var quotes = new LatestQuotes();
            PaperBook book = PaperBook.OpenOrCreate(Path.Combine(stateDir, "paper"), setup.Paper, quotes, time, out IReadOnlyList<string> bookNotes);
            foreach (string note in bookNotes)
            {
                output.WriteLine(note);
            }

            var channel = new PaperOrderChannel(book, setup.Costs, quotes, catalog, time);
            var risk = new PreTradeRiskEngine(setup.Limits);
            var oms = new OrderManager(audit, halts, time);
            var env = new GatewayEnvironment
            {
                Mode = setup.Mode,
                Instruments = catalog,
                Quotes = quotes,
                Account = book,
                Calendar = setup.Calendar,
                Universe = setup.Universe,
                AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { PaperConfig.AccountId },
                Fees = (order, spec) => channel.EstimateFees(order.Value, spec.Currency),
                CourtageVerified = setup.Costs.Verified,
            };
            using var gateway = new OrderGateway(channel, env, risk, oms, halts, audit, time);
            using var kill = new KillSwitch(gateway, halts, audit, time, parse.GetValue(killFile)!, stateDir, book, setup.Limits);
            kill.Alerted += message => output.WriteLine("ALERT: " + message);
            if (kill.IsKilled)
            {
                output.WriteLine($"The kill switch is active ({kill.Record!.Source}: {kill.Record.Reason}). Check with 'qa kill --status', clear with 'qa kill --reset'.");
                return ExitHalt;
            }

            var reconciler = new Reconciler(oms, halts, audit, time, book.Account);
            IReadOnlyList<string> tickers = [.. specs.Select(s => s.Ticker)];
            Task<PlanResult> Decide(CancellationToken ct)
            {
                DateTimeOffset now = time.GetUtcNow();
                DateOnly yesterday = PreviousTradingDay(setup.Calendar, OrderGateway.StockholmDate(now));
                using HistoryStore history = DataCommands.OpenExisting(storePath);
                MarketPanel panel = BacktestCommands.LoadStorePanel(history, tickers, null, yesterday);
                if (panel.Dates[^1] != yesterday)
                {
                    throw new InvalidOperationException(
                        $"the history ends {panel.Dates[^1]:yyyy-MM-dd}, not on the last trading day {yesterday:yyyy-MM-dd}; run 'qa history import' for {string.Join(", ", tickers)} first. No orders today.");
                }

                double[] targets = StrategyReplay.DecideAtLastBar(panel, definition.Factory(panel));
                return Task.FromResult(DailyPlanner.Plan(targets, specs, book.Snapshot(), gateway.OpenOrders, quotes, risk, new ExecutionOptions(), definition.Spec.Describe(), now));
            }

            DateTimeOffset start = time.GetUtcNow();
            DateTimeOffset stopAt = parse.GetValue(duration) is { } seconds
                ? (seconds is > 0 and <= 16 * 3600 ? start.AddSeconds(seconds) : throw new ArgumentException("--duration must be in (0, 57600] seconds."))
                : setup.Schedule.Plan(OrderGateway.StockholmDate(start)) is { } today && start < today.CloseUtc.AddMinutes(2)
                    ? today.CloseUtc.AddMinutes(2)
                    : throw new ArgumentException(
                        $"No session left today. The next one is {MarketTime.ToStockholm(setup.Schedule.NextDecision(start).DecisionUtc):dddd yyyy-MM-dd}: start 'qa paper run' that morning before {setup.Paper.DecisionTime:HH\\:mm}. (--duration <seconds> runs a session now, outside market hours nothing trades.)");

            output.WriteLine($"Running until {MarketTime.ToStockholm(stopAt):yyyy-MM-dd HH:mm} (Stockholm). Stop early with Ctrl+C or 'qa kill'.");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
            ConsoleCancelEventHandler? onCancel = null;
            if (ctx.Interactive)
            {
                onCancel = (_, e) =>
                {
                    e.Cancel = true;
                    stop.Cancel();
                };
                Console.CancelKeyPress += onCancel;
            }

            var composers = specs.Select(s => new QuoteComposer(ctx.Connection.Gateway, s.OrderbookId, new QuoteComposerOptions(), time, ctx.Logger)).ToList();
            var subscriptions = composers.Select(c => c.Quotes.Subscribe(capacity: 256)).ToList();
            var pumps = subscriptions.Select(s => PumpQuotesAsync(s, quotes, channel)).ToList();
            var feeds = composers.Select(c => StopAllOnFailure(c.RunAsync(stop.Token), stop)).ToList();
            string EndOfDayReport(DateOnly day)
            {
                Trading.Reports.EodReport report = Trading.Reports.EodReport.Build(audit.Directory, day, time);
                string saved = report.Save(parse.GetValue(reportsDir)!);
                return $"{report.Summary()} Saved to {saved}.";
            }

            var session = new PaperSession(gateway, channel, book, kill, reconciler, halts, setup.Schedule, audit, time, Decide, output, EndOfDayReport);
            PaperSessionSummary summary;
            try
            {
                summary = await session.RunAsync(stopAt, stop.Token).ConfigureAwait(false);
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                if (onCancel is not null)
                {
                    Console.CancelKeyPress -= onCancel;
                }

                try
                {
                    await Task.WhenAll(feeds).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The feeds stop with the session.
                }

                foreach (Data.Live.Broadcaster<Quote>.Subscription s in subscriptions)
                {
                    s.Dispose();
                }

                await Task.WhenAll(pumps).ConfigureAwait(false);
            }

            output.WriteLine();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Session over: {summary.Submitted} order(s) sent to the paper channel, {summary.Accepted} accepted, {summary.RiskRejected} stopped by the risk checks, {summary.Filled} with fills."));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Paper account: value {summary.AccountValue:N2} SEK (start of day {summary.StartOfDayValue:N2}), cash {summary.Cash:N2}, fees paid {summary.FeesPaid:N2}."));
            foreach (PaperPosition p in book.Positions)
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {p.Ticker}: {p.Quantity} (cost {p.CostBasis:N2} SEK)"));
            }

            output.WriteLine($"Reconciliation: {(summary.ReconciliationClean ? "clean" : "MISMATCH (see the audit log)")}. Audit: {audit.Directory} (check with 'qa audit verify').");
            return summary.Killed ? ExitHalt : 0;
        }, live: true));
        return command;
    }

    private static Command PaperStrategyCommand()
    {
        var name = new Argument<string?>("name") { Description = $"Strategy: {string.Join(", ", StrategyCatalog.Names)}. Leave out to show the saved one.", Arity = ArgumentArity.ZeroOrOne };
        var param = new Option<string[]>("--param") { Description = "Strategy parameter key=value (repeatable)", AllowMultipleArgumentsPerToken = false };
        var clear = new Option<bool>("--clear") { Description = "Remove the saved strategy" };
        var configDir = TradingCommands.ConfigDirOption();
        var ledger = new Option<string?>("--ledger") { Description = $"Trial ledger to look for its backtests (default: <repository>/{TrialLedger.DefaultPath})" };
        var command = new Command("strategy", "Save the strategy 'qa paper run' trades (in config/paper.json), or show it. Offline.");
        command.Arguments.Add(name);
        command.Options.Add(param);
        command.Options.Add(clear);
        command.Options.Add(configDir);
        command.Options.Add(ledger);
        command.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            string path = Path.Combine(TradingCommands.ResolveConfigDir(parse.GetValue(configDir)), PaperConfig.FileName);
            string? chosen = parse.GetValue(name);
            if (parse.GetValue(clear))
            {
                if (chosen is not null)
                {
                    throw new ArgumentException("Give a strategy name or --clear, not both.");
                }

                PaperConfig.SaveStrategy(path, null);
                w.WriteLine($"Removed the saved strategy from {path}; 'qa paper run' now needs --strategy.");
                return 0;
            }

            if (chosen is null)
            {
                if (parse.GetValue(param) is { Length: > 0 })
                {
                    throw new ArgumentException("--param needs a strategy name, e.g. qa paper strategy ma-cross --param fast=20 --param slow=100");
                }

                PaperStrategy? saved = PaperConfig.Load(path).Strategy;
                w.WriteLine(saved is null
                    ? $"No strategy saved in {path}. Save one, e.g.: qa paper strategy ma-cross --param fast=20 --param slow=100"
                    : $"Saved strategy: {StrategyCatalog.Create(saved.Name, saved.Parameters).Spec.Describe()} ({path}). 'qa paper run' trades it.");
                return 0;
            }

            StrategyDefinition definition = StrategyCatalog.Create(chosen, BacktestCommands.ParseParams(parse.GetValue(param)));
            PaperConfig.SaveStrategy(path, new PaperStrategy(definition.Spec.Name, definition.Spec.Parameters));
            w.WriteLine($"Saved {definition.Spec.Describe()} in {path}. 'qa paper run' now trades it without arguments.");
            if (BacktestedTrials(definition.Spec, parse.GetValue(ledger)) is 0)
            {
                w.WriteLine($"Note: the trial ledger has no backtest of exactly this on imported history. See how it did first: qa backtest run --strategy {new PaperStrategy(definition.Spec.Name, definition.Spec.Parameters).CommandLine()}");
            }

            return 0;
        }));
        return command;
    }

    /// <summary>--strategy (with its --param values), else the strategy saved in paper.json.</summary>
    private static StrategyDefinition ChooseStrategy(string? name, string[]? parameters, PaperConfig paper)
    {
        if (name is not null)
        {
            return StrategyCatalog.Create(name, BacktestCommands.ParseParams(parameters));
        }

        if (parameters is { Length: > 0 })
        {
            throw new ArgumentException("--param needs --strategy (or save both with 'qa paper strategy <name> --param ...').");
        }

        return paper.Strategy is { } saved
            ? StrategyCatalog.Create(saved.Name, saved.Parameters)
            : throw new ArgumentException("No strategy: save one once with 'qa paper strategy ma-cross --param fast=20 --param slow=100' (or pass --strategy).");
    }

    /// <summary>Backtests of this exact strategy on imported (not synthetic) history, or null when the ledger is not found.</summary>
    internal static int? BacktestedTrials(StrategySpec spec, string? ledgerPath = null)
    {
        try
        {
            var ledger = new TrialLedger(BacktestCommands.ResolveLedger(ledgerPath));
            if (!File.Exists(ledger.Path))
            {
                return 0;
            }

            return ledger.ReadAll().Count(t => t.Strategy == spec.Name && t.Status == TrialStatus.Ok && t.DataSource != SyntheticMarket.SourceName
                && t.Parameters.Count == spec.Parameters.Count && spec.Parameters.All(p => t.Parameters.TryGetValue(p.Key, out string? v) && v == p.Value));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Brings every allowlisted instrument's daily bars up to the last trading day before today (read-only chart calls,
    /// the same as 'qa history import'), so a session never stops at the decision for want of yesterday's bar.
    /// </summary>
    private static async Task RefreshHistoryAsync(Ctx ctx, string storePath, PaperSetup setup, TimeProvider time, TextWriter output)
    {
        DateOnly through = PreviousTradingDay(setup.Calendar, OrderGateway.StockholmDate(time.GetUtcNow()));
        string source = AvanzaChartImporter.AvanzaPriceChart.Name;
        using HistoryStore history = HistoryStore.Open(storePath);
        foreach (UniverseEntry entry in setup.Universe.Entries)
        {
            IReadOnlyList<StoredBar> bars = history.GetSource(source) is null ? [] : history.GetDailyBars(entry.OrderbookId, source);
            DateOnly? last = bars.Count > 0 ? bars[^1].Bar.Date : null;
            if (last >= through)
            {
                continue;
            }

            // A week of overlap catches restated bars; with no history, a year gives the strategies their look-back.
            DateOnly from = last is { } l ? l.AddDays(-7) : through.AddYears(-1);
            InstrumentTradingParams p = await ctx.Connection.Gateway.GetTradingParamsAsync(entry.OrderbookId, ctx.Ct).ConfigureAwait(false);
            history.UpsertInstrument(InstrumentRecord.FromTradingParams(p), "avanza-orderbook", AvanzaConnection.OrderbookSourceVersion, p.KnownAtUtc);
            var provider = new AvanzaChartImporter(ctx.Connection.Gateway, time, AvanzaConnection.PriceChartSourceVersion);
            ImportReport report = await new HistoryImporter(history, time, setup.Calendar)
                .ImportAsync(provider, entry.OrderbookId, from, through, ctx.Ct).ConfigureAwait(false);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"History: {entry.Ticker} brought up to {report.LastDate:yyyy-MM-dd} ({report.Bars.New} new bar(s), {report.Bars.Restated} restated)."));
            foreach (string warning in report.Warnings)
            {
                output.WriteLine($"warning: {warning}");
            }
        }
    }

    private static Command PaperStatus()
    {
        var stateDir = new Option<string>("--state-dir") { Description = "State folder", DefaultValueFactory = _ => TradingCommands.DefaultStateDir };
        var command = new Command("status", "Show the paper book (cash, positions, fees) from state/paper/book.json. Offline; positions are shown at cost.");
        command.Options.Add(stateDir);
        command.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            string dir = Path.Combine(parse.GetValue(stateDir)!, "paper");
            if (!File.Exists(Path.Combine(dir, PaperBook.FileName)))
            {
                w.WriteLine("No paper book yet; 'qa paper run' starts one from config/paper.json.");
                return 0;
            }

            PaperBook book = PaperBook.OpenOrCreate(dir, new PaperConfig("?", 1m, new TimeOnly(9, 10)), null, TimeProvider.System, out _);
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Paper book ({book.Costs}): started with {book.StartingCash:N2} SEK; cash {book.Cash:N2}; realised P&L {book.RealizedPnl:N2}; fees {book.FeesPaid:N2}."));
            foreach (PaperPosition p in book.Positions)
            {
                w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {p.Ticker} ({p.OrderbookId}): {p.Quantity}, cost {p.CostBasis:N2} SEK, last fill {p.LastFillPrice}"));
            }

            w.WriteLine($"Session running: {(SessionLock.Holder(parse.GetValue(stateDir)!) is { } holder ? holder : "no")}.");
            return 0;
        }));
        return command;
    }

    internal static DateOnly PreviousTradingDay(MarketCalendar calendar, DateOnly today)
    {
        for (DateOnly d = today.AddDays(-1); d > today.AddDays(-15); d = d.AddDays(-1))
        {
            if (calendar.Classify(d).IsTradingDay)
            {
                return d;
            }
        }

        throw new InvalidOperationException($"No trading day in the two weeks before {today:yyyy-MM-dd}; check the calendar.");
    }

    private static async Task PumpQuotesAsync(Data.Live.Broadcaster<Quote>.Subscription subscription, LatestQuotes quotes, PaperOrderChannel channel)
    {
        try
        {
            await foreach (Quote q in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                quotes.Set(q);
                channel.OnQuote(q);
            }
        }
        catch (Exception) when (subscription.Reader.Completion.IsFaulted)
        {
            // The composer's failure is reported by its run task.
        }
    }

    /// <summary>The newest composed quote per instrument, fed by the composers.</summary>
    private sealed class LatestQuotes : IQuoteSource
    {
        private readonly ConcurrentDictionary<OrderbookId, Quote> _latest = new();

        public Quote? Latest(OrderbookId id) => _latest.GetValueOrDefault(id);

        public void Set(Quote q) => _latest[q.OrderbookId] = q;
    }

    /// <summary>Everything a Paper session reads from config/ and promotion/, checked before any login.</summary>
    private sealed record PaperSetup(
        TradingMode Mode, PromotionState Promotion, RiskLimits Limits, PaperConfig Paper, Universe Universe, CostModel Costs, MarketCalendar Calendar,
        TradingSchedule Schedule, IReadOnlyList<string> Warnings)
    {
        public static PaperSetup Load(string configDir, string promotionDir)
        {
            PromotionState promotion = PromotionState.Load(promotionDir);
            TradingMode mode = promotion.Effective(TradingMode.Paper);
            RiskLimits limits = RiskLimits.Load(Path.Combine(configDir, RiskLimits.FileName));
            PaperConfig paper = PaperConfig.Load(Path.Combine(configDir, PaperConfig.FileName));
            Universe universe = Universe.Load(Path.Combine(configDir, Universe.FileName));
            if (universe.Entries.Count == 0)
            {
                throw new ArgumentException("The instrument allowlist is empty, so every order would be rejected (R2). Add names first: qa universe add ERIC-B");
            }

            if (universe.Entries.Count > MaxStreamInstruments)
            {
                throw new ArgumentException($"Paper streams every allowlisted instrument; at most {MaxStreamInstruments} (ADR 0002 §3 load budget). The allowlist has {universe.Entries.Count}.");
            }

            string costsPath = Path.Combine(configDir, $"costs.{paper.Costs}.json");
            CostModel costs = CostModel.Load(costsPath);
            if (costs.EligibleBelowCapital is { } cap && paper.Cash >= cap)
            {
                throw new ArgumentException($"{costs.DisplayName ?? costs.Name} can only be chosen below {cap:N0} SEK; the paper cash is {paper.Cash:N0}. Change config/paper.json.");
            }

            MarketCalendar calendar = MarketCalendarLoader.LoadDirectory(configDir);
            var warnings = new List<string>();
            if (!calendar.IsVerified)
            {
                warnings.Add($"the trading calendar for {string.Join(", ", calendar.UnverifiedYears)} is not verified yet (allowed in Paper; live trading refuses it).");
            }

            if (!costs.Verified)
            {
                warnings.Add($"the courtage class {costs.Name} has no verified_on date.");
            }

            return new PaperSetup(mode, promotion, limits, paper, universe, costs, calendar, new TradingSchedule(calendar, limits, paper.DecisionTime), warnings);
        }
    }
}
