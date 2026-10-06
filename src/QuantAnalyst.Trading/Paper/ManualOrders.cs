using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Core.Orders;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Paper;

/// <summary>What the owner asks for by hand (plan 23).</summary>
public enum ManualAction
{
    Buy,
    Sell,

    /// <summary>Give a manual share back to the strategy.</summary>
    Release,
}

/// <summary>A request the owner placed by hand (plan 23): an order for the Paper session to send, or a release.</summary>
/// <param name="Id">E.g. "M261001-7f3a".</param>
/// <param name="Limit">The owner's limit in the share's currency; null: marketable (the ask for a buy, the bid for a sell).</param>
/// <param name="Source">"cli" or "app".</param>
public sealed record ManualOrderRequest(
    string Id, ManualAction Action, OrderbookId OrderbookId, string Ticker, long Quantity, decimal? Limit, DateTimeOffset CreatedUtc, string Source)
{
    /// <summary>E.g. "Buy 7 ERIC B (limit 70.5)", "Sell 7 ERIC B (at the bid)", "Release ERIC B".</summary>
    public string Describe() => Action == ManualAction.Release
        ? $"Release {Ticker}"
        : string.Create(CultureInfo.InvariantCulture,
            $"{Action} {Quantity} {Ticker} ({(Limit is { } l ? $"limit {l:0.####}" : Action == ManualAction.Buy ? "at the ask" : "at the bid")})");
}

/// <summary>What became of a request: sent (the gateway accepted it), rejected, expired, cancelled, released, interrupted.</summary>
public sealed record ManualOrderOutcome(ManualOrderRequest Request, string Outcome, string Message, DateTimeOffset AtUtc);

/// <summary>
/// Plan 23: the owner's manual requests, as files in <c>state/paper/manual/</c> (one per request, written atomically), so
/// the CLI and the app can place them while a session runs. The session takes a request by moving it to
/// <c>taken/</c> (never twice), then appends its outcome to <c>done.jsonl</c>.
/// </summary>
public sealed class ManualOrderInbox
{
    public const string DirectoryName = "manual";
    public const string DoneFileName = "done.jsonl";
    private const string TakenDirectoryName = "taken";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower), new OrderbookIdConverter() },
    };

    // A cancel from the CLI or the app may hold done.jsonl for a moment (Windows refuses a second writer).
    private static readonly TimeSpan[] AppendRetries = [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(250)];

    private readonly TimeProvider _time;

    /// <param name="paperDirectory">The Paper book's folder, <c>state/paper</c>.</param>
    public ManualOrderInbox(string paperDirectory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(paperDirectory);
        Directory = Path.Combine(paperDirectory, DirectoryName);
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public string Directory { get; }

    private string TakenDirectory => Path.Combine(Directory, TakenDirectoryName);

    /// <summary>Places a request; it waits until a Paper session takes it.</summary>
    public ManualOrderRequest Place(ManualAction action, OrderbookId id, string ticker, long quantity, decimal? limit, string source)
    {
        ArgumentNullException.ThrowIfNull(ticker);
        if (action != ManualAction.Release && quantity <= 0)
        {
            throw new ArgumentException("The quantity must be a whole number of shares above zero.");
        }

        if (limit is <= 0)
        {
            throw new ArgumentException("A limit must be above zero.");
        }

        System.IO.Directory.CreateDirectory(Directory);
        DateTimeOffset now = _time.GetUtcNow();
        string day = MarketTime.ToStockholm(now).ToString("yyMMdd", CultureInfo.InvariantCulture);
        string requestId;
        do
        {
            requestId = $"M{day}-{Guid.NewGuid().ToString("N")[..4]}";
        }
        while (File.Exists(PendingPath(requestId)) || File.Exists(Path.Combine(TakenDirectory, requestId + ".json")));

        var request = new ManualOrderRequest(requestId, action, id, ticker, action == ManualAction.Release ? 0 : quantity, limit, now, source);
        string temp = Path.Combine(Directory, requestId + ".tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(request, Json));
        File.Move(temp, PendingPath(requestId));
        return request;
    }

    /// <summary>The requests still waiting, oldest first (two placed in the same instant in the order their files were written).</summary>
    public IReadOnlyList<ManualOrderRequest> Pending() =>
        !System.IO.Directory.Exists(Directory)
            ? []
            : [.. System.IO.Directory.EnumerateFiles(Directory, "*.json")
                .Select(f => (Request: Read(f), Written: File.GetLastWriteTimeUtc(f)))
                .Where(x => x.Request is not null)
                .OrderBy(x => x.Request!.CreatedUtc).ThenBy(x => x.Written).ThenBy(x => x.Request!.Id, StringComparer.Ordinal)
                .Select(x => x.Request!)];

    /// <summary>Removes a waiting request; false when there is none (unknown, or a session has taken it).</summary>
    public bool Cancel(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        string path = PendingPath(id.Trim());
        if (Read(path) is not { } request)
        {
            return false;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            return false;
        }

        _ = TryAppendDone(new ManualOrderOutcome(request, "cancelled", "cancelled by the owner before it was sent", _time.GetUtcNow()));
        return true;
    }

    /// <summary>The outcomes so far (all days, oldest first), or of one Stockholm <paramref name="day"/>.</summary>
    public IReadOnlyList<ManualOrderOutcome> Done(DateOnly? day = null)
    {
        string path = Path.Combine(Directory, DoneFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        var result = new List<ManualOrderOutcome>();
        foreach (string line in File.ReadAllLines(path).Where(l => l.Length > 0))
        {
            ManualOrderOutcome? outcome = JsonSerializer.Deserialize<ManualOrderOutcome>(line, Json);
            if (outcome is not null && (day is null || DateOnly.FromDateTime(MarketTime.ToStockholm(outcome.AtUtc).DateTime) == day))
            {
                result.Add(outcome);
            }
        }

        return result;
    }

    /// <summary>Takes a waiting request for the session (moves it to <c>taken/</c>); null when it is gone (cancelled meanwhile).</summary>
    internal ManualOrderRequest? Take(ManualOrderRequest request)
    {
        System.IO.Directory.CreateDirectory(TakenDirectory);
        try
        {
            File.Move(PendingPath(request.Id), Path.Combine(TakenDirectory, request.Id + ".json"));
            return request;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the outcome of a taken request. It never throws on a busy or locked file (the CLI or the app may append a
    /// cancel at the same moment): <paramref name="error"/> says what could not be written; the audit has the outcome.
    /// </summary>
    internal ManualOrderOutcome Finish(ManualOrderRequest request, string outcome, string message, out string? error)
    {
        var done = new ManualOrderOutcome(request, outcome, message, _time.GetUtcNow());
        error = TryAppendDone(done);
        try
        {
            File.Delete(Path.Combine(TakenDirectory, request.Id + ".json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in taken/: the next session reports it as interrupted, and never sends it again.
            error ??= ex.Message;
        }

        return done;
    }

    /// <summary>Requests a session took but did not finish (it stopped in between): never sent again, only reported.</summary>
    internal IReadOnlyList<ManualOrderRequest> Interrupted() =>
        !System.IO.Directory.Exists(TakenDirectory) ? [] : [.. System.IO.Directory.EnumerateFiles(TakenDirectory, "*.json").Select(Read).OfType<ManualOrderRequest>()];

    private string PendingPath(string id) => Path.Combine(Directory, id + ".json");

    /// <summary>Appends an outcome to <c>done.jsonl</c>, retrying a moment while another process writes it; the error when it can't.</summary>
    private string? TryAppendDone(ManualOrderOutcome outcome)
    {
        string line = JsonSerializer.Serialize(outcome, Json) + "\n";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.AppendAllText(Path.Combine(Directory, DoneFileName), line);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == AppendRetries.Length)
                {
                    return ex.Message;
                }

                Thread.Sleep(AppendRetries[attempt]);
            }
        }
    }

    private static ManualOrderRequest? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ManualOrderRequest>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or JsonException)
        {
            return null;
        }
    }

    private sealed class OrderbookIdConverter : JsonConverter<OrderbookId>
    {
        public override OrderbookId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, OrderbookId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }
}

/// <summary>Where a share's market stands for a manual request (plan 23), as the session sees it.</summary>
/// <param name="Open">Inside today's trading window (R16).</param>
/// <param name="PreviousWindowCloseUtc">
/// The end of the trading window (R16) on the market's last trading day before today. A request is for the first window
/// that ends after it was placed: one placed before this end has expired; one placed later (also between the window's
/// end and the close) is for today.
/// </param>
public sealed record ManualWindow(bool Open, DateTimeOffset PreviousWindowCloseUtc);

/// <summary>
/// Plan 23: the Paper session's side of the manual requests. It turns a due request into an intent for the gateway
/// (the same pipeline and risk checks as the strategy's orders), applies releases, expires what waited past a close,
/// and records every outcome (<c>done.jsonl</c> and the audit's <c>manual-order</c> records).
/// </summary>
public sealed class ManualOrderDesk(ManualOrderInbox inbox, PaperBook book, IQuoteSource quotes, IInstrumentCatalog instruments, PreTradeRiskEngine risk, AuditLog audit, TextWriter output)
{
    public const string StrategyId = "manual";

    private readonly HashSet<string> _waiting = [];

    /// <summary>Reports requests a stopped session took but never finished. They are not sent again.</summary>
    public void Recover()
    {
        foreach (ManualOrderRequest r in inbox.Interrupted())
        {
            Record(r, "interrupted", "a session took it and stopped before it finished: check the orders in 'qa report eod'; it is not sent again");
        }
    }

    /// <summary>
    /// The requests to act on now: releases are applied at once; a request for a share no session trades is rejected; one
    /// that waited past its market's close expires; an order whose market is in its trading window and has a price
    /// becomes an intent. The rest keep waiting.
    /// </summary>
    public IReadOnlyList<(ManualOrderRequest Request, OrderIntent Intent, InstrumentSpec Spec)> Due(DateTimeOffset now, Func<InstrumentSpec, ManualWindow?> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var due = new List<(ManualOrderRequest, OrderIntent, InstrumentSpec)>();
        foreach (ManualOrderRequest r in inbox.Pending())
        {
            if (r.Action == ManualAction.Release)
            {
                if (inbox.Take(r) is not null)
                {
                    bool released = book.ReleaseManual(r.OrderbookId);
                    Record(r, "released", released
                        ? $"{r.Ticker} is the strategy's again: the next decision trades it to its target"
                        : $"{r.Ticker} was not manual: nothing to release");
                }

                continue;
            }

            InstrumentSpec? spec = instruments.Find(r.OrderbookId);
            if (spec is null)
            {
                if (inbox.Take(r) is not null)
                {
                    Record(r, "rejected", $"{r.Ticker} is not traded by this session (not on the list, or skipped today): add it to the list first (R2)");
                }

                continue;
            }

            if (window(spec) is not { } w)
            {
                continue; // its market does not trade today: it waits
            }

            if (r.CreatedUtc < w.PreviousWindowCloseUtc)
            {
                if (inbox.Take(r) is not null)
                {
                    Record(r, "expired", string.Create(CultureInfo.InvariantCulture,
                        $"placed before the trading window of {MarketTime.ToStockholm(w.PreviousWindowCloseUtc):yyyy-MM-dd} closed and no session sent it that day; a request is for one trading day"));
                }

                continue;
            }

            if (!w.Open)
            {
                continue;
            }

            Quote? quote = quotes.Latest(r.OrderbookId);
            decimal? reference = risk.ReferencePrice(quote, now);
            OrderSide side = r.Action == ManualAction.Buy ? OrderSide.Buy : OrderSide.Sell;
            decimal? limit = r.Limit ?? (side == OrderSide.Buy ? quote?.Ask : quote?.Bid) ?? reference;
            if (limit is null || reference is null)
            {
                if (_waiting.Add(r.Id))
                {
                    output.WriteLine($"{Local(now)} manual {r.Describe()}: waiting for a live price");
                }

                continue;
            }

            if (inbox.Take(r) is null)
            {
                continue; // cancelled meanwhile
            }

            string reason = string.Create(CultureInfo.InvariantCulture, $"manual {side} by the owner ({r.Id})");
            due.Add((r, new OrderIntent(r.OrderbookId, r.Ticker, side, r.Quantity, limit, reason, reference, now, StrategyId), spec));
        }

        return due;
    }

    /// <summary>
    /// A market's close: the orders still waiting for a share of that market (<paramref name="trades"/>) that were placed
    /// before its trading window ended (<paramref name="windowCloseUtc"/>) expire now, so the day's report shows them. One
    /// placed after the window ended waits for the next trading day.
    /// </summary>
    public void ExpireAtClose(Func<InstrumentSpec, bool> trades, DateTimeOffset windowCloseUtc)
    {
        ArgumentNullException.ThrowIfNull(trades);
        foreach (ManualOrderRequest r in inbox.Pending())
        {
            if (r.Action == ManualAction.Release || r.CreatedUtc >= windowCloseUtc || instruments.Find(r.OrderbookId) is not { } spec || !trades(spec))
            {
                continue;
            }

            if (inbox.Take(r) is not null)
            {
                Record(r, "expired", string.Create(CultureInfo.InvariantCulture,
                    $"the trading window closed at {MarketTime.ToStockholm(windowCloseUtc):HH:mm} before it was sent{(_waiting.Contains(r.Id) ? " (no live price came)" : string.Empty)}; a request is for one trading day"));
            }
        }
    }

    /// <summary>The gateway's answer to a manual order. A refused order (no broker acceptance) does not keep the share manual unless it was before.</summary>
    public void Sent(ManualOrderRequest request, SubmitResult result, bool wasManual)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        bool accepted = result.Status == SubmitStatus.Accepted;
        if (!accepted && !wasManual)
        {
            book.ReleaseManual(request.OrderbookId);
        }

        string detail = result.Order is { } o
            ? string.Create(CultureInfo.InvariantCulture, $"{o.State}, filled {o.FilledVolume}/{o.Volume}{(o.AverageFillPrice is { } p ? $" @ {p:0.####}" : string.Empty)}")
            : result.Message;
        Record(request, accepted ? "sent" : "rejected", accepted
            ? $"{detail}; {request.Ticker} is now manual: the strategy leaves it until 'qa paper release {request.Ticker.Replace(' ', '-')}'"
            : $"{result.Status}: {detail}");
    }

    /// <summary>A manual order taken but not sent before its market closed or the session stopped.</summary>
    public void NotSent(ManualOrderRequest request, bool wasManual, string why)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!wasManual)
        {
            book.ReleaseManual(request.OrderbookId);
        }

        Record(request, "expired", why);
    }

    private void Record(ManualOrderRequest r, string outcome, string message)
    {
        ManualOrderOutcome done = inbox.Finish(r, outcome, message, out string? error);
        audit.Append("manual-order", new
        {
            r.Id,
            action = r.Action.ToString(),
            orderbookId = r.OrderbookId.Value,
            r.Ticker,
            r.Quantity,
            r.Limit,
            r.Source,
            r.CreatedUtc,
            outcome,
            message,
        });
        output.WriteLine($"{Local(done.AtUtc)} manual {r.Describe()} [{r.Id}]: {outcome} ({message})");
        if (error is not null)
        {
            output.WriteLine($"  (not written to {ManualOrderInbox.DirectoryName}/{ManualOrderInbox.DoneFileName}: {error}; the audit has it)");
        }
    }

    private static string Local(DateTimeOffset utc) => MarketTime.ToStockholm(utc).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
