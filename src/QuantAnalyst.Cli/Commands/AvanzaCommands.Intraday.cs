using System.CommandLine;
using System.Globalization;
using QuantAnalyst.Avanza;
using QuantAnalyst.Cli.Output;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Intraday;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Cli.Commands;

internal static partial class AvanzaCommands
{
    // ---- qa intraday probe (plan 17, ADR 0006) -----------------------------------------------------------

    private static Command Intraday(AvanzaCliServices services)
    {
        var command = new Command("intraday", "Intraday research (plan 17, ADR 0006): collect intraday bars and backtest intraday strategies on them. Read-only; no orders.");
        command.Subcommands.Add(IntradayProbe(services));
        command.Subcommands.Add(IntradayImport(services));
        command.Subcommands.Add(IntradayResearch(services));
        command.Subcommands.Add(IntradayBacktestCommands.Backtest());
        return command;
    }

    private static Command IntradayImport(AvanzaCliServices services)
    {
        var common = new Common();
        var tickers = new Argument<string[]>("tickers") { Description = "Shares to collect (default: the allowlist's Stockholm shares and the research list)", Arity = ArgumentArity.ZeroOrMore };
        var period = new Option<string>("--period") { Description = "today (default) or one_week, one_month, three_months where 'qa intraday probe' shows 1- or 5-minute bars", DefaultValueFactory = _ => "today" };
        var resolution = new Option<string>("--resolution") { Description = "minute, five_minutes or both (default)", DefaultValueFactory = _ => "both" };
        var store = DataCommands.StoreOption();
        var configDir = TradingCommands.ConfigDirOption();
        var command = new Command(
            "import",
            "Store 1- and 5-minute bars from Avanza's public price chart, without a login (plan 17 step A2). Bars still open are left out; run it after 17:30 for the whole day. Read-only; nothing is traded.");
        common.AddTo(command, json: false);
        command.Arguments.Add(tickers);
        command.Options.Add(period);
        command.Options.Add(resolution);
        command.Options.Add(store);
        command.Options.Add(configDir);
        command.SetAction(parse => Run(parse, services, common, record: null, async (ctx, output) =>
        {
            ChartPeriod chartPeriod = ParsePeriod(parse.GetValue(period)!);
            IReadOnlyList<ChartResolution> resolutions = ParseResolutions(parse.GetValue(resolution)!);
            string storePath = parse.GetValue(store)!;
            string config = TradingCommands.ResolveConfigDir(parse.GetValue(configDir));
            IReadOnlyList<IntradayName> names = parse.GetValue(tickers) is { Length: > 0 } asked
                ? NamedShares(storePath, config, asked)
                : CollectedShares(storePath, config, output);
            if (names.Count == 0)
            {
                throw new ArgumentException("Nothing to collect: the allowlist has no Stockholm share and the research list is empty. Add names with 'qa intraday research add VOLV-B'.");
            }

            int failed = await CollectIntradayAsync(ctx.Connection.Gateway, storePath, names, chartPeriod, resolutions, services.Time, output, ctx.Ct).ConfigureAwait(false);
            return failed == 0 ? 0 : 1;
        }));
        return command;
    }

