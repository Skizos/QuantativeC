using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Cli.Output;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Native;

namespace QuantAnalyst.Cli.Commands;

/// <summary>
/// Phase 5 research verbs: <c>qa backtest run|sweep</c> and <c>qa trials list|verify</c>. Offline. Every backtest is
/// appended to the TrialLedger, whatever its outcome (CLAUDE.md "TrialLedger logs EVERY evaluation").
/// </summary>
internal static class BacktestCommands
{
    public static IEnumerable<Command> Create()
    {
        yield return CostsCommand();

        var backtest = new Command("backtest", "Backtest a strategy on synthetic data or the local history store (docs/plans/05-phase5-backtesting.md). Offline; model output, not advice.");
        backtest.Subcommands.Add(Run());
        backtest.Subcommands.Add(Sweep());
        yield return backtest;

        var trials = new Command("trials", "The TrialLedger: every backtest ever run, hash-chained (research/trial-ledger.jsonl).");
        trials.Subcommands.Add(List());
        trials.Subcommands.Add(Verify());
        yield return trials;
    }

    // ---- shared options ---------------------------------------------------------------------------------

    private sealed class Inputs
    {
        public Option<string> Strategy { get; } = new("--strategy") { Description = $"Strategy: {string.Join(", ", StrategyCatalog.Names)}", Required = true };

        public Option<string[]> Param { get; } = new("--param") { Description = "Strategy parameter key=value (repeatable)", AllowMultipleArgumentsPerToken = false };

        public Option<string?> Synthetic { get; } = new("--synthetic") { Description = "Synthetic GBM data: <instruments>x<bars>, e.g. 300x2520" };

        public Option<string?> Tickers { get; } = new("--tickers") { Description = "Comma-separated tickers from the history store (qa history import first)" };

        public Option<string?> From { get; } = new("--from") { Description = "First bar date (yyyy-MM-dd)" };

        public Option<string?> To { get; } = new("--to") { Description = "Last bar date (yyyy-MM-dd); defaults to the day before a locked holdout" };

        public Option<ulong> Seed { get; } = new("--seed") { Description = $"Seed for synthetic data (default {QaCli.DefaultSeed})" };

        public Option<double> Drift { get; } = new("--drift") { Description = "Synthetic annual drift (default 0: a random walk)" };

        public Option<string?> Costs { get; } = new("--costs") { Description = "Courtage class / cost model: avanza-start, avanza-mini, avanza-small, avanza-medium, avanza-fastpris (config/costs.<name>.json) or a path. Default: config/backtest-defaults.json" };

        public Option<string> Order { get; } = new("--order") { Description = "Order type: limit, moo (market on open) or moc (market on close)", DefaultValueFactory = _ => "limit" };

        public Option<double> LimitOffset { get; } = new("--limit-offset-bps") { Description = "Limit distance from the decision close toward the market", DefaultValueFactory = _ => 50 };

        public Option<decimal?> Cash { get; } = new("--cash") { Description = "Initial cash (SEK). Default: config/backtest-defaults.json" };

        public Option<string?> ConfigDir { get; } = new("--config-dir") { Description = "Folder with holdout.json and costs.*.json (default: ./config, then next to qa)" };

        public Option<string> Store { get; } = DataCommands.StoreOption();

        public Option<string?> Ledger { get; } = LedgerOption();

        public Option<bool> Json { get; } = new("--json") { Description = "JSON output" };

        public void AddTo(Command command)
        {
            foreach (Option o in new Option[] { Strategy, Param, Synthetic, Tickers, From, To, Seed, Drift, Costs, Order, LimitOffset, Cash, ConfigDir, Store, Ledger, Json })
            {
                command.Options.Add(o);
            }
        }
    }

    private static Option<string?> LedgerOption() =>
        new("--ledger") { Description = $"Ledger file (default: <repository>/{TrialLedger.DefaultPath})" };

    /// <summary>Everything a run needs except the strategy (shared by run and sweep).</summary>
    internal sealed record Setup(BacktestRequest Template, IReadOnlyList<string> Notes);

