using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Cli.Output;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Calendar;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Cli.Commands;

/// <summary>Offline data verbs (Phase 4): <c>qa instruments</c> and <c>qa calendar</c>, plus shared store/config helpers.</summary>
internal static class DataCommands
{
    public const string DefaultStore = "data/quant.duckdb";

    public static IEnumerable<Command> Create()
    {
        yield return Instruments();
        yield return Calendar();
    }

    public static Option<string> StoreOption() =>
        new("--store") { Description = "History store file (DuckDB, git-ignored)", DefaultValueFactory = _ => DefaultStore };

    public static Option<string?> ConfigDirOption() =>
        new("--config-dir") { Description = "Folder with market-calendar.XSTO.<year>.json (default: ./config, then next to qa)" };

    // ---- qa instruments ------------------------------------------------------------------------------

    private static Command Instruments()
    {
        var asOf = new Option<string?>("--as-of") { Description = "Show the master as known at this time (ISO 8601; no offset = Stockholm time)" };
        var store = StoreOption();
        var json = new Option<bool>("--json") { Description = "JSON output" };
        var command = new Command("instruments", "List the instrument master (orderbook id ↔ ISIN ↔ ticker, tick table, trading model). Offline.");
        command.Options.Add(asOf);
        command.Options.Add(store);
        command.Options.Add(json);
        command.SetAction(parse => Execute(parse, w =>
        {
            DateTimeOffset? asOfUtc = ParseAsOf(parse.GetValue(asOf));
            using HistoryStore history = OpenExisting(parse.GetValue(store)!);
            IReadOnlyList<StoredInstrument> all = history.ListInstruments(asOfUtc);
            if (parse.GetValue(json))
            {
                w.WriteLine(JsonSerializer.Serialize(all.Select(i => new
                {
                    orderbookId = i.Instrument.OrderbookId.Value,
                    i.Instrument.Isin,
                    i.Instrument.Ticker,
                    i.Instrument.Name,
                    i.Instrument.Currency,
                    i.Instrument.MarketPlace,
                    i.Instrument.InstrumentType,
                    i.Instrument.TradingModel,
                    i.Instrument.VolumeFactor,
                    tickTable = JsonDocument.Parse(i.Instrument.TickTableJson).RootElement,
                    i.Instrument.ValidFrom,
                    i.KnownAtUtc,
                    i.Source,
                    i.SourceVersion,
                }), QaCli.Json));
                return 0;
            }

            var table = new TextTable(("ticker", false), ("orderbook", false), ("isin", false), ("name", false), ("market", false), ("ccy", false),
                ("model", false), ("tick bands", true), ("known at", false));
            foreach (StoredInstrument i in all)
            {
                InstrumentRecord r = i.Instrument;
                int bands = JsonDocument.Parse(r.TickTableJson).RootElement.GetArrayLength();
                table.Add(r.Ticker, r.OrderbookId.Value, r.Isin ?? "-", r.Name, r.MarketPlace, r.Currency, r.TradingModel.ToString(),
                    bands.ToString(CultureInfo.InvariantCulture), Local(i.KnownAtUtc));
            }

            table.Write(w);
            w.WriteLine($"{all.Count} instrument(s) in {history.Path}.");
            return 0;
        }));
        return command;
    }

    // ---- qa calendar ---------------------------------------------------------------------------------

