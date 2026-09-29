using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Calendar;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Live;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Native;

namespace QuantAnalyst.Cli.Commands;

/// <summary>
/// <c>qa intraday backtest</c> (plan 17 step A5, ADR 0006): the intraday strategies on the collected 1- or 5-minute
/// bars. Offline; every run is logged to the TrialLedger, whatever its outcome. The intraday holdout
/// (config/holdout.intraday.json, the last collected days) is never read while locked.
/// </summary>
internal static class IntradayBacktestCommands
{
    /// <summary>Spread samples a share needs before its own measured spread replaces the cost model's.</summary>
    internal const int MinSpreadSamples = 30;

    /// <summary>An instrument's day ends this long before the close or later, else it is incomplete (a mid-day import, a halt).</summary>
    internal static readonly TimeSpan CompleteBy = TimeSpan.FromMinutes(30);

    /// <summary>Placeholder ticks for a research-list share not in the instrument master: intraday runs send market orders only, which never read it.</summary>
    private static readonly TickSizeTable MarketOrdersOnly = new([new TickSizeBand(0m, 1_000_000_000m, 0.0001m)]);

    private sealed class Inputs
    {
        public Option<string> Strategy { get; } = new("--strategy") { Description = $"Intraday strategy: {string.Join(", ", IntradayStrategyCatalog.Names)}", Required = true };

        public Option<string[]> Param { get; } = new("--param") { Description = "Strategy parameter key=value (repeatable)", AllowMultipleArgumentsPerToken = false };

        public Option<string[]> Grid { get; } = new("--grid") { Description = "Sweep: key=v1,v2,... (repeatable); every combination is run and logged, then PBO and the Deflated Sharpe Ratio of the best" };

        public Option<int> Top { get; } = new("--top") { Description = "Sweep rows to show", DefaultValueFactory = _ => 10 };

        public Option<string?> Tickers { get; } = new("--tickers") { Description = "Comma-separated tickers (default: the allowlist's Stockholm shares and the research list)" };

        public Option<string> Resolution { get; } = new("--resolution") { Description = "five_minutes (default, ADR 0006 D3) or minute", DefaultValueFactory = _ => "five_minutes" };

        public Option<string?> From { get; } = new("--from") { Description = "First trading day (yyyy-MM-dd)" };

        public Option<string?> To { get; } = new("--to") { Description = "Last trading day (yyyy-MM-dd); defaults to the day before the locked intraday holdout" };

        public Option<string?> Costs { get; } = new("--costs") { Description = "Courtage class (config/costs.<name>.json) or a path. Default: config/backtest-defaults.json" };

        public Option<decimal?> Cash { get; } = new("--cash") { Description = "Initial cash (SEK). Default: config/backtest-defaults.json" };

        public Option<string?> ConfigDir { get; } = new("--config-dir") { Description = "Folder with holdout.intraday.json, costs.*.json and the XSTO calendar (default: ./config, then next to qa)" };

        public Option<string> Store { get; } = DataCommands.StoreOption();

        public Option<string?> Ledger { get; } = new("--ledger") { Description = $"Ledger file (default: <repository>/{TrialLedger.DefaultPath})" };

        public Option<bool> Json { get; } = new("--json") { Description = "JSON output" };

        public void AddTo(Command command)
        {
            foreach (Option o in new Option[] { Strategy, Param, Grid, Top, Tickers, Resolution, From, To, Costs, Cash, ConfigDir, Store, Ledger, Json })
            {
                command.Options.Add(o);
            }
        }
    }