    private static Command IntradayResearch(AvanzaCliServices services)
    {
        var command = new Command("research", "The research list (config/research-universe.json): Stockholm shares whose intraday bars are collected besides the allowlist's. Never traded because they are on it.");
        var configDir = TradingCommands.ConfigDirOption();

        var common = new Common();
        var addTickers = new Argument<string[]>("tickers") { Description = "Tickers as Avanza shows them, e.g. VOLV-B or \"VOLV B\"", Arity = ArgumentArity.OneOrMore };
        var add = new Command("add", "Find each ticker with Avanza's public search (no login) and put the Stockholm (SEK) share on the research list.");
        common.AddTo(add, json: false);
        add.Arguments.Add(addTickers);
        add.Options.Add(configDir);
        add.SetAction(parse => Run(parse, services, common, record: null, async (ctx, output) =>
        {
            string path = Path.Combine(TradingCommands.ResolveConfigDir(parse.GetValue(configDir)), ResearchList.FileName);
            ResearchList list = LoadResearch(path);
            int failed = 0;
            foreach (string ticker in parse.GetValue(addTickers)!)
            {
                IReadOnlyList<InstrumentSearchHit> hits = await ctx.Connection.Gateway.SearchStocksAsync(ticker.Replace('-', ' ').Trim().ToUpperInvariant(), 20, ctx.Ct).ConfigureAwait(false);
                InstrumentSearchHit[] exact = [.. hits.Where(h => h.Ticker is { } t && SameTicker(t, ticker) && h.Currency == "SEK")];
                if (exact.Length != 1)
                {
                    failed++;
                    output.WriteLine(exact.Length == 0
                        ? $"{ticker}: no Stockholm (SEK) share with that ticker in Avanza's search."
                        : $"{ticker}: {exact.Length} shares match ({string.Join(", ", exact.Select(h => $"{h.Name}, orderbook {h.OrderbookId}"))}); give the exact ticker.");
                    continue;
                }

                InstrumentSearchHit hit = exact[0];
                if (list.Contains(hit.OrderbookId))
                {
                    output.WriteLine($"{hit.Ticker}: already on the research list.");
                    continue;
                }

                try
                {
                    list = list.With(new ResearchEntry(hit.OrderbookId, hit.Ticker!, hit.Name));
                }
                catch (ResearchListException ex)
                {
                    throw new ArgumentException(ex.Message, ex);
                }

                output.WriteLine($"added {hit.Ticker} ({hit.Name}, orderbook {hit.OrderbookId}, {hit.MarketPlaceName}).");
            }

            list.Save(path);
            output.WriteLine($"Research list: {list.Entries.Count} of {ResearchList.MaxNames} ({path}). Its bars are collected by 'qa intraday import' and after each Paper session.");
            return failed == 0 ? 0 : 1;
        }));

        var removeTickers = new Argument<string[]>("tickers") { Description = "Tickers on the research list", Arity = ArgumentArity.OneOrMore };
        var remove = new Command("remove", "Take shares off the research list. Their stored bars stay. Offline.");
        remove.Arguments.Add(removeTickers);
        remove.Options.Add(configDir);
        remove.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            string path = Path.Combine(TradingCommands.ResolveConfigDir(parse.GetValue(configDir)), ResearchList.FileName);
            ResearchList list = LoadResearch(path);
            foreach (string ticker in parse.GetValue(removeTickers)!)
            {
                ResearchEntry entry = list.Entries.FirstOrDefault(e => SameTicker(e.Ticker, ticker))
                                      ?? throw new ArgumentException($"{ticker} is not on the research list.");
                list = list.Without(entry.OrderbookId);
                w.WriteLine($"removed {entry.Ticker}.");
            }

            list.Save(path);
            return 0;
        }));

        var show = new Command("list", "Show the research list. Offline.");
        show.Options.Add(configDir);
        show.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            string path = Path.Combine(TradingCommands.ResolveConfigDir(parse.GetValue(configDir)), ResearchList.FileName);
            ResearchList list = LoadResearch(path);
            w.WriteLine(list.Entries.Count == 0
                ? $"The research list is empty ({path}). Add Stockholm shares with: qa intraday research add VOLV-B"
                : $"Research list, {list.Entries.Count} of {ResearchList.MaxNames}: {string.Join(", ", list.Entries.Select(e => e.Ticker))}");
            return 0;
        }));

        command.Subcommands.Add(add);
        command.Subcommands.Add(remove);
        command.Subcommands.Add(show);
        return command;
    }

    /// <summary>
    /// Collects 1- and 5-minute bars for <paramref name="names"/> (plan 17 step A2): one public chart call per name and
    /// resolution. A name that fails is reported and the others go on. Returns how many imports failed.
    /// </summary>
    internal static async Task<int> CollectIntradayAsync(
        IBrokerGateway gateway, string storePath, IReadOnlyList<IntradayName> names, ChartPeriod period, IReadOnlyList<ChartResolution> resolutions, TimeProvider time,
        TextWriter output, CancellationToken ct)
    {
        using HistoryStore history = HistoryStore.Open(storePath);
        int failed = 0, stored = 0;
        foreach (IntradayName name in names)
        {
            foreach (ChartResolution resolution in resolutions)
            {
                string label = $"{name.Ticker} {(resolution == ChartResolution.Minute ? "1-minute" : "5-minute")}";
                try
                {
                    IntradayImportReport r = await IntradayImporter.ImportAsync(history, gateway, name.Id, period, resolution, AvanzaConnection.PriceChartSourceVersion, time, ct)
                        .ConfigureAwait(false);
                    stored += r.Bars.New + r.Bars.Restated;
                    string span = r.FirstUtc is { } f && r.LastUtc is { } l
                        ? string.Create(CultureInfo.InvariantCulture, $" over {r.Days} day(s), {MarketTime.ToStockholm(f):yyyy-MM-dd HH:mm} to {MarketTime.ToStockholm(l):yyyy-MM-dd HH:mm}")
                        : string.Empty;
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{label}: {r.Bars.New} new, {r.Bars.Restated} restated, {r.Bars.Unchanged} unchanged bar(s){span}{(r.InProgress > 0 ? $"; {r.InProgress} still open, left out" : string.Empty)}."));
                }
                catch (Exception ex) when (ex is HistoryImportException or SchemaDriftException or BrokerUnavailableException)
                {
                    failed++;
                    output.WriteLine($"{label}: FAILED ({ex.Message})");
                }
            }
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Intraday: {stored} bar(s) stored for {names.Count} share(s){(failed > 0 ? $", {failed} import(s) failed" : string.Empty)}. Source: {AvanzaChartImporter.AvanzaPriceChart.Label}."));
        return failed;
    }

    /// <summary>The shares collected by default: the allowlist's Stockholm (SEK) shares and the research list (ADR 0006 D5).</summary>
    internal static IReadOnlyList<IntradayName> CollectedShares(string storePath, string configDir, TextWriter output)
    {
        var names = new List<IntradayName>();
        Universe universe;
        try
        {
            universe = Universe.Load(Path.Combine(configDir, Universe.FileName));
        }
        catch (TradingConfigException ex)
        {
            throw new ArgumentException(ex.Message, ex);
        }

        if (universe.Entries.Count > 0 && File.Exists(storePath))
        {
            using HistoryStore history = HistoryStore.Open(storePath);
            foreach (UniverseEntry e in universe.Entries)
            {
                string? currency = history.GetInstrument(e.OrderbookId)?.Instrument.Currency;
                if (currency == Markets.Stockholm.Currency)
                {
                    names.Add(new IntradayName(e.OrderbookId, e.Ticker));
                }
                else
                {
                    output.WriteLine($"{e.Ticker}: skipped, intraday research is Stockholm only (ADR 0006).");
                }
            }
        }

        foreach (ResearchEntry e in LoadResearch(Path.Combine(configDir, ResearchList.FileName)).Entries.Where(e => names.All(n => n.Id != e.OrderbookId)))
        {
            names.Add(new IntradayName(e.OrderbookId, e.Ticker));
        }

        return names;
    }

    /// <summary>Shares named on the command line: from the research list, else the instrument master.</summary>
    internal static List<IntradayName> NamedShares(string storePath, string configDir, IReadOnlyList<string> tickers)
    {
        ResearchList research = LoadResearch(Path.Combine(configDir, ResearchList.FileName));
        var names = new List<IntradayName>();
        foreach (string ticker in tickers)
        {
            if (research.Entries.FirstOrDefault(e => SameTicker(e.Ticker, ticker)) is { } listed)
            {
                names.Add(new IntradayName(listed.OrderbookId, listed.Ticker));
                continue;
            }

            using HistoryStore history = DataCommands.OpenExisting(storePath);
            InstrumentRecord r = DataCommands.FindInstrument(history, ticker, null, null).Instrument;
            if (r.Currency != Markets.Stockholm.Currency)
            {
                throw new ArgumentException($"{r.Ticker} trades in {r.Currency}; intraday research is Stockholm only (ADR 0006).");
            }

            names.Add(new IntradayName(r.OrderbookId, r.Ticker));
        }

        return names;
    }

    private static ResearchList LoadResearch(string path)
    {
        try
        {
            return ResearchList.Load(path);
        }
        catch (ResearchListException ex)
        {
            throw new ArgumentException(ex.Message, ex);
        }
    }

    /// <summary>"VOLV-B", "volv b" and "VOLV B" are the same ticker.</summary>
    private static bool SameTicker(string a, string b) =>
        string.Equals(a.Replace('-', ' ').Trim(), b.Replace('-', ' ').Trim(), StringComparison.OrdinalIgnoreCase);

    private static ChartPeriod ParsePeriod(string text) => text switch
    {
        "today" => ChartPeriod.Today,
        "one_week" => ChartPeriod.OneWeek,
        "one_month" => ChartPeriod.OneMonth,
        "three_months" => ChartPeriod.ThreeMonths,
        _ => throw new ArgumentException($"--period: '{text}' is not today, one_week, one_month or three_months."),
    };

    private static IReadOnlyList<ChartResolution> ParseResolutions(string text) => text switch
    {
        "both" => IntradayImporter.Resolutions,
        "minute" => [ChartResolution.Minute],
        "five_minutes" => [ChartResolution.FiveMinutes],
        _ => throw new ArgumentException($"--resolution: '{text}' is not minute, five_minutes or both."),
    };

    private static Command IntradayProbe(AvanzaCliServices services)
    {
        var common = new Common();
        var ticker = new Argument<string?>("ticker") { Description = "A share in the instrument master (default ERIC-B)", Arity = ArgumentArity.ZeroOrOne };
        var id = new Option<string?>("--id") { Description = "An orderbook id instead of a ticker" };
        var store = DataCommands.StoreOption();
        var recordDir = new Option<string>("--record-dir") { Description = "Raw recordings folder (git-ignored)", DefaultValueFactory = _ => Path.Combine("recordings", "live") };
        var noRecord = new Option<bool>("--no-record") { Description = "Do not record the answers" };
        var command = new Command(
            "probe",
            "Ask Avanza's public price chart, without a login, which bar sizes it gives for today, one week, one month and three months, and how many days of 1- and 5-minute bars. About 10 read-only calls; the answers are recorded.");
        common.AddTo(command, json: false);
        command.Arguments.Add(ticker);
        command.Options.Add(id);
        command.Options.Add(store);
        command.Options.Add(recordDir);
        command.Options.Add(noRecord);
        command.SetAction(parse => Run(parse, services, common, parse.GetValue(noRecord) ? null : parse.GetValue(recordDir), async (ctx, output) =>
        {
            string? asked = parse.GetValue(ticker);
            string? orderbook = parse.GetValue(id);
            if (asked is not null && orderbook is not null)
            {
                throw new ArgumentException("Give a ticker or --id, not both.");
            }

            (OrderbookId instrument, string name) = orderbook is not null
                ? (new OrderbookId(orderbook), "orderbook " + orderbook)
                : ProbeInstrument(parse.GetValue(store)!, asked ?? "ERIC-B");

            // Public: no login, ever (the Go SDK lists the price chart as public; docs/research/avanza-endpoints.md).
            output.WriteLine($"Avanza's price chart for {name}, without a login:");
            IReadOnlyList<ChartProbeRow> rows = await ChartProbe.RunAsync(ctx.Connection.Gateway, instrument, ctx.Ct).ConfigureAwait(false);
            ChartProbe.Write(output, rows);
            if (ctx.Connection.RecordingDirectory is { } dir)
            {
                output.WriteLine();
                output.WriteLine($"Raw answers: {dir}");
                output.WriteLine($"To keep them as test fixtures: qa recordings sanitize --in \"{dir}\" --out \"recordings/fixtures/avanza/{DateTime.UtcNow:yyyy-MM-dd}-chart\"");
            }

            return rows.All(r => r.Error is not null) ? 1 : 0;
        }));
        return command;
    }

    private static (OrderbookId Id, string Name) ProbeInstrument(string storePath, string ticker)
    {
        using HistoryStore history = DataCommands.OpenExisting(storePath);
        InstrumentRecord r = DataCommands.FindInstrument(history, ticker, null, null).Instrument;
        return (r.OrderbookId, $"{r.Ticker} (orderbook {r.OrderbookId})");
    }
}

