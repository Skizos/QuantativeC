using System.CommandLine;
using System.Globalization;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Avanza;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Live;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Trading;
using QuantAnalyst.Trading.Accounts;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Confirm;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Live;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Oms;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Pipeline;
using QuantAnalyst.Trading.Reconciliation;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Cli.Commands;

/// <summary>
/// Confirm mode on the command line (Phase 7 step 5; plan 07 "Decisions"):
/// <list type="bullet">
/// <item><c>qa trade run --mode confirm</c>: the daily session with an order card per order;</item>
/// <item><c>qa rebalance</c>: what the strategy would trade now, on the live account; sends nothing;</item>
/// <item><c>qa rebalance --mode confirm --execute</c>: the same cards now instead of at the decision time.</item>
/// </list>
/// Only the owner starts the live ones: the hook blocks them for Claude Code (rules 1 and 7), and the first Confirm
/// startup check refuses a process Claude Code started. Every check that needs no connection runs before the login.
/// </summary>
internal static partial class AvanzaCommands
{
    private static Command Trade(AvanzaCliServices services)
    {
        var command = new Command("trade", "Live trading (Phase 7). Confirm mode: an order card per order, sent only after you type its ticker and JA.");
        var options = new LiveOptions(execute: false);
        var run = new Command(
            "run",
            "Run one Confirm session: the startup checks, one login, the history up to yesterday, then at the decision time an order card per order, each re-checked before it is sent. Stopping it cancels its working orders.");
        options.AddTo(run);
        run.SetAction(parse => Run(parse, services, options.Common, record: null, (ctx, output) =>
        {
            RequireConfirm(parse.GetValue(options.Mode), "qa trade run");
            return ConfirmSessionAsync(services, parse, options, ctx, output, decideAtStart: false);
        }, live: true));
        command.Subcommands.Add(run);
        return command;
    }

    private static Command Rebalance(AvanzaCliServices services)
    {
        var options = new LiveOptions(execute: true);
        var command = new Command(
            "rebalance",
            "Show what the strategy would trade now on your live account (R1), and send nothing. With --mode confirm --execute: run the order cards now, the same way as 'qa trade run'.");
        options.AddTo(command);
        command.SetAction(parse => Run(parse, services, options.Common, record: null, (ctx, output) =>
        {
            if (!parse.GetValue(options.Execute!))
            {
                return RebalancePlanAsync(services, parse, options, ctx, output);
            }

            RequireConfirm(parse.GetValue(options.Mode), "qa rebalance --execute");
            return ConfirmSessionAsync(services, parse, options, ctx, output, decideAtStart: true);
        }, live: true));
        return command;
    }

    private static void RequireConfirm(string? mode, string what)
    {
        switch (mode?.Trim().ToLowerInvariant())
        {
            case "confirm":
                return;
            case null or "":
                throw new ArgumentException($"{what} needs --mode confirm: every live start names its mode. (Paper runs with: qa paper run)");
            case "paper":
                throw new ArgumentException("Paper runs with: qa paper run");
            case "auto":
                throw new ArgumentException("Auto is not available before Phase 8: every live order needs your typed confirmation.");
            default:
                throw new ArgumentException($"Unknown mode '{mode}': {what} runs only in Confirm (--mode confirm).");
        }
    }

    private sealed class LiveOptions
    {
        public LiveOptions(bool execute)
        {
            Execute = execute ? new Option<bool>("--execute") { Description = "Run the order cards now (needs --mode confirm) instead of only showing the plan" } : null;
        }

        public Common Common { get; } = new();

        public Option<string?> Mode { get; } = new("--mode") { Description = "confirm: an order card per order (the only live mode; Auto arrives in Phase 8)" };

        public Option<bool>? Execute { get; }

        public Option<string?> Strategy { get; } = new("--strategy") { Description = "Strategy (default: the one saved with 'qa paper strategy')" };

        public Option<string[]> Param { get; } = new("--param") { Description = "Strategy parameter key=value (repeatable; with --strategy)", AllowMultipleArgumentsPerToken = false };

        public Option<double?> Duration { get; } = new("--duration") { Description = "Seconds to run (default: until two minutes after today's close)" };

        public Option<string?> ConfigDir { get; } = TradingCommands.ConfigDirOption();

        public Option<string> Store { get; } = DataCommands.StoreOption();

