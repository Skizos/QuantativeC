using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Avanza;
using QuantAnalyst.Cli.Output;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Calendar;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Cli.Commands;

internal static partial class AvanzaCommands
{
    // ---- qa history import | show -------------------------------------------------------------------

    private static Command History(AvanzaCliServices services)
    {
        var command = new Command("history", "Daily price history in the local store (data/quant.duckdb), with known-at versioning.");
        command.Subcommands.Add(HistoryImport(services));
        command.Subcommands.Add(HistoryShow());
        command.Subcommands.Add(HistoryDividends());
        return command;
    }

    private static Command HistoryImport(AvanzaCliServices services)
    {
        var common = new Common();
        var ticker = new Argument<string?>("ticker") { Description = "Ticker, e.g. ERIC-B", Arity = ArgumentArity.ZeroOrOne };
        var id = new Option<string?>("--id") { Description = "Avanza orderbook id instead of a ticker" };
        var from = new Option<string?>("--from") { Description = "First date, yyyy-MM-dd (default: one year ago)" };
        var to = new Option<string?>("--to") { Description = "Last date, yyyy-MM-dd (default: today)" };
        var store = DataCommands.StoreOption();
        var configDir = DataCommands.ConfigDirOption();
        var command = new Command(
            "import",
            "Import daily bars from Avanza's price chart (read-only) and update the instrument master. Re-imports only add changed rows.");
        command.Arguments.Add(ticker);
        common.AddTo(command, json: false);
        command.Options.Add(id);
        command.Options.Add(from);
        command.Options.Add(to);
        command.Options.Add(store);
        command.Options.Add(configDir);
        command.SetAction(parse => Run(parse, services, common, record: null, async (ctx, output) =>
        {
            DateOnly today = DateOnly.FromDateTime(MarketTime.ToStockholm(DateTimeOffset.UtcNow).DateTime);
            DateOnly toDate = DataCommands.ParseDate(parse.GetValue(to), "--to") ?? today;
            DateOnly fromDate = DataCommands.ParseDate(parse.GetValue(from), "--from") ?? toDate.AddYears(-1);
            if (fromDate > toDate)
            {
                throw new ArgumentException("--from is after --to.");
            }

            string? tickerText = parse.GetValue(ticker);
            string? idText = parse.GetValue(id);
            if ((tickerText is null) == (idText is null))
            {
                throw new ArgumentException("Give either a ticker or --id <orderbookId>.");
            }

            await ctx.Connection.Authenticator.LoginAsync(ctx.Ct).ConfigureAwait(false);
            InstrumentTradingParams p = idText is not null
                ? await ctx.Connection.Gateway.GetTradingParamsAsync(new OrderbookId(idText), ctx.Ct).ConfigureAwait(false)
                : await TickerResolver.ResolveAsync(ctx.Connection.Gateway, tickerText!, ctx.Ct).ConfigureAwait(false);

            string storePath = parse.GetValue(store)!;
            InstrumentImportResult result = await InstrumentImport.ImportAsync(
                ctx.Connection.Gateway, services.FxRates(), p, storePath, parse.GetValue(configDir), fromDate, toDate, ctx.Ct).ConfigureAwait(false);
            WriteCounts instrument = result.Instrument;
            ImportReport report = result.Report;

            output.WriteLine($"{p.TickerSymbol} {p.Name} (orderbook {p.OrderbookId}, {p.Isin}, {p.MarketPlace}, {p.Currency})");
            output.WriteLine($"Instrument master: {(instrument.New > 0 ? "added" : instrument.Restated > 0 ? "new version stored (attributes changed)" : "unchanged")}.");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Daily bars {report.FirstDate:yyyy-MM-dd}..{report.LastDate:yyyy-MM-dd}: {report.Bars.New} new, {report.Bars.Restated} restated, {report.Bars.Unchanged} unchanged."));
            output.WriteLine($"Stored in {storePath}, known at {Local(report.KnownAtUtc)} (Europe/Stockholm).");
            output.WriteLine($"Source: {report.Source.Label}.");
            output.WriteLine(report.Source.Notes);
            if (result.CalendarNote is not null)
            {
                output.WriteLine(result.CalendarNote);
            }

            if (result.Fx is { } fx)
            {
                output.WriteLine($"FX fixings (ADR 0005): {FxCommands.Describe(fx)} Source: {fx.Source.Label}.");
            }

            if (result.CorporateNote is { } corporate)
            {
                output.WriteLine(corporate);
            }

            foreach (string warning in report.Warnings)
            {
                output.WriteLine($"warning: {warning}");
            }

            return 0;
        }));
        return command;
    }

