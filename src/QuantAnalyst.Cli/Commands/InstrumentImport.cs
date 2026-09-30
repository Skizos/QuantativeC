using System.Globalization;
using QuantAnalyst.Avanza;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Fx;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Cli.Commands;

/// <summary>What one import stored: the instrument (and whether it was new), the daily bars, and a foreign share's FX fixings.</summary>
/// <param name="CorporateNote">Plan 21: the dividends and share count stored, or why they could not be read.</param>
/// <param name="TradingModel">Plan 22: how a share outside XSTO was measured to trade; null for XSTO, US and Canadian shares.</param>
internal sealed record InstrumentImportResult(
    InstrumentTradingParams Params, WriteCounts Instrument, ImportReport Report, string? CalendarNote, FxImportReport? Fx = null, string? CorporateNote = null,
    TradingModelEvidence? TradingModel = null);

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
    /// <param name="evidence">How the share was measured to trade, when the caller did it already (the app's Add checks first).</param>
    public static async Task<InstrumentImportResult> ImportAsync(
        IBrokerGateway gateway, IFxRateSource fx, InstrumentTradingParams instrument, string storePath, string? configDir, DateOnly from, DateOnly to, CancellationToken ct,
        TradingModelEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(fx);
        ArgumentNullException.ThrowIfNull(instrument);
        if (from > to)
        {
            throw new ArgumentException("--from is after --to.");
        }

        MarketCalendar? calendar = DataCommands.TryLoadCalendar(configDir, out string? calendarNote, Markets.ForCurrency(instrument.Currency)?.Mic ?? Markets.Stockholm.Mic);
        using HistoryStore history = HistoryStore.Open(storePath);
        FxImportReport? fxReport = Markets.ForCurrency(instrument.Currency) is not null && Markets.IsForeign(instrument.Currency)
            ? await FxImporter.ImportAsync(history, fx, instrument.Currency, from.AddDays(-FxLeadDays), to, TimeProvider.System, ct).ConfigureAwait(false)
            : null;
        (InstrumentRecord record, evidence) = await RecordAsync(gateway, instrument, history, ct, evidence).ConfigureAwait(false);
        WriteCounts written = history.UpsertInstrument(record, "avanza-orderbook", AvanzaConnection.OrderbookSourceVersion, instrument.KnownAtUtc);
        var provider = new AvanzaChartImporter(gateway, TimeProvider.System, AvanzaConnection.PriceChartSourceVersion);
        ImportReport report = await new HistoryImporter(history, TimeProvider.System, calendar)
            .ImportAsync(provider, instrument.OrderbookId, from, to, ct).ConfigureAwait(false);
        return new InstrumentImportResult(
            instrument, written, report, calendarNote, fxReport, await CorporateAsync(history, gateway, instrument.OrderbookId, ct).ConfigureAwait(false), evidence);
    }

    /// <summary>
    /// Plan 22: the instrument master row, with its trading model measured when the share is outside XSTO (one public chart
    /// call, unless <paramref name="evidence"/> is given). An Unknown measurement keeps the model <paramref name="history"/>
    /// has stored. The one place a row is built for storing: the import, the app's Add and the Paper history refresh.
    /// </summary>
    public static async Task<(InstrumentRecord Record, TradingModelEvidence? Evidence)> RecordAsync(
        IBrokerGateway gateway, InstrumentTradingParams instrument, HistoryStore? history, CancellationToken ct, TradingModelEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(instrument);
        InstrumentRecord record = InstrumentRecord.FromTradingParams(instrument);
        bool refusedAnyway = Markets.ForCurrency(record.Currency) is null
            || (!Markets.IsForeign(record.Currency) && !Allowlist.SwedishMarketplaces.Contains(record.MarketPlace, StringComparer.Ordinal));
        if (TradingModelCheck.KnownContinuous(record.MarketPlace, record.Currency) || refusedAnyway)
        {
            return (record, null); // nothing to measure: continuous, or refused for its currency or marketplace first
        }

        evidence ??= await TradingModelCheck.MeasureAsync(gateway, instrument.OrderbookId, ct).ConfigureAwait(false);
        return (TradingModelCheck.Apply(record, evidence, history?.GetInstrument(instrument.OrderbookId)?.Instrument.TradingModel), evidence);
    }

    /// <summary>
    /// Plan 21: the share's dividends and share count (public, read-only; informational, ADR 0002 Tier B), stored so
    /// 'qa history dividends' can check the history. A failure is a note: the bars are imported either way.
    /// </summary>
    private static async Task<string> CorporateAsync(HistoryStore history, IBrokerGateway gateway, OrderbookId id, CancellationToken ct)
    {
        try
        {
            CorporateSnapshot s = await CorporateDataImporter.ImportAsync(history, gateway, id, AvanzaConnection.StockDetailsSourceVersion, TimeProvider.System, ct).ConfigureAwait(false);
            DateOnly today = DateOnly.FromDateTime(MarketTime.ToStockholm(s.Data.RetrievedAtUtc).DateTime);
            int announced = s.Data.Dividends.Count(d => d.ExDate > today);
            string shares = s.Data.SharesOutstanding is { } n ? string.Create(CultureInfo.InvariantCulture, $"; share count {n:N0}") : "; no share count";
            return string.Create(CultureInfo.InvariantCulture,
                $"Dividends: {s.Data.Dividends.Count - announced} past and {announced} announced stored{shares} (check the history against them with 'qa history dividends').");
        }
        catch (Exception ex) when (ex is BrokerException or HistoryStoreException)
        {
            return $"warning: dividends and the share count were not stored ({ex.Message}); the bars are.";
        }
    }
}