        public Option<string> AuditDir { get; } = new("--audit-dir") { Description = "Audit folder", DefaultValueFactory = _ => TradingCommands.DefaultAuditDir };

        public Option<string> KillFile { get; } = new("--kill-file") { Description = "The kill flag file", DefaultValueFactory = _ => TradingCommands.DefaultKillFile };

        public Option<string> PromotionDir { get; } = new("--promotion-dir") { Description = "Promotion state folder", DefaultValueFactory = _ => TradingCommands.DefaultPromotionDir };

        public Option<string> ReportsDir { get; } = new("--reports-dir") { Description = "End-of-day reports folder", DefaultValueFactory = _ => TradingCommands.DefaultReportsDir };

        public void AddTo(Command command)
        {
            Common.AddTo(command, json: false);
            foreach (Option o in new Option?[] { Mode, Execute, Strategy, Param, Duration, ConfigDir, Store, AuditDir, KillFile, PromotionDir, ReportsDir }.OfType<Option>())
            {
                command.Options.Add(o);
            }
        }
    }

    /// <summary>
    /// A Confirm session: the checks that need no connection (before any login), one login, R1 and the channel, then the
    /// full checks. Only with every check passed does the gateway get the live authorization.
    /// </summary>
    private static async Task<int> ConfirmSessionAsync(AvanzaCliServices services, ParseResult parse, LiveOptions o, Ctx ctx, TextWriter output, bool decideAtStart)
    {
        TimeProvider time = services.Time;
        string stateDir = parse.GetValue(o.Common.StateDir)!;
        string auditDir = parse.GetValue(o.AuditDir)!;
        string promotionDir = parse.GetValue(o.PromotionDir)!;
        PaperSetup setup = PaperSetup.Load(TradingCommands.ResolveConfigDir(parse.GetValue(o.ConfigDir)), promotionDir);
        StrategyDefinition definition = ChooseStrategy(parse.GetValue(o.Strategy), parse.GetValue(o.Param), setup.Paper);
        string storePath = parse.GetValue(o.Store)!;
        output.WriteLine($"Confirm session: {definition.Spec.Describe()} on {string.Join(", ", setup.Universe.Entries.Select(e => e.Ticker))}; model courtage class {setup.Costs.DisplayName ?? setup.Costs.Name}.");

        using SessionLock sessionLock = SessionLock.Acquire(stateDir, time);
        var audit = new AuditLog(auditDir, time);
        IBrokerOrderChannel channel = services.OrderChannel(ctx.Connection);
        var inputs = new ConfirmStartupInputs
        {
            PromotionDirectory = promotionDir,
            PromotionKeys = PromotionKeysOrUnavailable(services),
            EvidenceBaseDirectory = Environment.CurrentDirectory,
            Calendar = setup.Calendar,
            Costs = setup.Costs,
            KillFile = parse.GetValue(o.KillFile)!,
            StateDirectory = stateDir,
            SessionLock = sessionLock,
            AuditDirectory = auditDir,
            Account = null,
            Channel = channel,
            GetVariable = services.GetVariable,
            Time = time,
        };

        ConfirmStartupResult beforeLogin = ConfirmStartup.Check(inputs);
        audit.Append("confirm-startup", new { stage = "before login", checks = beforeLogin.Checks.Select(c => c.ToString()) });
        if (!ConfirmStartup.ReadyToConnect(beforeLogin))
        {
            WriteChecks(output, beforeLogin, "Confirm can't start; nothing was logged in to or sent:");
            return ExitHalt;
        }

        await ctx.Connection.Authenticator.LoginAsync(ctx.Ct).ConfigureAwait(false);
        IReadOnlyList<TradingAccount> accounts = await ctx.Connection.Gateway.GetTradingAccountsAsync(ctx.Ct).ConfigureAwait(false);
        ConfirmStartupResult startup = ConfirmStartup.Check(inputs with { Account = AccountAllowlist.FromEnvironment(accounts, services.GetVariable) });
        audit.Append("confirm-startup", new { stage = "after login", passed = startup.Passed, checks = startup.Checks.Select(c => c.ToString()) });
        if (startup.Authorization is not { } live)
        {
            WriteChecks(output, startup, "Confirm can't start; nothing was sent:");
            return ExitHalt;
        }

        WriteChecks(output, startup, "Confirm startup checks: all passed.");
        var halts = new HaltController(audit, time);
        List<InstrumentSpec> specs = await LiveSpecsAsync(ctx, setup).ConfigureAwait(false);
        await RefreshHistoryForLiveAsync(ctx, storePath, setup, time, output).ConfigureAwait(false);

        var quotes = new LiveQuotes();
        var account = new GatewayAccountState(ctx.Connection.Gateway, live.Account, quotes, time, stateDir);
        var risk = new PreTradeRiskEngine(setup.Limits);
        var oms = new OrderManager(audit, halts, time);
        var prompt = new ConfirmationPrompt(services.Input, time);
        var env = new GatewayEnvironment
        {
            Mode = TradingMode.Confirm,
            Instruments = new InstrumentCatalog(specs),
            Quotes = quotes,
            Account = account,
            Calendar = setup.Calendar,
            Universe = setup.Universe,
            AllowedAccountIds = new HashSet<string>(StringComparer.Ordinal) { live.Account.Value },
            Fees = (order, spec) => ModelFees.For(setup.Costs, order.Value, spec.Currency),
            CourtageVerified = setup.Costs.Verified,
            Preflight = ctx.Connection.CreatePreflight(),
            Confirmation = new ConsoleOrderConfirmation(output, prompt, time),
            Live = live,
        };
        using var gateway = new OrderGateway(channel, env, risk, oms, halts, audit, time);
        using var kill = new KillSwitch(gateway, halts, audit, time, parse.GetValue(o.KillFile)!, stateDir, account, setup.Limits.DailyLossStopPct);
        kill.Alerted += message => output.WriteLine("ALERT: " + message);
        var reconciler = new Reconciler(oms, halts, audit, time, live.Account);
        IReadOnlyList<string> tickers = [.. specs.Select(s => s.Ticker)];

        async Task<PlanResult> Plan(CancellationToken ct)
        {
            DateTimeOffset now = time.GetUtcNow();
            double[] targets = TargetsAtLastBar(storePath, tickers, setup, definition, now);
            AccountSnapshot snapshot = await account.GetAsync(ct).ConfigureAwait(false);
            return DailyPlanner.Plan(targets, specs, snapshot, quotes, risk, new ExecutionOptions(), definition.Spec.Describe(), now);
        }

        DateTimeOffset start = time.GetUtcNow();
        DateTimeOffset stopAt = StopAt(parse.GetValue(o.Duration), setup, start, decideAtStart ? "qa rebalance --mode confirm --execute" : "qa trade run --mode confirm");
        output.WriteLine($"Running until {MarketTime.ToStockholm(stopAt):yyyy-MM-dd HH:mm} (Stockholm). {(decideAtStart ? "The cards start now" : $"The cards start at {setup.Paper.DecisionTime:HH\\:mm}")}; answer each within 30 s. Stop early with Ctrl+C or 'qa kill'; stopping cancels the session's working orders.");
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
        var pumps = subscriptions.Select(s => PumpLiveQuotesAsync(s, quotes)).ToList();
        var feeds = composers.Select(c => StopAllOnFailure(c.RunAsync(stop.Token), stop)).ToList();
        string EndOfDayReport(DateOnly day)
        {
            Trading.Reports.EodReport report = Trading.Reports.EodReport.Build(audit.Directory, day, time);
            string saved = report.Save(parse.GetValue(o.ReportsDir)!);
            return $"{report.Summary()} Saved to {saved}.";
        }

        var session = new ConfirmSession(gateway, account, kill, reconciler, new GatewayBrokerState(ctx.Connection.Gateway, time), halts, setup.Schedule, audit, time,
            Plan, output, decideAtStart, EndOfDayReport);
        ConfirmSessionSummary summary;
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

            foreach (Broadcaster<Quote>.Subscription s in subscriptions)
            {
                s.Dispose();
            }

            await Task.WhenAll(pumps).ConfigureAwait(false);
        }