/// <summary>A share whose intraday bars are collected.</summary>
internal sealed record IntradayName(OrderbookId Id, string Ticker);

/// <summary>One chart question and its answer (plan 17 step A1).</summary>
/// <param name="Asked">The resolution asked for, or null for the server's own choice.</param>
/// <param name="Answered">The resolution the server used, as it names it.</param>
/// <param name="Offered">The resolutions the server says it gives for this period.</param>
/// <param name="Days">The distinct Stockholm trading dates the bars cover.</param>
internal sealed record ChartProbeRow(
    ChartPeriod Period, ChartResolution? Asked, string? Answered, IReadOnlyList<string> Offered, int Bars, DateTimeOffset? First, DateTimeOffset? Last, int Days,
    string? Error);

/// <summary>
/// What intraday history Avanza's public price chart gives (plan 17 step A1, ADR 0006): for today, one week, one month
/// and three months, the server's own resolution and the ones it offers; then each offered 1- or 5-minute resolution
/// asked for, with how many bars and days come back. Read-only and public; a chart that suddenly wants a login stops it.
/// </summary>
internal static class ChartProbe
{
    /// <summary>The periods asked about: the short ones, where intraday bars can be.</summary>
    public static IReadOnlyList<ChartPeriod> Periods { get; } = [ChartPeriod.Today, ChartPeriod.OneWeek, ChartPeriod.OneMonth, ChartPeriod.ThreeMonths];