/// <summary>
/// Changing the allowlist (R2, <c>config/universe.json</c>). A name joins only from the instrument master, only in SEK,
/// USD or CAD (ADR 0005: foreign shares trade on paper only), only on a marketplace whose courtage is known, only when it
/// trades continuously (plans 18 and 22: the fill model assumes it), and only while there are fewer than
/// <see cref="MaxNames"/>, because the Paper session polls every listed name and refuses to start with more. Shared by
/// <c>qa universe add|remove</c> and the app's share search.
/// </summary>
internal static class Allowlist
{
    /// <summary>
    /// Plan 22: the most names on the list. Paper polls each share's market data (no stream since 2026-09-30); at 10 the
    /// polls use about 70 % of the request budget and keep every quote under R15's 10 s (<see cref="PaperPolling"/>).
    /// Confirm still streams each share and takes <see cref="AvanzaCommands.MaxStreamInstruments"/>.
    /// </summary>
    public const int MaxNames = 10;

    /// <summary>
    /// Plan 22: the Swedish marketplaces whose courtage the cost files give: Nasdaq Stockholm's main market and First
    /// North (<c>marketplace_courtage.FNSE</c>). Spotlight, NGM and the rest cost more and are not modelled.
    /// </summary>
    public static IReadOnlyList<string> SwedishMarketplaces { get; } = ["XSTO", "FNSE"];

    /// <summary>The allowlist with <paramref name="ticker"/> added (unchanged when it is on it already).</summary>
    public static (Universe Universe, UniverseEntry Entry) Add(Universe universe, HistoryStore history, string ticker)
    {
        ArgumentNullException.ThrowIfNull(universe);
        InstrumentRecord r = DataCommands.FindInstrument(history, ticker, null, null).Instrument;
        Check(universe, r);
        var entry = new UniverseEntry(r.OrderbookId, r.Ticker, r.Name);
        return (universe.With(entry), entry);
    }

    /// <summary>Throws with the reason when the instrument may not join <paramref name="universe"/> (a name already on it may).</summary>
    /// <param name="evidence">How the share was measured to trade (plan 22), for the reason given when it is refused.</param>
    public static void Check(Universe universe, InstrumentRecord instrument, TradingModelEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(instrument);
        if (Markets.ForCurrency(instrument.Currency) is null)
        {
            throw new ArgumentException($"{instrument.Ticker} trades in {instrument.Currency}; the program trades shares in {Markets.CurrencyList} (ADR 0005).");
        }

        if (!Markets.IsForeign(instrument.Currency) && !SwedishMarketplaces.Contains(instrument.MarketPlace, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"{instrument.Ticker} is listed on {Marketplace(instrument)}: the program knows the courtage for Nasdaq Stockholm (XSTO) and First North (FNSE) only, so it can't cost its trades.");
        }

        if (NotContinuous(instrument, evidence) is { } why)
        {
            throw new ArgumentException(why);
        }

        if (!universe.Contains(instrument.OrderbookId) && universe.Entries.Count >= MaxNames)
        {
            throw new ArgumentException(
                $"The allowlist already has {universe.Entries.Count} names, the most a Paper session polls ({MaxNames}). Remove one first.");
        }
    }

    /// <summary>
    /// Plan 18 (P5), plan 22: why a share that does not trade continuously can't be on the list, or null when it does.
    /// Nasdaq Stockholm's main market (XSTO) and the US and Canadian exchanges trade continuously; a share elsewhere (First
    /// North) is measured on its last week of trades (<see cref="TradingModelCheck"/>). The fill model assumes continuous
    /// trading, so the backtest and the Paper decision leave out a share that trades only in auctions.
    /// </summary>
    public static string? NotContinuous(InstrumentRecord instrument, TradingModelEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return instrument.TradingModel switch
        {
            TradingModel.Continuous => null,
            TradingModel.PeriodicAuction =>
                $"{instrument.Ticker} is listed on {Marketplace(instrument)} and trades only in auctions ({evidence?.Reason ?? "Nasdaq's First North auction model"}): "
                + "the backtest and the Paper fills assume continuous trading, so it can't be traded (docs/plans/22-first-north-and-ten-names.md).",
            _ =>
                $"{instrument.Ticker} is listed on {Marketplace(instrument)}, not Nasdaq Stockholm's main market (XSTO), and it can't be told yet whether it trades continuously"
                + $" ({evidence?.Reason ?? "not measured: import it again with 'qa history import'"}). The backtest and the Paper fills assume continuous trading; try again when it has traded more"
                + " (docs/plans/22-first-north-and-ten-names.md).",
        };
    }

