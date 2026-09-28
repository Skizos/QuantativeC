using QuantAnalyst.Avanza;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Trading.Pipeline;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Cli.Commands;

/// <summary>What one import stored: the instrument (and whether it was new) and the daily bars.</summary>
internal sealed record InstrumentImportResult(InstrumentTradingParams Params, WriteCounts Instrument, ImportReport Report, string? CalendarNote);

/// <summary>
/// Imports one instrument's daily bars from Avanza's price chart (read-only) and updates the instrument master. The one
/// place this happens, for <c>qa history import</c> and the Windows app's share search (docs/plans/15-share-search.md).
/// The caller has logged in and resolved the instrument.
/// </summary>
internal static class InstrumentImport
{
    /// <summary>How many years the app imports when a share is added from the search (like the names you have).</summary>
    public const int AppYears = 3;

    public static async Task<InstrumentImportResult> ImportAsync(
        IBrokerGateway gateway, InstrumentTradingParams instrument, string storePath, string? configDir, DateOnly from, DateOnly to, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(instrument);
        if (from > to)
        {
            throw new ArgumentException("--from is after --to.");
        }

        MarketCalendar? calendar = DataCommands.TryLoadCalendar(configDir, out string? calendarNote);
        using HistoryStore history = HistoryStore.Open(storePath);
        WriteCounts written = history.UpsertInstrument(
            InstrumentRecord.FromTradingParams(instrument), "avanza-orderbook", AvanzaConnection.OrderbookSourceVersion, instrument.KnownAtUtc);
        var provider = new AvanzaChartImporter(gateway, TimeProvider.System, AvanzaConnection.PriceChartSourceVersion);
        ImportReport report = await new HistoryImporter(history, TimeProvider.System, calendar)
            .ImportAsync(provider, instrument.OrderbookId, from, to, ct).ConfigureAwait(false);
        return new InstrumentImportResult(instrument, written, report, calendarNote);
    }
}

/// <summary>
/// Adding to the allowlist (R2, <c>config/universe.json</c>): only instruments in the instrument master, only SEK (v1),
/// and at most <see cref="MaxNames"/>, because the Paper session streams every allowlisted name and refuses to start with
/// more. Shared by <c>qa universe add</c> and the app's share search.
/// </summary>
internal static class Allowlist
{
    public const int MaxNames = AvanzaCommands.MaxStreamInstruments;

    /// <summary>The allowlist with <paramref name="ticker"/> added (unchanged when it is on it already).</summary>
    public static (Universe Universe, UniverseEntry Entry) Add(Universe universe, HistoryStore history, string ticker)
    {
        ArgumentNullException.ThrowIfNull(universe);
        InstrumentRecord r = DataCommands.FindInstrument(history, ticker, null, null).Instrument;
        Check(universe, r.OrderbookId, r.Ticker, r.Currency);
        var entry = new UniverseEntry(r.OrderbookId, r.Ticker, r.Name);
        return (universe.With(entry), entry);
    }

    /// <summary>Throws with the reason when the instrument may not join <paramref name="universe"/> (a name already on it may).</summary>
    public static void Check(Universe universe, OrderbookId id, string ticker, string currency)
    {
        ArgumentNullException.ThrowIfNull(universe);
        if (!string.Equals(currency, OrderPreparation.Currency, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{ticker} trades in {currency}; v1 trades SEK instruments only.");
        }

        if (!universe.Contains(id) && universe.Entries.Count >= MaxNames)
        {
            throw new ArgumentException(
                $"The allowlist already has {universe.Entries.Count} names, the most a Paper session can stream ({MaxNames}). Remove one first.");
        }
    }

    /// <summary>Adds <paramref name="ticker"/> to the allowlist file in <paramref name="configDir"/> and saves it.</summary>
    public static UniverseEntry AddAndSave(string configDir, string storePath, string ticker)
    {
        string path = Path.Combine(configDir, Universe.FileName);
        using HistoryStore history = DataCommands.OpenExisting(storePath);
        (Universe universe, UniverseEntry entry) = Add(Universe.Load(path), history, ticker);
        universe.Save(path);
        return entry;
    }
}