    /// <summary>The resolutions plan 17 would store (D3: 5 minutes; 1 minute for the spread and the fill check).</summary>
    public static IReadOnlyList<ChartResolution> Wanted { get; } = [ChartResolution.Minute, ChartResolution.FiveMinutes];

    public static async Task<IReadOnlyList<ChartProbeRow>> RunAsync(IBrokerGateway gateway, OrderbookId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var rows = new List<ChartProbeRow>();
        foreach (ChartPeriod period in Periods)
        {
            ChartProbeRow first = await AskAsync(gateway, id, period, null, ct).ConfigureAwait(false);
            rows.Add(first);
            foreach (ChartResolution wanted in Wanted)
            {
                bool offered = first.Offered.Any(o => ParseResolution(o) == wanted);
                if (offered && ParseResolution(first.Answered) != wanted)
                {
                    rows.Add(await AskAsync(gateway, id, period, wanted, ct).ConfigureAwait(false));
                }
            }
        }

        return rows;
    }

    public static void Write(TextWriter output, IReadOnlyList<ChartProbeRow> rows)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(rows);
        var table = new TextTable(("period", false), ("asked", false), ("answered", false), ("bars", true), ("days", true), ("from", false), ("to", false), ("offers", false));
        foreach (ChartProbeRow r in rows)
        {
            table.Add(
                Wire(r.Period.ToString()), r.Asked is { } a ? Wire(a.ToString()) : "(its own)", r.Error is null ? r.Answered ?? "?" : "ERROR",
                r.Error is null ? r.Bars.ToString(CultureInfo.InvariantCulture) : string.Empty, r.Error is null ? r.Days.ToString(CultureInfo.InvariantCulture) : string.Empty,
                Stamp(r.First), Stamp(r.Last), r.Error ?? string.Join(", ", r.Offered));
        }