    private static string Marketplace(InstrumentRecord r) => string.IsNullOrWhiteSpace(r.MarketPlace) ? "an unknown marketplace" : $"'{r.MarketPlace}'";

    /// <summary>Adds <paramref name="ticker"/> to the allowlist file in <paramref name="configDir"/> and saves it.</summary>
    public static UniverseEntry AddAndSave(string configDir, string storePath, string ticker)
    {
        string path = Path.Combine(configDir, Universe.FileName);
        using HistoryStore history = DataCommands.OpenExisting(storePath);
        (Universe universe, UniverseEntry entry) = Add(Universe.Load(path), history, ticker);
        universe.Save(path);
        return entry;
    }

    /// <summary>
    /// The allowlist without <paramref name="ticker"/> ("ERIC-B", "eric b" and "ERIC B" are the same name). Plan 21: a share
    /// still <paramref name="held"/> is not dropped but moved to the exiting list, which the sessions sell; an exiting
    /// share is dropped once it is no longer held.
    /// </summary>
    /// <returns><c>Exiting</c> is true when the share was kept for selling.</returns>
    public static (Universe Universe, UniverseEntry Entry, bool Exiting) Remove(Universe universe, string ticker, IReadOnlyDictionary<OrderbookId, long>? held = null)
    {
        ArgumentNullException.ThrowIfNull(universe);
        long Held(OrderbookId id) => held?.GetValueOrDefault(id) ?? 0;
        if (universe.Entries.FirstOrDefault(e => SameTicker(e.Ticker, ticker)) is { } listed)
        {
            return Held(listed.OrderbookId) > 0 ? (universe.Exit(listed.OrderbookId), listed, true) : (universe.Without(listed.OrderbookId), listed, false);
        }

        UniverseEntry exiting = universe.Exiting.FirstOrDefault(e => SameTicker(e.Ticker, ticker))
            ?? throw new ArgumentException($"{ticker} is not in the allowlist.");
        return Held(exiting.OrderbookId) is var left and > 0
            ? throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"{exiting.Ticker} is still held ({left} shares) and on the exiting list: the next session sells it; remove it again once it is sold."))
            : (universe.Without(exiting.OrderbookId), exiting, false);
    }

    /// <summary>
    /// Removes <paramref name="ticker"/> from the allowlist file in <paramref name="configDir"/> and saves it; a share the
    /// Paper book in <paramref name="paperDir"/> still holds moves to the exiting list (plan 21).
    /// </summary>
    public static (UniverseEntry Entry, bool Exiting) RemoveAndSave(string configDir, string ticker, string? paperDir = null)
    {
        string path = Path.Combine(configDir, Universe.FileName);
        (Universe universe, UniverseEntry entry, bool exiting) = Remove(Universe.Load(path), ticker, paperDir is null ? null : PaperBook.HeldIn(paperDir));
        universe.Save(path);
        return (entry, exiting);
    }

    /// <summary>What <c>qa universe remove</c> and the app say after a removal.</summary>
    public static string Removed(UniverseEntry entry, bool exiting) => exiting
        ? $"{entry.Ticker} is still held: it moves to the exiting list, and the next session sells it (sells only; it no longer counts towards the {MaxNames}). Run 'qa universe remove {entry.Ticker.Replace(' ', '-')}' again once it is sold."
        : $"removed {entry.Ticker} ({entry.OrderbookId})";

    private static bool SameTicker(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string t) => t.Trim().Replace('-', ' ').Replace('_', ' ');
}

/// <summary>
/// Plan 22: how often Paper polls each share's market data. Every share is polled on the same interval, 0.7 s per share
/// polled, at least 5 s (the old rate) and at most 7 s, so 10 shares make 1.43 requests/s (about 70 % of the 2/s budget,
/// ADR 0002 §3) and a quote stays under R15's 10 s between polls.
/// </summary>
internal static class PaperPolling
{
    /// <summary>More shares than this (the list plus the exiting ones) would take the polls past the budget or R15.</summary>
    public const int MaxPolled = 14;

    public static TimeSpan Interval(int shares) =>
        TimeSpan.FromMilliseconds(Math.Clamp(700 * Math.Max(shares, 1), 5_000, 7_000));
}