        output.WriteLine();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Session over: {summary.Cards} card(s): {summary.Accepted} sent and accepted, {summary.Skipped} skipped, {summary.RiskRejected} stopped by the risk checks, {summary.Blocked} blocked; {summary.Filled} with fills."));
        if (summary.AccountValue is { } value)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Account {live.Account.Masked}: value {value:N2} SEK (start of day {summary.StartOfDayValue:N2})."));
        }

        output.WriteLine($"Reconciliation: {(summary.ReconciliationClean ? "clean" : "MISMATCH or FAILED (see the audit log)")}. Audit: {audit.Directory} (check with 'qa audit verify').");
        return summary.Killed || halts.IsHalted ? ExitHalt : 0;
    }

    /// <summary>
    /// <c>qa rebalance</c> without <c>--execute</c>: one login, R1, the live account and a quote per instrument, then the
    /// plan. Read-only: nothing is sent, and there is no order card.
    /// </summary>
    private static async Task<int> RebalancePlanAsync(AvanzaCliServices services, ParseResult parse, LiveOptions o, Ctx ctx, TextWriter output)
    {
        TimeProvider time = services.Time;
        PaperSetup setup = PaperSetup.Load(TradingCommands.ResolveConfigDir(parse.GetValue(o.ConfigDir)), parse.GetValue(o.PromotionDir)!);
        StrategyDefinition definition = ChooseStrategy(parse.GetValue(o.Strategy), parse.GetValue(o.Param), setup.Paper);
        string storePath = parse.GetValue(o.Store)!;

        await ctx.Connection.Authenticator.LoginAsync(ctx.Ct).ConfigureAwait(false);
        AllowlistResult allowed = AccountAllowlist.FromEnvironment(await ctx.Connection.Gateway.GetTradingAccountsAsync(ctx.Ct).ConfigureAwait(false), services.GetVariable);
        if (allowed is not { Allowed: true, Account: { } tradingAccount })
        {
            output.WriteLine(allowed.Describe());
            return 1;
        }

        List<InstrumentSpec> specs = await LiveSpecsAsync(ctx, setup).ConfigureAwait(false);
        await RefreshHistoryForLiveAsync(ctx, storePath, setup, time, output).ConfigureAwait(false);
        var quotes = new LiveQuotes();
        foreach (InstrumentSpec spec in specs)
        {
            quotes.Set(QuoteFromSnapshot(await ctx.Connection.Gateway.GetMarketSnapshotAsync(spec.OrderbookId, ctx.Ct).ConfigureAwait(false), time.GetUtcNow()));
        }

        var account = new GatewayAccountState(ctx.Connection.Gateway, tradingAccount.Id, quotes, time, parse.GetValue(o.Common.StateDir)!);
        AccountSnapshot snapshot = await account.GetAsync(ctx.Ct).ConfigureAwait(false);
        DateTimeOffset now = time.GetUtcNow();
        var risk = new PreTradeRiskEngine(setup.Limits);
        PlanResult plan = DailyPlanner.Plan(
            TargetsAtLastBar(storePath, [.. specs.Select(s => s.Ticker)], setup, definition, now), specs, snapshot, quotes, risk, new ExecutionOptions(), definition.Spec.Describe(), now);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Rebalance plan for {tradingAccount.Id.Masked} ({definition.Spec.Describe()}): value {snapshot.AccountValue:N2} SEK, available {snapshot.AvailableCash:N2}."));
        foreach (string note in plan.Notes)
        {
            output.WriteLine("  " + note);
        }

        output.WriteLine(plan.Intents.Count == 0
            ? "Nothing to trade now. Nothing was sent."
            : $"{plan.Intents.Count} order(s) would be proposed, one card each. Nothing was sent. To trade them now: qa rebalance --mode confirm --execute");
        return 0;
    }

    private static async Task<List<InstrumentSpec>> LiveSpecsAsync(Ctx ctx, PaperSetup setup)
    {
        var specs = new List<InstrumentSpec>();
        foreach (UniverseEntry entry in setup.Universe.Entries)
        {
            InstrumentTradingParams p = await ctx.Connection.Gateway.GetTradingParamsAsync(entry.OrderbookId, ctx.Ct).ConfigureAwait(false);
            specs.Add(new InstrumentSpec(entry.OrderbookId, entry.Ticker, p.Name, p.Currency, Math.Max(1, p.TradingUnit), p.TickSizes, TickTableVerified: true, p.Isin, p.MarketPlace));
        }

        return specs;
    }

    private static async Task RefreshHistoryForLiveAsync(Ctx ctx, string storePath, PaperSetup setup, TimeProvider time, TextWriter output)
    {
        try
        {
            await RefreshHistoryAsync(ctx, storePath, setup, time, output).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BrokerException or HistoryImportException)
        {
            // Informational data (ADR 0002 Tier B): the decision refuses stale history itself.
            output.WriteLine($"WARNING: the history could not be brought up to date ({ex.Message}); the decision uses what is stored.");
        }
    }

    /// <summary>The strategy's target weights on the stored daily bars through the last trading day (never today's).</summary>
    private static double[] TargetsAtLastBar(string storePath, IReadOnlyList<string> tickers, PaperSetup setup, StrategyDefinition definition, DateTimeOffset now)
    {
        DateOnly yesterday = PreviousTradingDay(setup.Calendar, OrderGateway.StockholmDate(now));
        using HistoryStore history = DataCommands.OpenExisting(storePath);
        MarketPanel panel = BacktestCommands.LoadStorePanel(history, tickers, null, yesterday);
        if (panel.Dates[^1] != yesterday)
        {
            throw new InvalidOperationException(
                $"the history ends {panel.Dates[^1]:yyyy-MM-dd}, not on the last trading day {yesterday:yyyy-MM-dd}; run 'qa history import' for {string.Join(", ", tickers)} first. No orders today.");
        }

        return StrategyReplay.DecideAtLastBar(panel, definition.Factory(panel));
    }

    private static DateTimeOffset StopAt(double? seconds, PaperSetup setup, DateTimeOffset start, string command) =>
        seconds is { } s
            ? (s is > 0 and <= 16 * 3600 ? start.AddSeconds(s) : throw new ArgumentException("--duration must be in (0, 57600] seconds."))
            : setup.Schedule.Plan(OrderGateway.StockholmDate(start)) is { } today && start < today.CloseUtc.AddMinutes(2)
                ? today.CloseUtc.AddMinutes(2)
                : throw new ArgumentException(
                    $"No session left today. The next one is {MarketTime.ToStockholm(setup.Schedule.NextDecision(start).DecisionUtc):dddd yyyy-MM-dd}: start '{command}' that morning before {setup.Paper.DecisionTime:HH\\:mm}.");

    private static void WriteChecks(TextWriter output, ConfirmStartupResult result, string heading)
    {
        output.WriteLine(heading);
        foreach (StartupCheck check in result.Checks)
        {
            output.WriteLine("  " + check);
        }
    }

    /// <summary>The owner's key store; where it can't exist (not Windows) a store that says why when read.</summary>
    private static IPromotionKeyStore PromotionKeysOrUnavailable(AvanzaCliServices services)
    {
        try
        {
            return services.PromotionKeys();
        }
        catch (ArgumentException ex)
        {
            return new UnavailableKeyStore(ex.Message);
        }
    }

    private static Quote QuoteFromSnapshot(MarketSnapshot m, DateTimeOffset now)
    {
        DepthLevel? top = m.Depth.Count > 0 ? m.Depth[0] : null;
        return new Quote(m.OrderbookId, m.Bid, top?.BidVolume ?? 0m, m.Ask, top?.AskVolume ?? 0m, m.Last, m.TimeOfLastUtc, m.TotalVolumeTraded,
            m.Depth, QuoteSource.Poll, m.DepthReceivedUtc, now, now, now, false, null);
    }

    private static async Task PumpLiveQuotesAsync(Broadcaster<Quote>.Subscription subscription, LiveQuotes quotes)
    {
        try
        {
            await foreach (Quote q in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                quotes.Set(q);
            }
        }
        catch (Exception) when (subscription.Reader.Completion.IsFaulted)
        {
            // The composer's failure is reported by its run task.
        }
    }

    /// <summary>The newest composed quote per instrument.</summary>
    private sealed class LiveQuotes : IQuoteSource
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<OrderbookId, Quote> _latest = new();

        public Quote? Latest(OrderbookId id) => _latest.GetValueOrDefault(id);

        public void Set(Quote q) => _latest[q.OrderbookId] = q;
    }

    private sealed class UnavailableKeyStore(string why) : IPromotionKeyStore
    {
        public string Name => "the promotion key store";

        public byte[]? Read() => throw new InvalidOperationException(why);

        public void Create(byte[] key) => throw new InvalidOperationException(why);
    }
}