    public static Command Backtest()
    {
        var o = new Inputs();
        var command = new Command(
            "backtest",
            "Backtest an intraday strategy (orb-long, late-momentum, open-close) on the collected bars, and log it to the TrialLedger. Market orders at the next bar's open, flat before the close. Offline; model output, not advice.");
        o.AddTo(command);
        command.SetAction(parse => BacktestCommands.Execute(parse, w =>
        {
            string configDir = BacktestCommands.ResolveConfigDir(parse.GetValue(o.ConfigDir));
            ChartResolution resolution = parse.GetValue(o.Resolution) switch
            {
                "five_minutes" => ChartResolution.FiveMinutes,
                "minute" => ChartResolution.Minute,
                string other => throw new ArgumentException($"--resolution: '{other}' is not five_minutes or minute."),
                null => ChartResolution.FiveMinutes,
            };

            MarketCalendar calendar = LoadCalendar(configDir, out string? calendarNote);
            var clock = new IntradayClock(calendar, IntradayImporter.Length(resolution));
            string name = parse.GetValue(o.Strategy)!;
            IReadOnlyDictionary<string, string> fixedParams = BacktestCommands.ParseParams(parse.GetValue(o.Param));
            var grid = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (string g in parse.GetValue(o.Grid) ?? [])
            {
                (string key, string list) = BacktestCommands.SplitPair(g, "--grid");
                grid[key] = QaCli.SplitList(list);
            }

            StrategyDefinition[] configurations = [.. BacktestSweep.ExpandGrid(grid, fixedParams).Select(p => IntradayStrategyCatalog.Create(name, p, clock))];
            BacktestCommands.Setup setup = Prepare(parse, o, configDir, resolution, configurations[0], calendar, calendarNote);
            bool json = parse.GetValue(o.Json);
            if (grid.Count == 0)
            {
                BacktestResult result = BacktestRunner.Run(setup.Template);
                if (json)
                {
                    w.WriteLine(JsonSerializer.Serialize(new { record = result.Record, notes = setup.Notes, ledger = setup.Template.Ledger!.Path }, QaCli.Json));
                    return result.Ok ? 0 : 2;
                }

                BacktestCommands.WriteHeader(w, setup, result.Record);
                if (result.Ok)
                {
                    w.WriteLine(Trades(setup.Template.Data, result));
                }

                BacktestCommands.WriteResult(w, result, setup.Template);
                return result.Ok ? 0 : 2;
            }

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

            BacktestCommands.WriteSweep(w, setup, sweep, parse.GetValue(o.Top));
            return 0;
        }));
        return command;
    }

    /// <summary>"Trades: 84 fills on 21 trading days (4.0 a day)."</summary>
    private static string Trades(MarketPanel data, BacktestResult result)
    {
        int days = data.Dates.Distinct().Count();
        return string.Create(CultureInfo.InvariantCulture, $"Trades: {result.Fills.Count} fills on {days} trading day(s) ({(double)result.Fills.Count / days:0.0} a day).");
    }

    private static MarketCalendar LoadCalendar(string configDir, out string? note)
    {
        try
        {
            MarketCalendar calendar = MarketCalendarLoader.LoadDirectory(configDir, Markets.Stockholm.Mic);
            note = calendar.IsVerified ? null : $"Session times: the UNVERIFIED {Markets.Stockholm.Mic} calendar draft for {string.Join(", ", calendar.UnverifiedYears)} (half days close early).";
            return calendar;
        }
        catch (CalendarConfigException ex)
        {
            throw new ArgumentException($"Intraday backtests need the {Markets.Stockholm.Mic} calendar for each day's open and close: {ex.Message}", ex);
        }
    }