    private static Setup Prepare(ParseResult parse, Inputs o, StrategyDefinition strategy)
    {
        string configDir = ResolveConfigDir(parse.GetValue(o.ConfigDir));
        HoldoutPolicy holdout = HoldoutPolicy.Load(Path.Combine(configDir, HoldoutPolicy.FileName));
        BacktestDefaults defaults = BacktestDefaults.Load(configDir);
        CostModel costs = LoadCosts(configDir, parse.GetValue(o.Costs) ?? defaults.Costs);
        decimal cash = parse.GetValue(o.Cash) ?? defaults.Cash;
        if (cash <= 0)
        {
            throw new ArgumentException("--cash must be > 0.");
        }

        if (costs.EligibleBelowCapital is { } limit && cash >= limit)
        {
            throw new ArgumentException(
                $"{costs.DisplayName ?? costs.Name} can only be chosen with less than {limit.ToString("N0", CultureInfo.InvariantCulture)} SEK, "
                + $"but --cash is {cash.ToString("N0", CultureInfo.InvariantCulture)}. Use less cash or another class (see 'qa costs').");
        }

        var notes = new List<string>();
        DateOnly? from = DataCommands.ParseDate(parse.GetValue(o.From), "--from");
        DateOnly? to = DataCommands.ParseDate(parse.GetValue(o.To), "--to");

        string? synthetic = parse.GetValue(o.Synthetic), tickers = parse.GetValue(o.Tickers);
        if (synthetic is not null && tickers is not null)
        {
            throw new ArgumentException("Give one data source: --synthetic <N>x<T> or --tickers A,B,... (without either: the allowlist).");
        }

        if (synthetic is null && tickers is null)
        {
            tickers = AllowlistTickers(configDir)
                      ?? throw new ArgumentException("Give a data source: --tickers A,B,... (imported history) or --synthetic <N>x<T>. Without either, the backtest uses the allowlist, which is empty: qa universe add ERIC-B");
            notes.Add($"Tickers: the allowlist in {Path.Combine(configDir, Trading.Risk.Universe.FileName)} ({tickers}).");
        }

        ulong seed = 0;
        MarketPanel data;
        if (synthetic is not null)
        {
            seed = QaCli.SeedOrDefault(parse.GetValue(o.Seed));
            (int n, int periods) = ParseShape(synthetic);
            data = SyntheticMarket.Generate(new SyntheticMarketOptions { Instruments = n, Periods = periods, Seed = seed, AnnualDrift = parse.GetValue(o.Drift) });
        }
        else
        {
            // With a locked holdout and no --to, holdout bars are not even read from the store.
            DateOnly? readTo = to is null && holdout.Locked ? holdout.Start.AddDays(-1) : to;
            using HistoryStore store = DataCommands.OpenExisting(parse.GetValue(o.Store)!);
            data = LoadStorePanel(store, QaCli.SplitList(tickers), from, readTo);
            notes.Add("Universe: current names only (survivorship-biased: delisted companies are missing).");
            if (data.Instruments.Where(i => i.ForeignCurrency).ToList() is { Count: > 0 } foreign)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Foreign shares (ADR 0005): {string.Join(", ", foreign.Select(i => $"{i.Symbol} ({i.Currency}, last fixing {i.LastSekPerUnit:0.0000} SEK)"))}. Their prices are in SEK at each day's Riksbank fixing, so FX moves count; their tick table and courtage minimum are converted at the last fixing (an approximation); they pay the class's foreign courtage (UNVERIFIED until checked) and FX fee."));
            }
            if (readTo != to)
            {
                notes.Add($"Data read up to {readTo:yyyy-MM-dd}: the final holdout from {holdout.Start:yyyy-MM-dd} is locked.");
            }
        }

        // Without --to, a locked holdout ends the data the day before it starts. An explicit --to into it is refused
        // (and logged) by the runner.
        if (to is null && holdout.Locked && holdout.Touches(data.Dates[^1]))
        {
            to = holdout.Start.AddDays(-1);
            notes.Add($"Data clipped to end {to:yyyy-MM-dd}: the final holdout from {holdout.Start:yyyy-MM-dd} is locked.");
        }

        if (from is not null || to is not null)
        {
            data = data.Between(from ?? DateOnly.MinValue, to ?? DateOnly.MaxValue);
        }

