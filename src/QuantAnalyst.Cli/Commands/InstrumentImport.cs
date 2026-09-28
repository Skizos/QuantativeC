using QuantAnalyst.Avanza;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Fx;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Cli.Commands;

/// <summary>What one import stored: the instrument (and whether it was new), the daily bars, and a foreign share's FX fixings.</summary>
internal sealed record InstrumentImportResult(InstrumentTradingParams Params, WriteCounts Instrument, ImportReport Report, string? CalendarNote, FxImportReport? Fx = null);

/// <summary>
/// Imports one instrument's daily bars from Avanza's price chart (read-only) and updates the instrument master. The one
/// place this happens, for <c>qa history import</c> and the Windows app's share search (docs/plans/15-share-search.md).
/// The caller has logged in and resolved the instrument.
/// </summary>
internal static class InstrumentImport
{
    /// <summary>How many years the app imports when a share is added from the search (like the names you have).</summary>
    public const int AppYears = 3;

    /// <summary>How far before the first bar a foreign share's FX fixings start, so its first day has a rate.</summary>
    public const int FxLeadDays = 10;

    /// <param name="fx">
    /// Where a USD or CAD share's FX fixings come from (ADR 0005). They are imported first, so a share whose rates can't
    /// be read stores nothing.
    /// </param>
    public static async Task<InstrumentImportResult> ImportAsync(
        IBrokerGateway gateway, IFxRateSource fx, InstrumentTradingParams instrument, string storePath, string? configDir, DateOnly from, DateOnly to, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(fx);
        ArgumentNullException.ThrowIfNull(instrument);
        if (from > to)
        {
            throw new ArgumentException("--from is after --to.");
        }

        MarketCalendar? calendar = DataCommands.TryLoadCalendar(configDir, out string? calendarNote);
        using HistoryStore history = HistoryStore.Open(storePath);
        FxImportReport? fxReport = Markets.ForCurrency(instrument.Currency) is not null && Markets.IsForeign(instrument.Currency)
            ? await FxImporter.ImportAsync(history, fx, instrument.Currency, from.AddDays(-FxLeadDays), to, TimeProvider.System, ct).ConfigureAwait(false)
            : null;
        WriteCounts written = history.UpsertInstrument(
            InstrumentRecord.FromTradingParams(instrument), "avanza-orderbook", AvanzaConnection.OrderbookSourceVersion, instrument.KnownAtUtc);
        var provider = new AvanzaChartImporter(gateway, TimeProvider.System, AvanzaConnection.PriceChartSourceVersion);
        ImportReport report = await new HistoryImporter(history, TimeProvider.System, calendar)
            .ImportAsync(provider, instrument.OrderbookId, from, to, ct).ConfigureAwait(false);
        return new InstrumentImportResult(instrument, written, report, calendarNote, fxReport);
    }
}

/// <summary>
/// Changing the allowlist (R2, <c>config/universe.json</c>). A name joins only from the instrument master, only in SEK,
/// USD or CAD (ADR 0005: foreign shares trade on paper only), and only while there are fewer than <see cref="MaxNames"/>, because the Paper session streams every allowlisted
/// name and refuses to start with more. Shared by <c>qa universe add|remove</c> and the app's share search.
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
        if (Markets.ForCurrency(currency) is null)
        {
            throw new ArgumentException($"{ticker} trades in {currency}; the program trades shares in {Markets.CurrencyList} (ADR 0005).");
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

    /// <summary>The allowlist without <paramref name="ticker"/> ("ERIC-B", "eric b" and "ERIC B" are the same name).</summary>
    public static (Universe Universe, UniverseEntry Entry) Remove(Universe universe, string ticker)
    {
        ArgumentNullException.ThrowIfNull(universe);
        UniverseEntry entry = universe.Entries.FirstOrDefault(e => SameTicker(e.Ticker, ticker))
            ?? throw new ArgumentException($"{ticker} is not in the allowlist.");
        return (universe.Without(entry.OrderbookId), entry);
    }

    /// <summary>Removes <paramref name="ticker"/> from the allowlist file in <paramref name="configDir"/> and saves it.</summary>
    public static UniverseEntry RemoveAndSave(string configDir, string ticker)
    {
        string path = Path.Combine(configDir, Universe.FileName);
        (Universe universe, UniverseEntry entry) = Remove(Universe.Load(path), ticker);
        universe.Save(path);
        return entry;
    }

    private static bool SameTicker(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string t) => t.Trim().Replace('-', ' ').Replace('_', ' ');
}