    private static Command HistoryShow()
    {
        var ticker = new Argument<string?>("ticker") { Description = "Ticker as stored, e.g. ERIC-B", Arity = ArgumentArity.ZeroOrOne };
        var id = new Option<string?>("--id") { Description = "Avanza orderbook id instead of a ticker" };
        var from = new Option<string?>("--from") { Description = "First date, yyyy-MM-dd" };
        var to = new Option<string?>("--to") { Description = "Last date, yyyy-MM-dd" };
        var asOf = new Option<string?>("--as-of") { Description = "Show what the store knew at this time (ISO 8601; no offset = Stockholm time)" };
        var source = new Option<string>("--source") { Description = "Data source", DefaultValueFactory = _ => AvanzaChartImporter.AvanzaPriceChart.Name };
        var store = DataCommands.StoreOption();
        var json = new Option<bool>("--json") { Description = "JSON output" };
        var command = new Command("show", "Show stored daily bars with their source labels (offline; no login).");
        command.Arguments.Add(ticker);
        command.Options.Add(id);
        command.Options.Add(from);
        command.Options.Add(to);
        command.Options.Add(asOf);
        command.Options.Add(source);
        command.Options.Add(store);
        command.Options.Add(json);
        command.SetAction(parse => DataCommands.Execute(parse, w =>
        {
            DateTimeOffset? asOfUtc = DataCommands.ParseAsOf(parse.GetValue(asOf));
            using HistoryStore history = DataCommands.OpenExisting(parse.GetValue(store)!);
            StoredInstrument instrument = DataCommands.FindInstrument(history, parse.GetValue(ticker), parse.GetValue(id), asOfUtc);
            string sourceName = parse.GetValue(source)!;
            DataSourceInfo info = history.GetSource(sourceName)
                                  ?? throw new ArgumentException($"No data from source '{sourceName}' in {history.Path}.");
            IReadOnlyList<StoredBar> bars = history.GetDailyBars(
                instrument.Instrument.OrderbookId, sourceName, DataCommands.ParseDate(parse.GetValue(from), "--from"), DataCommands.ParseDate(parse.GetValue(to), "--to"), asOfUtc);

            if (parse.GetValue(json))
            {
                w.WriteLine(JsonSerializer.Serialize(new
                {
                    orderbookId = instrument.Instrument.OrderbookId.Value,
                    instrument.Instrument.Ticker,
                    instrument.Instrument.Currency,
                    source = info,
                    asOfUtc,
                    bars = bars.Select(b => new { date = b.Bar.Date, b.Bar.Open, b.Bar.High, b.Bar.Low, b.Bar.Close, b.Bar.Volume, b.KnownAtUtc, b.SourceVersion }),
                }, QaCli.Json));
                return 0;
            }

            InstrumentRecord r = instrument.Instrument;
            w.WriteLine($"{r.Ticker} {r.Name} (orderbook {r.OrderbookId}, {r.Isin ?? "no ISIN"}, {r.MarketPlace}, {r.Currency})");
            w.WriteLine($"Source: {info.Label}.");
            w.WriteLine($"As known at: {(asOfUtc is { } t ? Local(t) + " (Europe/Stockholm)" : "latest")}.");
            var table = new TextTable(("date", false), ("open", true), ("high", true), ("low", true), ("close", true), ("volume", true), ("known at", false));
            foreach (StoredBar b in bars)
            {
                table.Add(b.Bar.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Num(b.Bar.Open), Num(b.Bar.High), Num(b.Bar.Low), Num(b.Bar.Close),
                    b.Bar.Volume.ToString(CultureInfo.InvariantCulture), Local(b.KnownAtUtc));
            }

            table.Write(w);
            w.WriteLine($"{bars.Count} bar(s); prices in {r.Currency}; dates are Nasdaq Stockholm trading dates.");
            return 0;
        }));
        return command;
    }
    private static Command HistoryDividends()
    {
        var ticker = new Argument<string?>("ticker") { Description = "Ticker as stored, e.g. ERIC-B", Arity = ArgumentArity.ZeroOrOne };
        var id = new Option<string?>("--id") { Description = "Avanza orderbook id instead of a ticker" };
        var store = DataCommands.StoreOption();
        var json = new Option<bool>("--json") { Description = "JSON output" };
        var command = new Command(
            "dividends",
            "Show a share's stored dividends and check the stored price history on each ex-date: does a backtest on it include dividends? (Plan 21; offline, no login.)");
        command.Arguments.Add(ticker);
        command.Options.Add(id);
        command.Options.Add(store);
        command.Options.Add(json);
        command.SetAction(parse => DataCommands.Execute(parse, w =>
        {
            using HistoryStore history = DataCommands.OpenExisting(parse.GetValue(store)!);
            InstrumentRecord r = DataCommands.FindInstrument(history, parse.GetValue(ticker), parse.GetValue(id), null).Instrument;
            DividendEvent[] dividends = [.. history.GetDividends(r.OrderbookId, CorporateDataImporter.AvanzaStockDetails.Name).Select(d => d.Dividend)];
            IReadOnlyList<StoredBar> bars = history.GetDailyBars(r.OrderbookId, AvanzaChartImporter.AvanzaPriceChart.Name);
            (IReadOnlyList<DividendGap> gaps, DividendVerdict verdict) = DividendCheck.Check(dividends, [.. bars.Select(b => b.Bar)], r.Currency);
            StoredShareCount? shares = history.LatestShareCount(r.OrderbookId, CorporateDataImporter.AvanzaStockDetails.Name, DateOnly.MaxValue);

            if (parse.GetValue(json))
            {
                w.WriteLine(JsonSerializer.Serialize(new
                {
                    orderbookId = r.OrderbookId.Value,
                    r.Ticker,
                    r.Currency,
                    sharesOutstanding = shares?.Shares,
                    dividends = gaps.Select(g => new
                    {
                        g.Dividend.ExDate,
                        g.Dividend.PaymentDate,
                        g.Dividend.Amount,
                        g.Dividend.Currency,
                        g.Dividend.Type,
                        g.PreviousClose,
                        g.ExOpen,
                        g.Yield,
                        g.Gap,
                        g.Reading,
                    }),
                    verdict = verdict.ToString(),
                }, QaCli.Json));
                return 0;
            }

            w.WriteLine($"{r.Ticker} {r.Name} (orderbook {r.OrderbookId}, {r.Currency})");
            if (dividends.Length == 0)
            {
                w.WriteLine($"No dividends stored. 'qa history import {r.Ticker.Replace(' ', '-')}' stores them (and every Paper session does for the shares it trades).");
                return 0;
            }

            w.WriteLine($"Source: {CorporateDataImporter.AvanzaStockDetails.Label}.");
            var table = new TextTable(("ex-date", false), ("amount", true), ("paid", false), ("close before", true), ("ex open", true), ("yield", true), ("gap", true), ("reading", false));
            foreach (DividendGap g in gaps)
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                table.Add(g.Dividend.ExDate.ToString("yyyy-MM-dd", c), string.Create(c, $"{g.Dividend.Amount:0.####} {g.Dividend.Currency}"),
                    g.Dividend.PaymentDate?.ToString("yyyy-MM-dd", c) ?? "-", g.PreviousClose is { } p ? Num(p) : "-", g.ExOpen is { } o ? Num(o) : "-",
                    g.Yield is { } y ? y.ToString("0.00%", c) : "-", g.Gap is { } gap ? gap.ToString("+0.00%;-0.00%;0.00%", c) : "-", g.Reading);
            }

            table.Write(w);
            if (shares is not null)
            {
                w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Share count {shares.Shares:N0} (as of {shares.AsOf:yyyy-MM-dd}); a split multiplies it, and the Paper book follows it."));
            }

            w.WriteLine(DividendCheck.Describe(verdict));
            return 0;
        }));
        return command;
    }
}
