using System.CommandLine;
using System.Globalization;
using QuantAnalyst.Cli.Output;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Fx;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Cli.Commands;

/// <summary>
/// <c>qa fx import|show</c> (ADR 0005): the Riksbank's daily fixing for the currencies of the foreign shares (USD, CAD),
/// in SEK per unit, stored in the history store. <c>qa history import</c> of a USD or CAD share imports them too.
/// Import calls the Riksbank (never Avanza); show is offline.
/// </summary>
internal static class FxCommands
{
    public static Command Create(AvanzaCliServices services)
    {
        var command = new Command("fx", "FX rates for USD and CAD shares (ADR 0005): the Riksbank's daily fixing, SEK per unit, in the history store.");
        command.Subcommands.Add(Import(services));
        command.Subcommands.Add(Show());
        return command;
    }

    private static Command Import(AvanzaCliServices services)
    {
        var currencies = new Argument<string[]>("currencies") { Description = "USD, CAD or both", Arity = ArgumentArity.OneOrMore };
        var from = new Option<string>("--from") { Description = "First date, yyyy-MM-dd", Required = true };
        var to = new Option<string?>("--to") { Description = "Last date, yyyy-MM-dd (default: today)" };
        var store = DataCommands.StoreOption();
        var command = new Command("import", "Import the Riksbank's daily fixings (SEK per unit) into the history store. Calls the Riksbank, not Avanza.");
        command.Arguments.Add(currencies);
        command.Options.Add(from);
        command.Options.Add(to);
        command.Options.Add(store);
        command.SetAction(parse => DataCommands.Execute(parse, w =>
        {
            DateOnly first = DataCommands.ParseDate(parse.GetValue(from), "--from")!.Value;
            DateOnly last = DataCommands.ParseDate(parse.GetValue(to), "--to") ?? DateOnly.FromDateTime(MarketTime.ToStockholm(services.Time.GetUtcNow()).DateTime);
            if (first > last)
            {
                throw new ArgumentException("--from is after --to.");
            }

            string[] wanted = [.. parse.GetValue(currencies)!.Select(c => c.Trim().ToUpperInvariant()).Distinct()];
            IFxRateSource source = services.FxRates();
            using HistoryStore history = HistoryStore.Open(parse.GetValue(store)!);
            foreach (string currency in wanted)
            {
                FxImportReport report = FxImporter.ImportAsync(history, source, currency, first, last, services.Time, services.Cancellation).GetAwaiter().GetResult();
                w.WriteLine(Describe(report));
            }

            w.WriteLine($"Stored in {history.Path}. Source: {source.Source.Label}.");
            return 0;
        }));
        return command;
    }

    private static Command Show()
    {
        var currency = new Argument<string>("currency") { Description = "USD or CAD" };
        var from = new Option<string?>("--from") { Description = "First date, yyyy-MM-dd (default: the last 10 fixings)" };
        var to = new Option<string?>("--to") { Description = "Last date, yyyy-MM-dd" };
        var store = DataCommands.StoreOption();
        var command = new Command("show", "Show stored fixings (SEK per unit). Offline.");
        command.Arguments.Add(currency);
        command.Options.Add(from);
        command.Options.Add(to);
        command.Options.Add(store);
        command.SetAction(parse => DataCommands.Execute(parse, w =>
        {
            string ccy = parse.GetValue(currency)!.Trim().ToUpperInvariant();
            DateOnly? first = DataCommands.ParseDate(parse.GetValue(from), "--from");
            DateOnly? last = DataCommands.ParseDate(parse.GetValue(to), "--to");
            using HistoryStore history = DataCommands.OpenExisting(parse.GetValue(store)!);
            IReadOnlyList<StoredFxRate> rates = history.GetFxRates(ccy, RiksbankFxSource.Riksbank.Name, first, last);
            if (rates.Count == 0)
            {
                w.WriteLine($"No {ccy} fixings stored{(first is null && last is null ? string.Empty : " in that range")}. Import them: qa fx import {ccy} --from <yyyy-MM-dd>");
                return 0;
            }

            IEnumerable<StoredFxRate> shown = first is null && last is null ? rates.TakeLast(10) : rates;
            var table = new TextTable(("date", false), ($"SEK per {ccy}", true), ("known at (UTC)", false));
            foreach (StoredFxRate r in shown)
            {
                table.Add(r.Rate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Rate.SekPerUnit.ToString("0.0000", CultureInfo.InvariantCulture),
                    r.KnownAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            }

            table.Write(w);
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{rates.Count} fixing(s) {rates[0].Rate.Date:yyyy-MM-dd} to {rates[^1].Rate.Date:yyyy-MM-dd}; source {RiksbankFxSource.Riksbank.Label}."));
            return 0;
        }));
        return command;
    }

    /// <summary>"USD: 752 new, 0 restated, 0 unchanged fixing(s), 2023-09-18 to 2026-09-25; latest 9.4123 SEK."</summary>
    internal static string Describe(FxImportReport r) =>
        r.First is null
            ? $"{r.Currency}: no fixing in the range (weekends and holidays have none)."
            : string.Create(CultureInfo.InvariantCulture,
                $"{r.Currency}: {r.Rates.New} new, {r.Rates.Restated} restated, {r.Rates.Unchanged} unchanged fixing(s), {r.First:yyyy-MM-dd} to {r.Last:yyyy-MM-dd}; latest {r.LatestSekPerUnit:0.0000} SEK.");
}