    private static BacktestCommands.Setup Prepare(
        ParseResult parse, Inputs o, string configDir, ChartResolution resolution, StrategyDefinition strategy, MarketCalendar calendar, string? calendarNote)
    {
        BacktestDefaults defaults = BacktestDefaults.Load(configDir);
        CostModel costs = BacktestCommands.LoadCosts(configDir, parse.GetValue(o.Costs) ?? defaults.Costs);
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
        if (calendarNote is not null)
        {
            notes.Add(calendarNote);
        }

        string storePath = parse.GetValue(o.Store)!;
        IReadOnlyList<IntradayName> names = parse.GetValue(o.Tickers) is { } tickers
            ? AvanzaCommands.NamedShares(storePath, configDir, QaCli.SplitList(tickers))
            : AvanzaCommands.CollectedShares(storePath, configDir, TextWriter.Null);
        if (names.Count == 0)
        {
            throw new ArgumentException("No shares: give --tickers, or put Stockholm shares on the allowlist or the research list ('qa intraday research add VOLV-B').");
        }

        DateOnly? from = DataCommands.ParseDate(parse.GetValue(o.From), "--from");
        DateOnly? to = DataCommands.ParseDate(parse.GetValue(o.To), "--to");
        using HistoryStore store = DataCommands.OpenExisting(storePath);
        string sourceName = AvanzaChartImporter.AvanzaPriceChart.Name;
        IntradayHoldout rule = IntradayHoldout.Load(Path.Combine(configDir, IntradayHoldout.FileName));
        IReadOnlyList<DateOnly> collected = store.GetIntradayCollectedDays(resolution, sourceName);
        string bars = Bars(resolution);
        if (collected.Count == 0)
        {
            throw new ArgumentException($"No {bars} bars are collected yet. Run 'qa intraday import' after each trading day (or let 'qa paper run' do it).");
        }

        // The holdout rolls with the collection: the last `days` collected days. Too few days, and all of them are in it.
        HoldoutPolicy holdout = rule.For(collected) ?? (rule.Locked
            ? throw new ArgumentException($"{collected.Count} trading day(s) of {bars} bars are collected; the last {rule.Days} are the locked intraday holdout ({rule.Path}), so nothing can be backtested yet. Keep collecting.")
            : new HoldoutPolicy(false, collected[0], rule.Path));

        DateOnly? readTo = to is null && holdout.Locked ? holdout.Start.AddDays(-1) : to;
        if (readTo != to)
        {
            notes.Add($"Data read up to {readTo:yyyy-MM-dd}: the intraday holdout, the last {rule.Days} collected days from {holdout.Start:yyyy-MM-dd}, is locked ({rule.Path}).");
        }

        MarketPanel data = LoadPanel(store, names, resolution, calendar, from, readTo, costs, notes);
        var template = new BacktestRequest
        {
            Data = data,
            Strategy = strategy,
            Costs = costs,
            Holdout = holdout,
            InitialCash = cash,
            Execution = new ExecutionOptions { OrderType = BacktestOrderType.MarketOnOpen },
            Ledger = new TrialLedger(BacktestCommands.ResolveLedger(parse.GetValue(o.Ledger))),
            GitCommit = Analytics.Backtesting.GitInfo.TryGetCommit(Directory.GetCurrentDirectory()),
        };
        notes.Add("Orders: market orders at the next bar's open, paying half the spread plus slippage; flat before each close (ADR 0006).");
        return new BacktestCommands.Setup(template, notes);
    }