        var execution = new ExecutionOptions
        {
            OrderType = parse.GetValue(o.Order) switch
            {
                "limit" => BacktestOrderType.Limit,
                "moo" => BacktestOrderType.MarketOnOpen,
                "moc" => BacktestOrderType.MarketOnClose,
                string other => throw new ArgumentException($"--order: '{other}' is not limit, moo or moc."),
                null => BacktestOrderType.Limit,
            },
            LimitOffsetBps = parse.GetValue(o.LimitOffset),
        };
        var template = new BacktestRequest
        {
            Data = data,
            Strategy = strategy,
            Costs = costs,
            Holdout = holdout,
            InitialCash = cash,
            Execution = execution,
            Seed = seed,
            Ledger = new TrialLedger(ResolveLedger(parse.GetValue(o.Ledger))),
            GitCommit = Analytics.Backtesting.GitInfo.TryGetCommit(Directory.GetCurrentDirectory()),
        };
        return new Setup(template, notes);
    }

    // ---- qa backtest run --------------------------------------------------------------------------------

    private static Command Run()
    {
        var o = new Inputs();
        var command = new Command("run", "Run one backtest and log it to the TrialLedger.");
        o.AddTo(command);
        command.SetAction(parse => Execute(parse, w =>
        {
            StrategyDefinition strategy = StrategyCatalog.Create(parse.GetValue(o.Strategy)!, ParseParams(parse.GetValue(o.Param)));
            Setup setup = Prepare(parse, o, strategy);
            BacktestResult result = BacktestRunner.Run(setup.Template);
            if (parse.GetValue(o.Json))
            {
                w.WriteLine(JsonSerializer.Serialize(new { record = result.Record, notes = setup.Notes, ledger = setup.Template.Ledger!.Path }, QaCli.Json));
                return result.Ok ? 0 : 2;
            }

            WriteHeader(w, setup, result.Record);
            WriteResult(w, result, setup.Template);
            return result.Ok ? 0 : 2;
        }));
        return command;
    }

    // ---- qa backtest sweep ------------------------------------------------------------------------------

    private static Command Sweep()
    {
        var o = new Inputs();
        var grid = new Option<string[]>("--grid") { Description = "Grid parameter key=v1,v2,... (repeatable); every combination is run and logged", Required = true };
        var top = new Option<int>("--top") { Description = "Rows to show", DefaultValueFactory = _ => 10 };
        var command = new Command("sweep", "Run every combination of a parameter grid, then PBO and the Deflated Sharpe Ratio of the best.");
        o.AddTo(command);
        command.Options.Add(grid);
        command.Options.Add(top);
        command.SetAction(parse => Execute(parse, w =>
        {
            string name = parse.GetValue(o.Strategy)!;
            IReadOnlyDictionary<string, string> fixedParams = ParseParams(parse.GetValue(o.Param));
            var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (string g in parse.GetValue(grid) ?? [])
            {
                (string key, string list) = SplitPair(g, "--grid");
                values[key] = QaCli.SplitList(list);
            }

            StrategyDefinition[] configurations = [.. BacktestSweep.ExpandGrid(values, fixedParams).Select(p => StrategyCatalog.Create(name, p))];
            Setup setup = Prepare(parse, o, configurations[0]);
            bool json = parse.GetValue(o.Json);
            int done = 0;
            SweepResult sweep = BacktestSweep.Run(setup.Template, configurations, _ =>
            {
                if (!json && ++done % 10 == 0)
                {
                    parse.InvocationConfiguration.Error.WriteLine($"  {done}/{configurations.Length} done");
                }
            });

            if (json)
            {
                w.WriteLine(JsonSerializer.Serialize(new
                {
                    trials = sweep.Trials.Select(t => t.Record),
                    pbo = sweep.Pbo is null ? null : new { sweep.Pbo.Probability, sweep.Pbo.ProbabilityOfOosLoss, sweep.Pbo.Blocks, sweep.Pbo.Combinations, sweep.Pbo.LogitMean },
                    best = sweep.Best?.Record.Id,
                    bestDsr = sweep.BestDsr,
                    studyTrials = sweep.StudyTrials,
                    notes = setup.Notes,
                }, QaCli.Json));
                return 0;
            }

            WriteSweep(w, setup, sweep, parse.GetValue(top));
            return 0;
        }));
        return command;
    }

    /// <summary>The text report of a sweep: the top rows by Sharpe, the best's Deflated Sharpe Ratio and the PBO.</summary>
    internal static void WriteSweep(TextWriter w, Setup setup, SweepResult sweep, int top)
    {
        WriteHeader(w, setup, sweep.Trials[0].Record);
        var table = new TextTable(("id", false), ("parameters", false), ("status", false), ("SR/yr", true), ("PSR(0)", true), ("return", true), ("max DD", true));
        foreach (BacktestResult r in sweep.Trials.OrderByDescending(t => t.Record.Metrics?.SharpePerPeriod ?? double.NegativeInfinity).Take(top))
        {
            TrialMetrics? m = r.Record.Metrics;
            table.Add(r.Record.Id, Describe(r.Record.Parameters), Status(r.Record.Status), F(m?.SharpeAnnualised, "0.00"), F(m?.Psr0, "0.000"),
                Pct(m?.TotalReturn), Pct(m?.MaxDrawdown));
        }

        table.Write(w);
        int ok = sweep.Trials.Count(t => t.Ok);
        w.WriteLine($"{sweep.Trials.Count} configurations run and logged ({ok} ok).");
        if (sweep.Best is { } best)
        {
            w.WriteLine($"Best: {best.Record.Id} {Describe(best.Record.Parameters)}, SR/yr {best.Record.Metrics!.SharpeAnnualised:0.00}.");
            w.WriteLine(sweep.BestDsr is { } dsr
                ? $"Deflated Sharpe Ratio of the best: {dsr:0.000} over {sweep.StudyTrials} trials in the study ({(dsr >= 0.95 ? "significant at 5 %" : "NOT significant at 5 %")})."
                : "Deflated Sharpe Ratio of the best: n/a (needs 2+ completed trials with different Sharpe ratios).");
        }

        w.WriteLine(sweep.Pbo is { } pbo
            ? $"PBO (CSCV, S = {pbo.Blocks}, {pbo.Combinations} splits): {pbo.Probability:0.000}; P(best in-sample loses out-of-sample): {pbo.ProbabilityOfOosLoss:0.000}."
            : $"PBO: n/a (needs 2+ completed configurations and at least {BacktestSweep.MinPeriodsForPbo} return periods).");
        w.WriteLine($"Ledger: {setup.Template.Ledger!.Path}. Model output; not financial advice.");
    }

    // ---- qa trials list / verify ------------------------------------------------------------------------

    private static Command List()
    {
        var ledger = LedgerOption();
        var study = new Option<string?>("--study") { Description = "Only trials of this study key" };
        var last = new Option<int>("--last") { Description = "Show the most recent N", DefaultValueFactory = _ => 20 };
        var json = new Option<bool>("--json") { Description = "JSON output" };
        var command = new Command("list", "List logged trials (newest last).");
        command.Options.Add(ledger);
        command.Options.Add(study);
        command.Options.Add(last);
        command.Options.Add(json);
        command.SetAction(parse => Execute(parse, w =>
        {
            var l = new TrialLedger(ResolveLedger(parse.GetValue(ledger)));
            string? studyKey = parse.GetValue(study);
            TrialRecord[] records = [.. l.ReadAll().Where(r => studyKey is null || r.Study == studyKey)];
            TrialRecord[] shown = [.. records.TakeLast(Math.Max(0, parse.GetValue(last)))];
            if (parse.GetValue(json))
            {
                w.WriteLine(JsonSerializer.Serialize(shown, QaCli.Json));
                return 0;
            }

            var table = new TextTable(("id", false), ("recorded (UTC)", false), ("runner", false), ("strategy", false), ("status", false),
                ("SR/yr", true), ("DSR", true), ("N", true), ("data", false));
            foreach (TrialRecord r in shown)
            {
                TrialMetrics? m = r.Metrics;
                table.Add(r.Id, r.RecordedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), r.Runner,
                    $"{r.Strategy}{(r.Parameters.Count == 0 ? string.Empty : " " + Describe(r.Parameters))}", Status(r.Status),
                    F(m?.SharpeAnnualised, "0.00"), F(m?.Dsr, "0.000"), m is null ? "-" : m.DsrTrials.ToString(CultureInfo.InvariantCulture),
                    $"{r.DataSource} {r.From:yyyy-MM-dd}..{r.To:yyyy-MM-dd}");
            }

            table.Write(w);
            w.WriteLine($"{shown.Length} of {records.Length} trial(s){(studyKey is null ? string.Empty : $" in study {studyKey}")}; ledger {l.Path}.");
            return 0;
        }));
        return command;
    }

    private static Command Verify()
    {
        var ledger = LedgerOption();
        var command = new Command("verify", "Check the ledger's hash chain: any edited, deleted or reordered line is reported.");
        command.Options.Add(ledger);
        command.SetAction(parse => Execute(parse, w =>
        {
            var l = new TrialLedger(ResolveLedger(parse.GetValue(ledger)));
            LedgerVerification v = l.Verify();
            if (v.Valid)
            {
                w.WriteLine($"OK: {v.Records} trial(s), hash chain intact ({l.Path}).");
                return 0;
            }

            parse.InvocationConfiguration.Error.WriteLine($"error: ledger {l.Path} is NOT intact after {v.Records} valid line(s): {v.Problem}");
            return 1;
        }));
        return command;
    }

    // ---- qa costs ---------------------------------------------------------------------------------------

    private static Command CostsCommand()
    {
        var amount = new Option<string?>("--amount") { Description = "Trade size(s) in SEK to price, comma-separated, e.g. 5000,20000,100000" };
        var capital = new Option<decimal?>("--capital") { Description = "Your capital at Avanza (SEK); classes you cannot choose with it are left out of 'cheapest'" };
        var configDir = new Option<string?>("--config-dir") { Description = "Folder with costs.*.json (default: ./config, then next to qa)" };
        var command = new Command("costs", "Avanza courtage classes (config/costs.*.json) and what one order costs in each, to choose a class for your trade size. Offline.");
        command.Options.Add(amount);
        command.Options.Add(capital);
        command.Options.Add(configDir);
        command.SetAction(parse => Execute(parse, w =>
        {
            string dir = ResolveConfigDir(parse.GetValue(configDir));
            IReadOnlyList<CostModel> classes = CostModel.LoadAll(dir);
            BacktestDefaults defaults = BacktestDefaults.Load(dir);
            decimal? cap = parse.GetValue(capital);
            decimal[] amounts = [.. QaCli.SplitList(parse.GetValue(amount)).Select(s =>
                decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v) && v > 0
                    ? v
                    : throw new ArgumentException($"--amount: '{s}' is not a positive number of SEK (use '.' as decimal separator)."))];

            bool Eligible(CostModel c) => c.EligibleBelowCapital is not { } limit || cap is null || cap < limit;

            // An order cannot be larger than the capital (ISK, no credit), so a class limited to capital under X
            // cannot carry an order of X or more.
            bool Usable(CostModel c, decimal a) => Eligible(c) && (c.EligibleBelowCapital is not { } limit || a < limit);
            var columns = new List<(string, bool)> { ("class", false), ("file (--costs)", false), ("courtage per order", false), ("Avanza: suits trades", false), ("who can choose it", false) };
            columns.AddRange(amounts.Select(a => ($"fee at {Sek(a)}", true)));
            columns.Add(("verified", false));
            var table = new TextTable([.. columns]);
            foreach (CostModel c in classes)
            {
                var row = new List<string>
                {
                    (c.DisplayName ?? c.Name) + (c.Name == defaults.Costs ? " (default)" : string.Empty),
                    c.Name,
                    Rule(c),
                    Suits(c.SuitsTrades),
                    c.EligibleBelowCapital is { } limit ? $"capital under {Sek(limit)}{(Eligible(c) ? string.Empty : " - not you")}" : "anyone",
                };
                foreach (decimal a in amounts)
                {
                    decimal fee = c.Courtage(a);
                    row.Add(!Usable(c, a)
                        ? "not available"
                        : $"{fee.ToString("0.##", CultureInfo.InvariantCulture)} ({(fee / a).ToString("0.###%", CultureInfo.InvariantCulture)})");
                }

                row.Add(c.VerifiedOn is { } v ? v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "NOT VERIFIED");
                table.Add([.. row]);
            }

            table.Write(w);
            foreach (decimal a in amounts)
            {
                CostModel? best = classes.Where(c => Usable(c, a)).MinBy(c => c.Courtage(a));
                if (best is not null)
                {
                    string ties = string.Join(", ", classes.Where(c => Usable(c, a) && c != best && c.Courtage(a) == best.Courtage(a)).Select(c => c.DisplayName ?? c.Name));
                    w.WriteLine($"Cheapest for one {Sek(a)} order: {best.DisplayName ?? best.Name} ({best.Courtage(a).ToString("0.##", CultureInfo.InvariantCulture)} SEK){(ties.Length == 0 ? string.Empty : $", tied with {ties}")}.");
                }
            }

            if (cap is null && classes.Any(c => c.EligibleBelowCapital is not null))
            {
                w.WriteLine("Some classes depend on your capital at Avanza; pass --capital to leave out the ones you cannot choose.");
            }

            w.WriteLine($"Fees are per order (a buy and a later sell pay twice), Nasdaq Stockholm main market; other marketplaces cost more. Backtests use {defaults.Costs} and {Sek(defaults.Cash)} unless you pass --costs / --cash (config/{BacktestDefaults.FileName}).");
            return 0;
        }));
        return command;

        static string Rule(CostModel c) =>
            c.CourtageRate == 0
                ? c.CourtageMin == 0 ? "free (0 %, no minimum)" : $"{c.CourtageMin.ToString("0.##", CultureInfo.InvariantCulture)} SEK flat"
                : $"{(c.CourtageRate * 100).ToString("0.###", CultureInfo.InvariantCulture)} %, min {c.CourtageMin.ToString("0.##", CultureInfo.InvariantCulture)} SEK";

        static string Suits((decimal? From, decimal? To) s) => s switch
        {
            (null, null) => "-",
            (null, { } to) => $"under {Sek(to)}",
            ({ } from, null) => $"over {Sek(from)}",
            ({ } from, { } to) => $"{Sek(from)} - {Sek(to)}",
        };
    }

    private static string Sek(decimal amount) => amount.ToString("N0", CultureInfo.InvariantCulture) + " SEK";

    /// <summary>A cost model by class name (config/costs.&lt;name&gt;.json) or by path.</summary>
    internal static CostModel LoadCosts(string configDir, string nameOrPath) =>
        CostModel.Load(nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || nameOrPath.Contains(Path.DirectorySeparatorChar) || nameOrPath.Contains('/')
            ? nameOrPath
            : Path.Combine(configDir, $"costs.{nameOrPath}.json"));

    // ---- data from the history store --------------------------------------------------------------------

    /// <summary>
    /// Builds a panel from imported Avanza history. Only continuously traded instruments are accepted (the fill model
    /// assumes continuous trading; market-rules.md). Lot size 1; non-SEK instruments pay the FX fee.
    /// </summary>
    internal static MarketPanel LoadStorePanel(HistoryStore store, IReadOnlyList<string> tickers, DateOnly? from, DateOnly? to)
    {
        if (tickers.Count == 0)
        {
            throw new ArgumentException("--tickers: give at least one ticker.");
        }

        string sourceName = AvanzaChartImporter.AvanzaPriceChart.Name;
        DataSourceInfo source = store.GetSource(sourceName) ?? throw new ArgumentException($"No data from source '{sourceName}' in {store.Path}. Run 'qa history import' first.");
        var series = new List<(PanelInstrument, IReadOnlyList<DailyBar>)>();
        foreach (string ticker in tickers)
        {
            StoredInstrument stored = DataCommands.FindInstrument(store, ticker, null, null);
            InstrumentRecord r = stored.Instrument;
            if (r.TradingModel != TradingModel.Continuous)
            {
                throw new ArgumentException($"{r.Ticker} trades as '{r.TradingModel}' on {r.MarketPlace}: the backtest fill model assumes continuous trading, so it is refused (docs/research/market-rules.md).");
            }

            IReadOnlyList<StoredBar> bars = store.GetDailyBars(r.OrderbookId, sourceName, from, to);
            if (bars.Count == 0)
            {
                throw new ArgumentException($"{r.Ticker}: no bars from {sourceName} in the requested range.");
            }

            IReadOnlyList<DailyBar> daily = [.. bars.Select(b => b.Bar)];
            PanelInstrument instrument;
            if (Markets.IsForeign(r.Currency))
            {
                if (Markets.ForCurrency(r.Currency) is null)
                {
                    throw new ArgumentException($"{r.Ticker} trades in {r.Currency}; the program trades shares in {Markets.CurrencyList} (ADR 0005).");
                }

                (daily, decimal last) = InSek(store, r, daily);
                instrument = new PanelInstrument(r.Ticker, 1, true, Scaled(ParseTickTable(r.TickTableJson), last)) { Currency = r.Currency, LastSekPerUnit = last };
            }
            else
            {
                instrument = new PanelInstrument(r.Ticker, 1, false, ParseTickTable(r.TickTableJson));
            }

            series.Add((instrument, daily));
        }

        return MarketPanel.FromDailyBars(series, source);
    }

    /// <summary>A fixing older than this for a bar means the FX history has a hole: refused rather than bridged.</summary>
    internal const int MaxFixingAgeDays = 7;

    /// <summary>
    /// A foreign share's bars in SEK (ADR 0005): each bar at the latest Riksbank fixing on or before its date (published
    /// about 16:15 Stockholm, before the US and Canadian close, so known when the bar closes). Returns the last fixing used.
    /// </summary>
    internal static (IReadOnlyList<DailyBar> Bars, decimal LastSekPerUnit) InSek(HistoryStore store, InstrumentRecord r, IReadOnlyList<DailyBar> bars)
    {
        string ccy = r.Currency;
        DateOnly first = bars[0].Date, last = bars[^1].Date;
        StoredFxRate[] rates = [.. store.GetFxRates(ccy, Data.Fx.RiksbankFxSource.Riksbank.Name, first.AddDays(-MaxFixingAgeDays), last)];
        string fix = $"run 'qa fx import {ccy} --from {first.AddDays(-14):yyyy-MM-dd}' (or import {r.Ticker} again, which brings its fixings)";
        if (rates.Length == 0 || rates[0].Rate.Date > first)
        {
            throw new ArgumentException($"{r.Ticker} trades in {ccy}, but no {ccy}/SEK fixing is stored for its first bar {first:yyyy-MM-dd}: {fix}.");
        }

        var converted = new List<DailyBar>(bars.Count);
        int k = 0;
        foreach (DailyBar b in bars)
        {
            while (k + 1 < rates.Length && rates[k + 1].Rate.Date <= b.Date)
            {
                k++;
            }

            FxRate fx = rates[k].Rate;
            if (b.Date.DayNumber - fx.Date.DayNumber > MaxFixingAgeDays)
            {
                throw new ArgumentException($"{r.Ticker}: the {ccy}/SEK fixing for {b.Date:yyyy-MM-dd} is from {fx.Date:yyyy-MM-dd}, more than {MaxFixingAgeDays} days old: {fix}.");
            }

            decimal m = fx.SekPerUnit;
            converted.Add(new DailyBar(b.Date, Sek(b.Open * m), Sek(b.High * m), Sek(b.Low * m), Sek(b.Close * m), b.Volume));
        }

        return (converted, rates[k].Rate.SekPerUnit);

        static decimal Sek(decimal v) => decimal.Round(v, 6, MidpointRounding.AwayFromZero);
    }

    /// <summary>A foreign tick table in SEK at one rate: an approximation, stated in the report (ADR 0005).</summary>
    internal static TickSizeTable Scaled(TickSizeTable table, decimal sekPerUnit) =>
        new([.. table.Bands.Select(b => new TickSizeBand(b.Min * sekPerUnit, b.Max * sekPerUnit, b.Tick * sekPerUnit))]);

    internal static TickSizeTable ParseTickTable(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return new TickSizeTable([.. doc.RootElement.EnumerateArray().Select(b =>
            new TickSizeBand(b.GetProperty("min").GetDecimal(), b.GetProperty("max").GetDecimal(), b.GetProperty("tick").GetDecimal()))]);
    }

    // ---- output -----------------------------------------------------------------------------------------

    internal static void WriteHeader(TextWriter w, Setup setup, TrialRecord record)
    {
        BacktestRequest t = setup.Template;
        MarketPanel d = t.Data;
        w.WriteLine($"Data: {d.Source.Label}; {d.InstrumentCount} instrument(s), {d.Periods} bars {d.Dates[0]:yyyy-MM-dd}..{d.Dates[^1]:yyyy-MM-dd}{(t.Seed == 0 ? string.Empty : $", seed {t.Seed}")}.");
        foreach (string note in setup.Notes)
        {
            w.WriteLine(note);
        }

        w.WriteLine($"Costs: {t.Costs.Label}. Orders: {Order(t.Execution)}; cash {t.InitialCash.ToString("N0", CultureInfo.InvariantCulture)} {t.Costs.Currency}; no-trade band {t.Execution.RebalanceBand:P0}.");
        w.WriteLine($"Study: {record.Study}");
    }

    internal static void WriteResult(TextWriter w, BacktestResult r, BacktestRequest t)
    {
        TrialRecord rec = r.Record;
        w.WriteLine($"Trial {rec.Id}: {rec.Strategy}{(rec.Parameters.Count == 0 ? string.Empty : " " + Describe(rec.Parameters))} → {Status(rec.Status)}{(rec.Note is null ? string.Empty : $" ({rec.Note})")}");
        if (rec.Metrics is { } m)
        {
            BacktestState s = r.FinalState;
            string ccy = t.Costs.Currency;
            var table = new TextTable(("metric", false), ("value", true));
            table.Add("total return", Pct(m.TotalReturn));
            table.Add("CAGR", Pct(m.Cagr));
            table.Add("volatility (annualised)", Pct(m.VolatilityAnnualised));
            table.Add("Sharpe (annualised)", F(m.SharpeAnnualised, "0.00"));
            table.Add("PSR(0)", F(m.Psr0, "0.000"));
            table.Add("Deflated Sharpe", m.Dsr is { } dsr ? $"{dsr:0.000} (N = {m.DsrTrials})" : $"n/a (N = {m.DsrTrials})");
            table.Add("max drawdown", Pct(m.MaxDrawdown));
            table.Add("turnover (one-way, per year)", F(m.Turnover, "0.00"));
            table.Add($"costs ({ccy})", Money(m.TotalCosts));
            table.Add("  courtage", Money(s.Courtage));
            table.Add("  FX fees", Money(s.FxFees));
            table.Add("  spread + slippage", Money(s.SpreadSlippage));
            table.Add("fills / orders", $"{s.Fills} / {s.Orders}");
            table.Add("observations", m.Observations.ToString(CultureInfo.InvariantCulture));
            table.Write(w);
            if (m.Dsr is { } d && d < 0.95)
            {
                w.WriteLine("Not significant at 5 % once all trials in the study are counted (DSR < 0.95).");
            }
        }

        w.WriteLine($"Logged to {t.Ledger!.Path} (runner {rec.Runner}). Model output; not financial advice.");
    }

    private static string Order(ExecutionOptions e) => e.OrderType switch
    {
        BacktestOrderType.Limit => $"day limits {e.LimitOffsetBps:0.#} bps from the decision close, rounded passively to the tick",
        BacktestOrderType.MarketOnOpen => "market on open",
        _ => "market on close",
    };

    private static string Describe(IReadOnlyDictionary<string, string> p) => string.Join(" ", p.Select(kv => $"{kv.Key}={kv.Value}"));

    private static string Status(TrialStatus s) => s switch
    {
        TrialStatus.Ok => "ok",
        TrialStatus.RejectedLeakage => "REJECTED (look-ahead)",
        TrialStatus.RejectedHoldout => "REJECTED (holdout)",
        _ => "FAILED",
    };

    private static string F(double? v, string format) => v is { } x && double.IsFinite(x) ? x.ToString(format, CultureInfo.InvariantCulture) : "-";

    private static string Pct(double? v) => v is { } x && double.IsFinite(x) ? (x * 100).ToString("0.00", CultureInfo.InvariantCulture) + " %" : "-";

    private static string Money(double v) => v.ToString("N0", CultureInfo.InvariantCulture);

    // ---- parsing and paths ------------------------------------------------------------------------------

    internal static IReadOnlyDictionary<string, string> ParseParams(string[]? items)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string item in items ?? [])
        {
            (string key, string value) = SplitPair(item, "--param");
            if (!result.TryAdd(key, value))
            {
                throw new ArgumentException($"--param: '{key}' given twice.");
            }
        }

        return result;
    }

    internal static (string Key, string Value) SplitPair(string item, string option)
    {
        int eq = item.IndexOf('=', StringComparison.Ordinal);
        return eq <= 0 || eq == item.Length - 1
            ? throw new ArgumentException($"{option}: '{item}' is not key=value.")
            : (item[..eq].Trim(), item[(eq + 1)..].Trim());
    }

    private static (int Instruments, int Periods) ParseShape(string text)
    {
        string[] parts = text.Split('x', 'X');
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 5000
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int t) && t is >= 2 and <= 20_000
            ? (n, t)
            : throw new ArgumentException($"--synthetic: '{text}' must be <instruments>x<bars> with 1-5000 instruments and 2-20000 bars, e.g. 300x2520.");
    }

    /// <summary>The folder with holdout.json and the cost models: --config-dir, ./config, then next to qa.</summary>
    internal static string ResolveConfigDir(string? explicitDir)
    {
        if (explicitDir is not null)
        {
            return explicitDir;
        }

        foreach (string candidate in new[] { "config", Path.Combine(AppContext.BaseDirectory, "config") })
        {
            if (File.Exists(Path.Combine(candidate, HoldoutPolicy.FileName)))
            {
                return candidate;
            }
        }

        throw new ArgumentException($"No {HoldoutPolicy.FileName} found (./config or next to qa); pass --config-dir. Backtests do not run without the holdout policy.");
    }

    /// <summary>The allowlist's tickers as a comma list, or null when there is none (no file or no entries).</summary>
    private static string? AllowlistTickers(string configDir)
    {
        string path = Path.Combine(configDir, Trading.Risk.Universe.FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            Trading.Risk.Universe universe = Trading.Risk.Universe.Load(path);
            return universe.Entries.Count == 0 ? null : string.Join(",", universe.Entries.Select(e => e.Ticker));
        }
        catch (Trading.Risk.TradingConfigException ex)
        {
            throw new ArgumentException(ex.Message, ex);
        }
    }

    /// <summary>--ledger, else research/trial-ledger.jsonl at the repository root above the current directory.</summary>
    internal static string ResolveLedger(string? explicitPath)
    {
        if (explicitPath is not null)
        {
            return explicitPath;
        }

        for (DirectoryInfo? d = new(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "QuantAnalyst.sln")))
            {
                return Path.Combine(d.FullName, TrialLedger.DefaultPath);
            }
        }

        throw new ArgumentException($"Not inside the repository, so the default ledger ({TrialLedger.DefaultPath}) is unknown; pass --ledger.");
    }

    internal static int Execute(ParseResult parse, Func<TextWriter, int> body) => DataCommands.Execute(parse, w =>
    {
        try
        {
            return body(w);
        }
        catch (Exception ex) when (ex is BacktestConfigException or StrategyException or JsonException)
        {
            throw new ArgumentException(ex.Message, ex);
        }
    });
}
