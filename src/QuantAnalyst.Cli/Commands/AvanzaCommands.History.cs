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

            MarketCalendar? calendar = DataCommands.TryLoadCalendar(parse.GetValue(configDir), out string? calendarNote);
            await ctx.Connection.Authenticator.LoginAsync(ctx.Ct).ConfigureAwait(false);
            InstrumentTradingParams p = idText is not null
                ? await ctx.Connection.Gateway.GetTradingParamsAsync(new OrderbookId(idText), ctx.Ct).ConfigureAwait(false)
                : await TickerResolver.ResolveAsync(ctx.Connection.Gateway, tickerText!, ctx.Ct).ConfigureAwait(false);

            using HistoryStore history = HistoryStore.Open(parse.GetValue(store)!);
            WriteCounts instrument = history.UpsertInstrument(
                InstrumentRecord.FromTradingParams(p), "avanza-orderbook", AvanzaConnection.OrderbookSourceVersion, p.KnownAtUtc);
            var provider = new AvanzaChartImporter(ctx.Connection.Gateway, TimeProvider.System, AvanzaConnection.PriceChartSourceVersion);
            ImportReport report = await new HistoryImporter(history, TimeProvider.System, calendar)
                .ImportAsync(provider, p.OrderbookId, fromDate, toDate, ctx.Ct).ConfigureAwait(false);

            output.WriteLine($"{p.TickerSymbol} {p.Name} (orderbook {p.OrderbookId}, {p.Isin}, {p.MarketPlace}, {p.Currency})");
            output.WriteLine($"Instrument master: {(instrument.New > 0 ? "added" : instrument.Restated > 0 ? "new version stored (attributes changed)" : "unchanged")}.");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Daily bars {report.FirstDate:yyyy-MM-dd}..{report.LastDate:yyyy-MM-dd}: {report.Bars.New} new, {report.Bars.Restated} restated, {report.Bars.Unchanged} unchanged."));
            output.WriteLine($"Stored in {history.Path}, known at {Local(report.KnownAtUtc)} (Europe/Stockholm).");
            output.WriteLine($"Source: {report.Source.Label}.");
            output.WriteLine(report.Source.Notes);
            if (calendarNote is not null)
            {
                output.WriteLine(calendarNote);
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
}
