using System.CommandLine;
using System.Globalization;
using QuantAnalyst.Avanza;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Trading.Reports;

namespace QuantAnalyst.Cli.Commands;

/// <summary>
/// Plan 24 B: the market index (e.g. OMX Stockholm 30) the weekly summary compares with. Its daily closes come from the
/// same public price chart as the shares' (no login). UNVERIFIED for an index until the owner's first import: the
/// reference clients document the chart route for stocks; one of them reads index info through the stock routes, which
/// suggests the chart works too (docs/research/avanza-endpoints.md). An answer of another shape fails the strict DTO
/// check and is reported; nothing is stored then.
/// </summary>
internal static partial class AvanzaCommands
{
    private static Command Benchmark(AvanzaCliServices services)
    {
        var configDir = TradingCommands.ConfigDirOption();
        var store = DataCommands.StoreOption();
        var command = new Command("benchmark", "The market index the weekly summary compares Paper with (plan 24 B): show it, set it, import its closes.");
        command.Options.Add(configDir);
        command.Options.Add(store);
        command.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            if (BenchmarkSettings.Load(TradingCommands.ResolveConfigDir(parse.GetValue(configDir))) is not { } b)
            {
                w.WriteLine("No benchmark set. Open the index on avanza.se (e.g. OMX Stockholm 30), copy the number from its page address, then: qa benchmark set --orderbook-id <number> --name \"OMX Stockholm 30\"");
                return 0;
            }

            string storePath = parse.GetValue(store)!;
            string stored = "no closes stored yet ('qa benchmark import')";
            if (File.Exists(storePath))
            {
                using HistoryStore history = DataCommands.OpenExisting(storePath);
                IReadOnlyList<StoredBar> bars = history.GetDailyBars(b.OrderbookId, AvanzaChartImporter.AvanzaPriceChart.Name);
                if (bars.Count > 0)
                {
                    stored = string.Create(CultureInfo.InvariantCulture, $"{bars.Count} closes, {bars[0].Bar.Date:yyyy-MM-dd} to {bars[^1].Bar.Date:yyyy-MM-dd}");
                }
            }

            w.WriteLine($"Benchmark: {b.Name} (Avanza orderbook {b.OrderbookId.Value}): {stored}.");
            return 0;
        }));
        command.Subcommands.Add(BenchmarkSet());
        command.Subcommands.Add(BenchmarkImport(services));
        return command;
    }

    private static Command BenchmarkSet()
    {
        var configDir = TradingCommands.ConfigDirOption();
        var id = new Option<string>("--orderbook-id") { Description = "Avanza's number for the index, from its page address on avanza.se", Required = true };
        var name = new Option<string>("--name") { Description = "What reports call it, e.g. \"OMX Stockholm 30\"", Required = true };
        var command = new Command("set", "Sets the market index (config/benchmark.json). It is never traded: the allowlist takes shares only.");
        foreach (Option o in new Option[] { configDir, id, name })
        {
            command.Options.Add(o);
        }

        command.SetAction(parse => TradingCommands.Execute(parse, w =>
        {
            string number = parse.GetValue(id)!.Trim();
            string label = parse.GetValue(name)!.Trim();
            if (number.Length == 0 || !number.All(char.IsAsciiDigit) || label.Length == 0)
            {
                throw new ArgumentException("--orderbook-id must be the number from the index's page address on avanza.se, and --name must not be empty.");
            }

            string dir = TradingCommands.ResolveConfigDir(parse.GetValue(configDir));
            new BenchmarkSettings(new OrderbookId(number), label).Save(dir);
            w.WriteLine($"Benchmark set: {label} (orderbook {number}). Import its closes with 'qa benchmark import'; Paper sessions and evening imports keep them up to date.");
            return 0;
        }));
        return command;
    }

    private static Command BenchmarkImport(AvanzaCliServices services)
    {
        var common = new Common();
        var configDir = TradingCommands.ConfigDirOption();
        var store = DataCommands.StoreOption();
        var from = new Option<string?>("--from") { Description = "First day, yyyy-MM-dd (default: a week before the last stored close, or a year back)" };
        var command = new Command("import", "Imports the index's daily closes from Avanza's public price chart (no login). Read-only.");
        common.AddTo(command, json: false);
        foreach (Option o in new Option[] { configDir, store, from })
        {
            command.Options.Add(o);
        }

        command.SetAction(parse => Run(parse, services, common, record: null, async (ctx, output) =>
        {
            string config = TradingCommands.ResolveConfigDir(parse.GetValue(configDir));
            DateOnly? start = parse.GetValue(from) is { } text
                ? DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) ? d : throw new ArgumentException($"--from: '{text}' is not yyyy-MM-dd.")
                : null;
            string said = await ImportBenchmarkAsync(ctx.Connection.Gateway, parse.GetValue(store)!, config, services.Time, start, ctx.Ct).ConfigureAwait(false)
                ?? throw new ArgumentException("No benchmark set: run 'qa benchmark' to see how.");
            output.WriteLine(said);
            return 0;
        }));
        return command;
    }

    /// <summary>
    /// Brings the benchmark's closes up to today; null when none is set. Throws the broker's or the store's exceptions
    /// (the command reports them; the session and the evening import turn them into a warning line).
    /// </summary>
    internal static async Task<string?> ImportBenchmarkAsync(IBrokerGateway gateway, string storePath, string configDir, TimeProvider time, DateOnly? from, CancellationToken ct)
    {
        if (BenchmarkSettings.Load(configDir) is not { } b)
        {
            return null;
        }

        DateOnly today = DateOnly.FromDateTime(MarketTime.ToStockholm(time.GetUtcNow()).DateTime);
        using HistoryStore history = HistoryStore.Open(storePath);
        DateOnly? last = history.GetDailyBars(b.OrderbookId, AvanzaChartImporter.AvanzaPriceChart.Name) is { Count: > 0 } bars ? bars[^1].Bar.Date : null;
        DateOnly start = from ?? (last is { } l ? l.AddDays(-7) : today.AddYears(-1));
        var provider = new AvanzaChartImporter(gateway, time, AvanzaConnection.PriceChartSourceVersion);
        ImportReport report = await new HistoryImporter(history, time).ImportAsync(provider, b.OrderbookId, start, today, ct).ConfigureAwait(false);
        return string.Create(CultureInfo.InvariantCulture,
            $"Benchmark {b.Name}: closes to {report.LastDate:yyyy-MM-dd} ({report.Bars.New} new, {report.Bars.Restated} restated){(report.Warnings.Count > 0 ? "; " + string.Join(" ", report.Warnings) : string.Empty)}.");
    }

    /// <summary>The benchmark refresh after a session or an evening import: informational, so a failure is a warning line.</summary>
    private static async Task RefreshBenchmarkAsync(IBrokerGateway gateway, string storePath, string configDir, TimeProvider time, TextWriter output, CancellationToken ct)
    {
        try
        {
            if (await ImportBenchmarkAsync(gateway, storePath, configDir, time, null, ct).ConfigureAwait(false) is { } said)
            {
                output.WriteLine(said);
            }
        }
        catch (Exception ex) when (ex is BrokerException or HistoryImportException or HistoryStoreException or IOException or Trading.Risk.TradingConfigException || DataCommands.IsStoreFailure(ex))
        {
            output.WriteLine($"warning: the benchmark's closes were not updated ({ex.Message}).");
        }
    }
}