    /// <summary>
    /// An intraday panel from the store (plan 17): each share's bars of one resolution inside each day's session (from the
    /// calendar), a share's day left out when its bars stop more than <see cref="CompleteBy"/> before the close, and its own
    /// half-spread when a Paper session measured it often enough. The source is the daily bars' own (NOT point-in-time,
    /// NOT survivorship-free), named with the resolution so 1- and 5-minute runs are separate studies.
    /// </summary>
    internal static MarketPanel LoadPanel(
        HistoryStore store, IReadOnlyList<IntradayName> names, ChartResolution resolution, MarketCalendar calendar, DateOnly? from, DateOnly? to,
        CostModel costs, List<string> notes)
    {
        string sourceName = AvanzaChartImporter.AvanzaPriceChart.Name;
        DataSourceInfo source = store.GetSource(sourceName) ?? throw new ArgumentException($"No data from source '{sourceName}' in {store.Path}. Run 'qa intraday import' first.");
        DateTimeOffset? fromUtc = from is { } f ? calendar.ToUtc(f, TimeOnly.MinValue) : null;
        DateTimeOffset? toUtc = to is { } t ? calendar.ToUtc(t.AddDays(1), TimeOnly.MinValue).AddTicks(-1) : null;
        var series = new List<(PanelInstrument, IReadOnlyList<Bar>)>();
        var spreads = new List<string>();
        int outside = 0, incomplete = 0;
        var empty = new List<string>();
        foreach (IntradayName name in names)
        {
            InstrumentRecord? record = store.GetInstrument(name.Id)?.Instrument;
            if (record is not null && record.TradingModel != TradingModel.Continuous)
            {
                throw new ArgumentException($"{record.Ticker} trades as '{record.TradingModel}' on {record.MarketPlace}: the fill model assumes continuous trading (docs/research/market-rules.md).");
            }

            var kept = new List<Bar>();
            foreach (IGrouping<DateOnly, Bar> day in store.GetIntradayBars(name.Id, resolution, sourceName, fromUtc, toUtc)
                         .Select(s => s.Bar)
                         .GroupBy(b => DateOnly.FromDateTime(MarketTime.ToStockholm(b.TimestampUtc).DateTime)))
            {
                TradingDay session = calendar.Classify(day.Key);
                if (!session.IsTradingDay)
                {
                    outside += day.Count(); // bars on a day the calendar has closed: the calendar or the data is wrong; neither is guessed
                    continue;
                }

                DateTimeOffset open = calendar.ToUtc(day.Key, session.Open!.Value), close = calendar.ToUtc(day.Key, session.Close!.Value);
                Bar[] inSession = [.. day.Where(b => b.TimestampUtc >= open && b.TimestampUtc < close)];
                outside += day.Count() - inSession.Length;
                if (inSession.Length == 0 || inSession[^1].TimestampUtc < close - CompleteBy)
                {
                    incomplete++;
                    continue;
                }

                kept.AddRange(inSession);
            }

            if (kept.Count == 0)
            {
                empty.Add(name.Ticker);
                continue;
            }

            double? halfSpread = null;
            IReadOnlyList<SpreadSample> samples = store.GetSpreadSamples(name.Id, SpreadSampler.Source.Name, kept[0].TimestampUtc, kept[^1].TimestampUtc);
            if (samples.Count >= MinSpreadSamples)
            {
                double[] relative = [.. samples.Select(s => (double)s.RelativeSpread).Order()];
                double median = relative.Length % 2 == 1 ? relative[relative.Length / 2] : (relative[(relative.Length / 2) - 1] + relative[relative.Length / 2]) / 2;
                halfSpread = median / 2 * 10_000;
                spreads.Add(string.Create(CultureInfo.InvariantCulture, $"{name.Ticker} {halfSpread:0.0} bps ({samples.Count} samples)"));
            }

            TickSizeTable ticks = record is null ? MarketOrdersOnly : BacktestCommands.ParseTickTable(record.TickTableJson);
            series.Add((new PanelInstrument(name.Ticker, 1, false, ticks) { HalfSpreadBps = halfSpread }, kept));
        }

        if (series.Count == 0)
        {
            throw new ArgumentException($"No {Bars(resolution)} bars for {string.Join(", ", names.Select(n => n.Ticker))} in the requested range. 'qa intraday import' collects them.");
        }

        if (empty.Count > 0)
        {
            notes.Add($"No bars in range, left out: {string.Join(", ", empty)}.");
        }

        if (incomplete > 0 || outside > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Data checks: {incomplete} share-day(s) left out whose bars stop more than {CompleteBy.TotalMinutes:0} minutes before the close (a mid-day import or a halt); {outside} bar(s) outside the session dropped."));
        }

        notes.Add(spreads.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Spreads: none measured yet (a Paper session samples its shares); every share pays the cost model's {costs.HalfSpreadBps:0.#} bps half-spread.")
            : string.Create(CultureInfo.InvariantCulture, $"Spreads measured by Paper sessions (median half-spread): {string.Join(", ", spreads)}; the others pay the cost model's {costs.HalfSpreadBps:0.#} bps."));
        notes.Add("Universe: current names only (survivorship-biased), chosen now (look-ahead in the choice of names).");
        string tag = resolution == ChartResolution.Minute ? "1m" : "5m";
        return MarketPanel.FromIntradayBars(series, source with { Name = $"{source.Name}:{tag}" });
    }

    private static string Bars(ChartResolution resolution) => resolution == ChartResolution.Minute ? "1-minute" : "5-minute";
}