        table.Write(output);
        output.WriteLine();
        foreach (ChartResolution wanted in Wanted)
        {
            ChartProbeRow[] with = [.. rows.Where(r => r.Error is null && ParseResolution(r.Answered) == wanted && r.Bars > 0)];
            string label = wanted == ChartResolution.Minute ? "1-minute bars" : "5-minute bars";
            output.WriteLine(with.Length == 0
                ? $"{label}: not offered for any of these periods."
                : $"{label}: {string.Join("; ", with.Select(r => string.Create(CultureInfo.InvariantCulture, $"{Wire(r.Period.ToString())} {r.Days} day(s) from {r.First:yyyy-MM-dd}")))}. The longest is how far back each daily collection must reach.");
        }
    }

    private static async Task<ChartProbeRow> AskAsync(IBrokerGateway gateway, OrderbookId id, ChartPeriod period, ChartResolution? resolution, CancellationToken ct)
    {
        try
        {
            PriceHistory h = await gateway.GetPriceHistoryAsync(id, period, resolution, ct).ConfigureAwait(false);
            int days = h.Bars.Select(b => DateOnly.FromDateTime(MarketTime.ToStockholm(b.TimestampUtc).DateTime)).Distinct().Count();
            return new ChartProbeRow(period, resolution, Wire(h.Resolution.ToString()), h.AvailableResolutions, h.Bars.Count,
                h.Bars.Count > 0 ? h.Bars[0].TimestampUtc : null, h.Bars.Count > 0 ? h.Bars[^1].TimestampUtc : null, days, null);
        }
        catch (Exception ex) when (ex is SchemaDriftException or BrokerUnavailableException)
        {
            // One question failing (an unexpected answer, a 400 for a combination it doesn't give) doesn't end the probe;
            // an expired session (the chart wants a login) or a gone endpoint does.
            return new ChartProbeRow(period, resolution, null, [], 0, null, null, 0, ex.Message);
        }
    }

    private static string Stamp(DateTimeOffset? utc) =>
        utc is { } t ? MarketTime.ToStockholm(t).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>"OneWeek" → "one_week", the chart's own naming.</summary>
    private static string Wire(string pascal) => string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    /// <summary>The broker's resolution names ("five_minutes", "FIVE_MINUTES") parsed as the gateway parses them; null if unknown.</summary>
    private static ChartResolution? ParseResolution(string? wire) =>
        string.IsNullOrWhiteSpace(wire) ? null
        : Enum.GetValues<ChartResolution>().Cast<ChartResolution?>()
            .FirstOrDefault(r => string.Equals(r.ToString(), wire.Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
}