    private static Command Calendar()
    {
        var year = new Option<int?>("--year") { Description = "Year to list (default: this year)" };
        var date = new Option<string?>("--date") { Description = "Classify one date, yyyy-MM-dd" };
        var market = new Option<string>("--market")
        {
            Description = $"Which market: {string.Join(", ", Markets.All.Select(m => $"{m.Mic} ({m.Name}, {m.Currency})"))}",
            DefaultValueFactory = _ => Markets.Stockholm.Mic,
        };
        var configDir = ConfigDirOption();
        var command = new Command("calendar", "A market's trading calendar (Nasdaq Stockholm unless --market): closed days, half days and verification status. Offline.");
        command.Options.Add(year);
        command.Options.Add(date);
        command.Options.Add(market);
        command.Options.Add(configDir);
        command.SetAction(parse => Execute(parse, w =>
        {
            string dir = ResolveConfigDir(parse.GetValue(configDir));
            string mic = parse.GetValue(market)!.Trim().ToUpperInvariant();
            MarketInfo info = Markets.ForMic(mic)
                ?? throw new ArgumentException($"--market: {mic} is not a market the program trades on; use {string.Join(", ", Markets.All.Select(m => m.Mic))}.");
            MarketCalendar calendar = MarketCalendarLoader.LoadDirectory(dir, info.Mic);
            string local = LocalName(info);
            if (ParseDate(parse.GetValue(date), "--date") is { } d)
            {
                TradingDay day = calendar.Classify(d);
                w.WriteLine(day.IsTradingDay
                    ? $"{d:yyyy-MM-dd} ({d.DayOfWeek}): {day.Kind} trading day, {day.Open:HH\\:mm}–{day.Close:HH\\:mm} {local}{(day.Name is null ? string.Empty : $" ({day.Name})")}{StockholmTimes(calendar, d, day)}."
                    : $"{d:yyyy-MM-dd} ({d.DayOfWeek}): {day.Kind}{(day.Name is null ? string.Empty : $" ({day.Name})")}.");
            }
            else
            {
                int y = parse.GetValue(year) ?? DateTime.UtcNow.Year;
                CalendarYear cy = calendar.GetYear(y);
                int full = 0, half = 0, closed = 0;
                for (var day = new DateOnly(y, 1, 1); day.Year == y; day = day.AddDays(1))
                {
                    switch (calendar.Classify(day).Kind)
                    {
                        case TradingDayKind.Full: full++; break;
                        case TradingDayKind.Half: half++; break;
                        case TradingDayKind.Closed: closed++; break;
                        default: break;
                    }
                }

                w.WriteLine($"{calendar.Mic} {y}: {full} full days, {half} half days (close {cy.HalfDayClose:HH\\:mm}), {closed} weekday closures; regular session {cy.RegularOpen:HH\\:mm}–{cy.RegularClose:HH\\:mm} {local}.");
                var table = new TextTable(("date", false), ("day", false), ("kind", false), ("name", false));
                foreach (CalendarEntry e in cy.Closed.Select(e => (e, TradingDayKind.Closed)).Concat(cy.HalfDays.Select(e => (e, TradingDayKind.Half)))
                             .OrderBy(x => x.e.Date).Select(x => x.e))
                {
                    TradingDay td = calendar.Classify(e.Date);
                    table.Add(e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e.Date.DayOfWeek.ToString()[..3], td.Kind.ToString(), e.Name);
                }

                table.Write(w);
            }

            w.WriteLine($"Loaded from {dir}: {string.Join(", ", calendar.Years.Order().Select(y => $"{y} {(calendar.GetYear(y).VerifiedOn is { } v ? $"verified {v:yyyy-MM-dd}" : "NOT VERIFIED")}"))}.");
            if (!calendar.IsVerified)
            {
                w.WriteLine($"UNVERIFIED: compare with {calendar.GetYear(calendar.UnverifiedYears.First()).SourceUrl} and set verified_on. Confirm and Auto modes will refuse to start until then.");
            }

            return 0;
        }));
        return command;
    }

    // ---- shared helpers ------------------------------------------------------------------------------

    /// <summary>Like <see cref="QaCli.Execute"/>, plus the data-layer exceptions.</summary>
    public static int Execute(ParseResult parse, Func<TextWriter, int> body) => QaCli.Execute(parse, w =>
    {
        try
        {
            return body(w);
        }
        catch (Exception ex) when (ex is HistoryStoreException or CalendarConfigException or FxUnavailableException || IsStoreFailure(ex))
        {
            throw new InvalidDataException(StoreFailureMessage(ex), ex);
        }
    });

    /// <summary>
    /// Failures opening or using the DuckDB store, including its native library not loading. They become a readable
    /// "error:" line instead of an unhandled exception.
    /// </summary>
    public static bool IsStoreFailure(Exception ex) =>
        ex is System.Data.Common.DbException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
        || (ex is TypeInitializationException t && t.InnerException is not null && IsStoreFailure(t.InnerException));

