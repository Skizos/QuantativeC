using System.CommandLine;
using System.Globalization;
using QuantAnalyst.Cli.Output;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Cli.Commands;

internal static partial class AvanzaCommands
{
    // ---- qa intraday probe (plan 17, ADR 0006) -----------------------------------------------------------

    private static Command Intraday(AvanzaCliServices services)
    {
        var command = new Command("intraday", "Intraday research (plan 17, ADR 0006): what intraday history Avanza gives. Read-only; no orders.");
        command.Subcommands.Add(IntradayProbe(services));
        return command;
    }

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
