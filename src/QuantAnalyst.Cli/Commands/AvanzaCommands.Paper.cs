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
using QuantAnalyst.Trading.Observation;
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

            // Plan 21: shares taken off the list while held are quoted and sold to zero; the strategy never sees them.
            foreach (UniverseEntry gone in setup.Universe.Exiting)
            {
                output.WriteLine($"{gone.Ticker}: off the list, still held; this session sells it (sells only).");
            }

            foreach (UniverseEntry entry in setup.Universe.Entries.Concat(setup.Universe.Exiting))
            {
                InstrumentTradingParams p = await ctx.Connection.Gateway.GetTradingParamsAsync(entry.OrderbookId, ctx.Ct).ConfigureAwait(false);
                specs.Add(new InstrumentSpec(entry.OrderbookId, entry.Ticker, p.Name, p.Currency, Math.Max(1, p.TradingUnit), p.TickSizes, TickTableVerified: true, p.Isin, p.MarketPlace));
            }

            List<PaperMarket> markets = setup.Markets(specs, output);
            try
            {
                await RefreshHistoryAsync(ctx, storePath, markets, time, output).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is BrokerException or HistoryImportException)
            {
                // Informational data (ADR 0002 Tier B): the session still runs; the decision refuses stale history itself.
                output.WriteLine($"WARNING: the history could not be brought up to date ({ex.Message}); the decision uses what is stored.");
            }

            FxTable fx = await FxAtStartAsync(storePath, markets, services.FxRates(), time, output, ctx.Ct).ConfigureAwait(false);
            markets.RemoveAll(m => fx.SekPerUnit(m.Info.Currency) is null);
            if (markets.Count == 0)
            {
                throw new ArgumentException("No market on the allowlist can trade today (see the warnings above).");
            }

            if (markets.Count > 1)
            {
                foreach (PaperMarket m in markets)
                {
                    output.WriteLine(m.Describe(time.GetUtcNow()));
                }
            }

            specs = [.. markets.SelectMany(m => m.Specs)];
            var catalog = new InstrumentCatalog(specs);
            var quotes = new LatestQuotes();
            PaperBook book = PaperBook.OpenOrCreate(Path.Combine(stateDir, "paper"), setup.Paper, quotes, time, out IReadOnlyList<string> bookNotes, fx);
            foreach (string note in bookNotes)
            {
                output.WriteLine(note);
            }

            var channel = new PaperOrderChannel(book, setup.Costs, quotes, catalog, time, fx);
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
                Fx = fx,
                Schedules = markets.ToDictionary(m => m.Info.Currency, m => m.Schedule, StringComparer.Ordinal),
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
            InstrumentSpec[] listed = [.. specs.Where(s => !setup.Universe.IsExiting(s.OrderbookId))];
            InstrumentSpec[] exiting = [.. specs.Where(s => setup.Universe.IsExiting(s.OrderbookId))];
            IReadOnlyList<string> tickers = [.. listed.Select(s => s.Ticker)];
            ISessionObserver? observer = GuardedObserver.Wrap(services.SessionObserver);
            if (observer is not null)
            {
                oms.Changed += o => observer.Order(OrderTick.From(o, time.GetUtcNow()));
            }

            // One decision per market (ADR 0005), each on the whole list's bars through yesterday (all complete by then),
            // so the targets are the ones a backtest of the list computes; each market trades its own shares.
            Task<PlanResult> Decide(PaperMarket market, CancellationToken ct)
            {
                DateTimeOffset now = time.GetUtcNow();
                DateOnly yesterday = PreviousTradingDay(market.Calendar, market.Calendar.LocalDate(now));
                int[] own = [.. listed.Select((s, i) => (s, i)).Where(x => market.Trades(x.s)).Select(x => x.i)];
                InstrumentSpec[] leaving = [.. exiting.Where(market.Trades)];
                double[] targets = [];
                if (own.Length > 0)
                {
                    using HistoryStore history = DataCommands.OpenExisting(storePath);
                    MarketPanel panel = BacktestCommands.LoadStorePanel(history, tickers, null, markets.Count == 1 ? yesterday : OrderGateway.StockholmDate(now).AddDays(-1));
                    DateOnly last = LastBarDate(panel, own);
                    if (last != yesterday)
                    {
                        string names = string.Join(", ", own.Select(i => tickers[i]));
                        throw new InvalidOperationException(
                            $"the history ends {last:yyyy-MM-dd}, not on the last trading day {yesterday:yyyy-MM-dd}; run 'qa history import' for {names} first. No orders today.");
                    }

                    targets = StrategyReplay.DecideAtLastBar(panel, definition.Factory(panel));
                }

                PlanResult plan = DailyPlanner.Plan(
                    [.. own.Select(i => targets[i]), .. leaving.Select(_ => 0.0)], [.. own.Select(i => listed[i]), .. leaving], book.Snapshot(), gateway.OpenOrders, quotes, risk,
                    new ExecutionOptions(), definition.Spec.Describe(), now, fx);
                return Task.FromResult(leaving.Length == 0 ? plan : plan with
                {
                    Notes = [.. leaving.Select(s => $"{s.Ticker}: off the list (exiting), target zero"), .. plan.Notes],
                });
            }

            DateTimeOffset start = time.GetUtcNow();
            SessionPlan[] todays = [.. markets.Select(m => m.Schedule.Plan(m.Calendar.LocalDate(start))).OfType<SessionPlan>()];
            DateTimeOffset stopAt = parse.GetValue(duration) is { } seconds
                ? (seconds is > 0 and <= 16 * 3600 ? start.AddSeconds(seconds) : throw new ArgumentException("--duration must be in (0, 57600] seconds."))
                : todays.Length > 0 && todays.Max(p => p.CloseUtc) is var lastClose && start < lastClose.AddMinutes(2)
                    ? lastClose.AddMinutes(2)
                    : throw new ArgumentException(
                        $"No session left today. The next one is {MarketTime.ToStockholm(NextDecision(markets, start)):dddd yyyy-MM-dd}: start 'qa paper run' that morning before {MarketTime.ToStockholm(NextDecision(markets, start)):HH\\:mm}. (--duration <seconds> runs a session now, outside market hours nothing trades.)");

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

            // Owner's decision 2026-09-30: Avanza refuses the order-depth stream (HTTP 429), so Paper runs on the market-data
            // polls alone (a quote is fresh while its last poll is under 10 s old). Confirm and Auto still require the stream.
            output.WriteLine("Live prices: polled every 5 s (Paper does not use Avanza's order-book stream, which Avanza refuses).");
            var composers = specs.Select(s => new QuoteComposer(ctx.Connection.Gateway, s.OrderbookId, new QuoteComposerOptions { DepthStream = false }, time, ctx.Logger)).ToList();
            var subscriptions = composers.Select(c => c.Quotes.Subscribe(capacity: 256)).ToList();
            if (observer is not null)
            {
                observer.Started(new SessionStarted(start, todays.Length > 0 ? todays.Min(p => p.OpenUtc) : null, todays.Length > 0 ? todays.Max(p => p.CloseUtc) : null,
                    todays.Length > 0 ? todays.Min(p => p.DecisionUtc) : null, definition.Spec.Describe(), ObservedInstruments(storePath, specs, OrderGateway.StockholmDate(start))));
            }

            var spreads = new SpreadSampler();
            var pumps = subscriptions.Select(s => PumpQuotesAsync(s, quotes, channel, observer, time, spreads)).ToList();
            var feeds = composers.Select(c => StopAllOnFailure(c.RunAsync(stop.Token), stop)).ToList();
            string EndOfDayReport(DateOnly day)
            {
                Trading.Reports.EodReport report = Trading.Reports.EodReport.Build(audit.Directory, day, time);
                string saved = report.Save(parse.GetValue(reportsDir)!);
                return $"{report.Summary()} Saved to {saved}.";
            }

            var session = new PaperSession(gateway, channel, book, kill, reconciler, halts,
                [.. markets.Select(m => new SessionMarket(m.Schedule, ct => Decide(m, ct), m.Trades)
                {
                    Prices = now => DailyPlanner.Coverage(specs.Where(m.Trades), quotes, risk, now),
                })], audit, time, output, EndOfDayReport, observer);
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
            await KeepIntradayResearchAsync(ctx, storePath, setup, spreads, time, output).ConfigureAwait(false);
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
    /// Brings every allowlisted instrument's daily bars up to its market's last trading day before today (read-only chart
    /// calls, the same as 'qa history import'), so a session never stops at the decision for want of yesterday's bar.
    /// </summary>
    private static async Task RefreshHistoryAsync(Ctx ctx, string storePath, IReadOnlyList<PaperMarket> markets, TimeProvider time, TextWriter output)
    {
        string source = AvanzaChartImporter.AvanzaPriceChart.Name;
        using HistoryStore history = HistoryStore.Open(storePath);
        foreach (PaperMarket market in markets)
        {
            DateOnly through = PreviousTradingDay(market.Calendar, market.Calendar.LocalDate(time.GetUtcNow()));
            foreach (InstrumentSpec spec in market.Specs)
            {
                IReadOnlyList<StoredBar> bars = history.GetSource(source) is null ? [] : history.GetDailyBars(spec.OrderbookId, source);
                DateOnly? last = bars.Count > 0 ? bars[^1].Bar.Date : null;
                if (last >= through)
                {
                    continue;
                }

                // A week of overlap catches restated bars; with no history, a year gives the strategies their look-back.
                DateOnly from = last is { } l ? l.AddDays(-7) : through.AddYears(-1);
                InstrumentTradingParams p = await ctx.Connection.Gateway.GetTradingParamsAsync(spec.OrderbookId, ctx.Ct).ConfigureAwait(false);
                history.UpsertInstrument(InstrumentRecord.FromTradingParams(p), "avanza-orderbook", AvanzaConnection.OrderbookSourceVersion, p.KnownAtUtc);
                var provider = new AvanzaChartImporter(ctx.Connection.Gateway, time, AvanzaConnection.PriceChartSourceVersion);
                ImportReport report = await new HistoryImporter(history, time, market.Calendar)
                    .ImportAsync(provider, spec.OrderbookId, from, through, ctx.Ct).ConfigureAwait(false);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"History: {spec.Ticker} brought up to {report.LastDate:yyyy-MM-dd} ({report.Bars.New} new bar(s), {report.Bars.Restated} restated)."));
                foreach (string warning in report.Warnings)
                {
                    output.WriteLine($"warning: {warning}");
                }
            }
        }
    }

    /// <summary>A session skips a foreign market whose latest fixing is older than this (ADR 0005).</summary>
    internal const int MaxFxAgeDays = 4;

    /// <summary>
    /// The day's FX table (ADR 0005): each foreign currency on the list brought up to date from the Riksbank, then its
    /// latest fixing on or before today. A currency whose fixing is missing or more than <see cref="MaxFxAgeDays"/> days
    /// old is left out, with a warning: its shares are skipped today and the others still trade.
    /// </summary>
    private static async Task<FxTable> FxAtStartAsync(string storePath, IReadOnlyList<PaperMarket> markets, IFxRateSource source, TimeProvider time, TextWriter output, CancellationToken ct)
    {
        var rates = new Dictionary<string, decimal>(StringComparer.Ordinal);
        DateOnly today = OrderGateway.StockholmDate(time.GetUtcNow());
        foreach (PaperMarket market in markets.Where(m => Markets.IsForeign(m.Info.Currency)))
        {
            string ccy = market.Info.Currency;
            string names = string.Join(", ", market.Specs.Select(s => s.Ticker));
            try
            {
                using HistoryStore history = HistoryStore.Open(storePath);
                string riksbank = Data.Fx.RiksbankFxSource.Riksbank.Name;
                DateOnly from = history.LatestFxRate(ccy, riksbank, today) is { } stored
                    ? stored.Rate.Date.AddDays(-7)
                    : FirstBar(history, market.Specs, today).AddDays(-InstrumentImport.FxLeadDays);
                try
                {
                    await Data.Fx.FxImporter.ImportAsync(history, source, ccy, from, today, time, ct).ConfigureAwait(false);
                }
                catch (FxUnavailableException ex)
                {
                    output.WriteLine($"WARNING: the {ccy}/SEK fixings could not be updated ({ex.Message}); using what is stored.");
                }

                if (history.LatestFxRate(ccy, riksbank, today) is not { } latest || today.DayNumber - latest.Rate.Date.DayNumber > MaxFxAgeDays)
                {
                    output.WriteLine($"WARNING: no {ccy}/SEK fixing from the last {MaxFxAgeDays} days is stored, so {names} {(market.Specs.Count == 1 ? "is" : "are")} skipped today (ADR 0005). Import it with 'qa fx import {ccy} --from {today.AddDays(-14):yyyy-MM-dd}'.");
                    continue;
                }

                rates[ccy] = latest.Rate.SekPerUnit;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"FX: {ccy} {latest.Rate.SekPerUnit:0.0000} SEK (Riksbank fixing {latest.Rate.Date:yyyy-MM-dd}) for {names}."));
            }
            catch (Exception ex) when (ex is HistoryStoreException or IOException || DataCommands.IsStoreFailure(ex))
            {
                output.WriteLine($"WARNING: the {ccy}/SEK fixings could not be read ({ex.Message}), so {names} {(market.Specs.Count == 1 ? "is" : "are")} skipped today.");
            }
        }

        return new FxTable(rates);

        static DateOnly FirstBar(HistoryStore history, IEnumerable<InstrumentSpec> specs, DateOnly today)
        {
            string source = AvanzaChartImporter.AvanzaPriceChart.Name;
            DateOnly first = today.AddYears(-1);
            if (history.GetSource(source) is not null)
            {
                foreach (InstrumentSpec spec in specs)
                {
                    IReadOnlyList<StoredBar> bars = history.GetDailyBars(spec.OrderbookId, source);
                    if (bars.Count > 0 && bars[0].Bar.Date < first)
                    {
                        first = bars[0].Bar.Date;
                    }
                }
            }

            return first;
        }
    }

    /// <summary>The last date on which any of <paramref name="instruments"/> has a bar in the panel.</summary>
    private static DateOnly LastBarDate(MarketPanel panel, int[] instruments)
    {
        for (int t = panel.Periods - 1; t >= 0; t--)
        {
            if (instruments.Any(i => !double.IsNaN(panel.Bar(t, i).Close)))
            {
                return panel.Dates[t];
            }
        }

        return panel.Dates[0];
    }

    /// <summary>The earliest next decision of the session's markets.</summary>
    private static DateTimeOffset NextDecision(IEnumerable<PaperMarket> markets, DateTimeOffset now) => markets.Min(m => m.Schedule.NextDecision(now).DecisionUtc);

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
                string currency = Markets.IsForeign(p.Currency) ? " " + p.Currency : string.Empty;
                w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {p.Ticker} ({p.OrderbookId}): {p.Quantity}, cost {p.CostBasis:N2} SEK, last fill {p.LastFillPrice}{currency}"));
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

    private static async Task PumpQuotesAsync(
        Data.Live.Broadcaster<Quote>.Subscription subscription, LatestQuotes quotes, PaperOrderChannel channel, ISessionObserver? observer, TimeProvider time,
        SpreadSampler? spreads = null)
    {
        DateTimeOffset? told = null;
        try
        {
            await foreach (Quote q in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                quotes.Set(q);
                channel.OnQuote(q);
                spreads?.Offer(q);
                DateTimeOffset now = time.GetUtcNow();
                if (observer is not null && (told is not { } t || now - t >= ObserverQuoteEvery))
                {
                    told = now;
                    observer.Quote(new QuoteTick(now, q.OrderbookId, q.Bid, q.Ask, q.Last));
                }
            }
        }
        catch (Exception) when (subscription.Reader.Completion.IsFaulted)
        {
            // The composer's failure is reported by its run task.
        }
    }

    /// <summary>
    /// Plan 17 step A2, after the session: its once-a-minute bid/ask samples go to the store, and after Stockholm's
    /// close today's 1- and 5-minute bars are collected for the allowlist's Stockholm shares and the research list
    /// (public chart, no login). Research data only: a failure is a warning and changes nothing about the day.
    /// </summary>
    private static async Task KeepIntradayResearchAsync(Ctx ctx, string storePath, PaperSetup setup, SpreadSampler spreads, TimeProvider time, TextWriter output)
    {
        try
        {
            IReadOnlyList<SpreadSample> samples = spreads.Drain();
            if (samples.Count > 0)
            {
                using HistoryStore history = HistoryStore.Open(storePath);
                history.RegisterSource(SpreadSampler.Source);
                int added = history.AddSpreadSamples(samples, SpreadSampler.Source);
                output.WriteLine($"Spreads: {added} bid/ask sample(s) stored for intraday research.");
            }

            DateTimeOffset now = time.GetUtcNow();
            if (setup.Schedule.Plan(OrderGateway.StockholmDate(now)) is not { } today || now < today.CloseUtc)
            {
                return;
            }

            IReadOnlyList<IntradayName> names = CollectedShares(storePath, setup.ConfigDir, output);
            if (names.Count > 0)
            {
                await CollectIntradayAsync(ctx.Connection.Gateway, storePath, names, ChartPeriod.Today, IntradayImporter.Resolutions, time, output, ctx.Ct, setup.Calendar).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("Intraday bars not collected (stopped); run 'qa intraday import' this evening.");
        }
        catch (Exception ex) when (ex is BrokerException or ArgumentException or HistoryStoreException or IOException || DataCommands.IsStoreFailure(ex))
        {
            output.WriteLine($"WARNING: intraday research data not kept ({ex.Message}); run 'qa intraday import' this evening.");
        }
    }

    /// <summary>An observer gets at most one quote a second per instrument: enough for a chart, cheap for the session.</summary>
    private static readonly TimeSpan ObserverQuoteEvery = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The instruments for an observer, each with its last stored close before today (what "today's change" is measured
    /// from). The store was just brought up to date; if it can't be read, the closes are left out.
    /// </summary>
    private static List<ObservedInstrument> ObservedInstruments(string storePath, IReadOnlyList<InstrumentSpec> specs, DateOnly today)
    {
        var closes = new Dictionary<OrderbookId, decimal>();
        try
        {
            using HistoryStore history = DataCommands.OpenExisting(storePath);
            string source = AvanzaChartImporter.AvanzaPriceChart.Name;
            if (history.GetSource(source) is not null)
            {
                foreach (InstrumentSpec spec in specs)
                {
                    IReadOnlyList<StoredBar> bars = history.GetDailyBars(spec.OrderbookId, source, to: today.AddDays(-1));
                    if (bars.Count > 0)
                    {
                        closes[spec.OrderbookId] = bars[^1].Bar.Close;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or HistoryStoreException or IOException || DataCommands.IsStoreFailure(ex))
        {
            // Informational only: the tiles then measure today's change from the first quote.
        }

        return [.. specs.Select(s => new ObservedInstrument(s.OrderbookId, s.Ticker, s.Name, closes.TryGetValue(s.OrderbookId, out decimal c) ? c : null))];
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
        TradingSchedule Schedule, IReadOnlyList<string> Warnings, string ConfigDir)
    {
        /// <summary>
        /// The list's markets (ADR 0005), Stockholm first: each with its calendar, schedule and shares. A share in another
        /// currency, or a foreign market without a calendar or a courtage, is left out with a warning.
        /// </summary>
        public List<PaperMarket> Markets(IReadOnlyList<InstrumentSpec> specs, TextWriter output)
        {
            foreach (InstrumentSpec odd in specs.Where(s => Core.Market.Markets.ForCurrency(s.Currency) is null))
            {
                output.WriteLine($"WARNING: {odd.Ticker} trades in {odd.Currency}; the program trades shares in {Core.Market.Markets.CurrencyList} (ADR 0005). It is skipped.");
            }

            var markets = new List<PaperMarket>();
            foreach (MarketInfo info in Core.Market.Markets.All)
            {
                InstrumentSpec[] own = [.. specs.Where(s => s.Currency == info.Currency)];
                if (own.Length == 0)
                {
                    continue;
                }

                if (!Core.Market.Markets.IsForeign(info.Currency))
                {
                    markets.Add(new PaperMarket(info, Calendar, Schedule, own));
                    continue;
                }

                string names = string.Join(", ", own.Select(s => s.Ticker));
                if (Costs.ForeignFor(info.Currency) is null)
                {
                    output.WriteLine($"WARNING: the courtage class {Costs.Name} has no courtage for {info.Currency} shares, so {names} {(own.Length == 1 ? "is" : "are")} skipped (add foreign_courtage.{info.Currency} to costs.{Costs.Name}.json).");
                    continue;
                }

                MarketCalendar calendar;
                try
                {
                    calendar = MarketCalendarLoader.LoadDirectory(ConfigDir, info.Mic);
                }
                catch (CalendarConfigException ex)
                {
                    output.WriteLine($"WARNING: no {info.Mic} calendar ({ex.Message}), so {names} {(own.Length == 1 ? "is" : "are")} skipped.");
                    continue;
                }

                if (!calendar.IsVerified)
                {
                    output.WriteLine($"WARNING: the {info.Mic} calendar for {string.Join(", ", calendar.UnverifiedYears)} is not verified yet (allowed in Paper).");
                }

                markets.Add(new PaperMarket(info, calendar, new TradingSchedule(calendar, Limits, Paper.DecisionTime, Calendar), own));
            }

            return markets;
        }

        public static PaperSetup Load(string configDir, string promotionDir)
        {
            PromotionState promotion = PromotionState.Load(promotionDir);
            TradingMode mode = promotion.Effective(TradingMode.Paper);
            RiskLimits limits = RiskLimits.Load(Path.Combine(configDir, RiskLimits.FileName));
            PaperConfig paper = PaperConfig.Load(Path.Combine(configDir, PaperConfig.FileName));
            Universe universe = Universe.Load(Path.Combine(configDir, Universe.FileName));
            if (universe.Entries.Count == 0 && universe.Exiting.Count == 0)
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

            return new PaperSetup(mode, promotion, limits, paper, universe, costs, calendar, new TradingSchedule(calendar, limits, paper.DecisionTime), warnings, configDir);
        }
    }

    /// <summary>One market of a Paper session (ADR 0005): its calendar and schedule, and the list's shares on it.</summary>
    private sealed record PaperMarket(MarketInfo Info, MarketCalendar Calendar, TradingSchedule Schedule, IReadOnlyList<InstrumentSpec> Specs)
    {
        public bool Trades(InstrumentSpec spec) => spec.Currency == Info.Currency;

        /// <summary>"XNYS (AAPL): decides 09:40 New York (15:40 Stockholm), closes 22:00 Stockholm." for today, or its next day.</summary>
        public string Describe(DateTimeOffset now)
        {
            SessionPlan next = Schedule.Plan(Calendar.LocalDate(now)) ?? Schedule.NextDecision(now);
            string clock = Calendar.TimeZoneId[(Calendar.TimeZoneId.IndexOf('/', StringComparison.Ordinal) + 1)..].Replace('_', ' ');
            string decides = Core.Market.Markets.IsForeign(Info.Currency)
                ? $"{MarketTime.ToZone(next.DecisionUtc, Calendar.TimeZone):HH\\:mm} {clock} ({MarketTime.ToStockholm(next.DecisionUtc):HH\\:mm} Stockholm)"
                : $"{MarketTime.ToStockholm(next.DecisionUtc):HH\\:mm} Stockholm";
            string day = next.Date == Calendar.LocalDate(now) ? string.Empty : $" on {next.Date:ddd yyyy-MM-dd}";
            return $"{Info.Mic} ({string.Join(", ", Specs.Select(s => s.Ticker))}): decides {decides}, closes {MarketTime.ToStockholm(next.CloseUtc):HH\\:mm} Stockholm{day}.";
        }
    }
}