    public static string StoreFailureMessage(Exception ex)
    {
        Exception root = ex is TypeInitializationException { InnerException: { } inner } ? inner : ex;
        return root switch
        {
            System.Data.Common.DbException => $"the history store could not be used: {root.Message}",
            DllNotFoundException or BadImageFormatException or EntryPointNotFoundException =>
                $"DuckDB's native library could not be loaded ({root.GetType().Name}: {root.Message}). Rebuild with 'dotnet build QuantAnalyst.sln' and run qa from the build output.",
            _ => root.Message,
        };
    }

    public static HistoryStore OpenExisting(string path) =>
        File.Exists(path) ? HistoryStore.Open(path) : throw new ArgumentException($"No history store at '{path}'. Run 'qa history import <TICKER>' first.");

    public static StoredInstrument FindInstrument(HistoryStore store, string? ticker, string? id, DateTimeOffset? asOfUtc)
    {
        if ((ticker is null) == (id is null))
        {
            throw new ArgumentException("Give either a ticker or --id <orderbookId>.");
        }

        return (id is not null ? store.GetInstrument(new OrderbookId(id), asOfUtc) : store.FindByTicker(ticker!, asOfUtc))
               ?? throw new ArgumentException($"'{ticker ?? id}' is not in the instrument master{(asOfUtc is null ? string.Empty : " at that time")}. Run 'qa history import {(ticker is null ? "--id " + id : ticker)}' first.");
    }

    public static DateOnly? ParseDate(string? text, string option) =>
        text is null ? null
        : DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) ? d
        : throw new ArgumentException($"{option}: '{text}' is not a yyyy-MM-dd date.");

    /// <summary>ISO 8601 with an offset, or a Stockholm wall-clock time without one (DST-ambiguous times are refused).</summary>
    public static DateTimeOffset? ParseAsOf(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset withOffset) && HasOffset(text))
        {
            return withOffset.ToUniversalTime();
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            return MarketTime.TryStockholmToUtc(local, out DateTimeOffset utc)
                ? utc
                : throw new ArgumentException($"--as-of: {text} does not exist or is ambiguous in Stockholm time (DST change); give an offset.");
        }

        throw new ArgumentException($"--as-of: '{text}' is not an ISO 8601 time.");

        static bool HasOffset(string s) => s.EndsWith('Z') || s.EndsWith('z') || (s.Length > 6 && (s[^6] is '+' or '-') && s[^3] == ':');
    }

    public static string ResolveConfigDir(string? explicitDir)
    {
        if (explicitDir is not null)
        {
            return explicitDir;
        }

        foreach (string candidate in new[] { "config", Path.Combine(AppContext.BaseDirectory, "config") })
        {
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "market-calendar.*.json").Any())
            {
                return candidate;
            }
        }

        throw new ArgumentException("No calendar folder found (./config or next to qa); pass --config-dir.");
    }

    /// <summary>The calendar for import warnings, or null with a note when it can't be loaded (imports still run).</summary>
    public static MarketCalendar? TryLoadCalendar(string? explicitDir, out string? note)
    {
        try
        {
            MarketCalendar calendar = MarketCalendarLoader.LoadDirectory(ResolveConfigDir(explicitDir));
            note = calendar.IsVerified ? null : $"Calendar check used the UNVERIFIED draft for {string.Join(", ", calendar.UnverifiedYears)}.";
            return calendar;
        }
        catch (Exception ex) when (ex is ArgumentException or CalendarConfigException)
        {
            note = $"Calendar not loaded ({ex.Message}); bars were not checked against trading days.";
            return null;
        }
    }

    /// <summary>"Stockholm", "New York" or "Toronto": whose clock a market's session times are on.</summary>
    private static string LocalName(MarketInfo market) => market.TimeZoneId[(market.TimeZoneId.IndexOf('/', StringComparison.Ordinal) + 1)..].Replace('_', ' ');

    /// <summary>For another market's trading day: its session in Stockholm time, e.g. " (15:30–22:00 Stockholm)".</summary>
    private static string StockholmTimes(MarketCalendar calendar, DateOnly date, TradingDay day) =>
        calendar.TimeZoneId == Markets.Stockholm.TimeZoneId
            ? string.Empty
            : $" ({MarketTime.ToStockholm(calendar.ToUtc(date, day.Open!.Value)):HH\\:mm}–{MarketTime.ToStockholm(calendar.ToUtc(date, day.Close!.Value)):HH\\:mm} Stockholm)";

    private static string Local(DateTimeOffset utc) =>
        MarketTime.ToStockholm(utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
