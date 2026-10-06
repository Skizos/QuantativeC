using System.Text.Json;
using System.Text.Json.Serialization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Model;

namespace QuantAnalyst.Trading.Paper;

/// <summary>A paper book operation that must not happen (a sell above the position, cash below zero): a bug upstream.</summary>
public sealed class PaperBookException(string message) : Exception(message);

/// <param name="CostBasis">In SEK, fees included.</param>
/// <param name="LastFillPrice">In the share's currency.</param>
/// <param name="Currency">The share's currency (ADR 0005): its value in SEK is quantity × price × the day's rate.</param>
/// <param name="LastMark">Its price at the last session's close (plan 21), which the split guard compares with; null before the first close.</param>
public sealed record PaperPosition(OrderbookId OrderbookId, string Ticker, long Quantity, decimal CostBasis, decimal LastFillPrice, string Currency = "SEK", decimal? LastMark = null);

/// <summary>A split the owner applied by hand (<c>qa paper split</c>, plan 21) that Avanza's share count has not shown yet.</summary>
public sealed record SplitByHand(OrderbookId OrderbookId, decimal Ratio, DateOnly Day);

/// <summary>
/// The paper account: cash, positions and fees, in SEK. It is the Paper source of truth for the account state (the
/// risk engine's inputs) and for reconciliation. With a directory it persists to <c>state/paper/book.json</c> after
/// every change (atomic replace) and appends each fill to <c>fills.jsonl</c>. A US or Canadian share (ADR 0005) is
/// bought and sold at the rate of the day (<see cref="Fx"/>): cash and cost in SEK, prices in its own currency.
/// </summary>
public sealed class PaperBook : IAccountState
{
    public const string FileName = "book.json";
    public const string FillsFileName = "fills.jsonl";
    private const string Format = "qa-paper-book/1";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };

    private readonly Lock _lock = new();
    private readonly string? _directory;
    private readonly IQuoteSource? _marks;
    private readonly IFxRates _fx;
    private readonly TimeProvider _time;
    private readonly Dictionary<OrderbookId, PaperPosition> _positions = [];
    private readonly Dictionary<Guid, decimal> _reserved = [];
    private decimal _cash;
    private decimal _fees;
    private decimal _realized;
    private DateOnly? _startOfDayDate;
    private decimal _startOfDayValue;
    private DateOnly? _corporateThrough;
    private decimal _dividends;
    private readonly HashSet<OrderbookId> _heldBack = [];
    private readonly Dictionary<OrderbookId, SplitByHand> _splitsByHand = [];
    private readonly HashSet<OrderbookId> _manual = [];

    private PaperBook(string? directory, string costs, decimal startingCash, IQuoteSource? marks, TimeProvider time, IFxRates? fx)
    {
        _directory = directory;
        Costs = costs;
        StartingCash = startingCash;
        _cash = startingCash;
        _marks = marks;
        _time = time;
        _fx = fx ?? FxTable.SekOnly;
    }

    /// <summary>Gets the rates foreign positions are valued at (ADR 0005).</summary>
    public IFxRates Fx => _fx;

    public AccountId Account { get; } = new(PaperConfig.AccountId);

    /// <summary>Gets the courtage class the book was opened with.</summary>
    public string Costs { get; }

    public decimal StartingCash { get; }

    public decimal Cash
    {
        get
        {
            lock (_lock)
            {
                return _cash;
            }
        }
    }

    public decimal FeesPaid
    {
        get
        {
            lock (_lock)
            {
                return _fees;
            }
        }
    }

    public decimal RealizedPnl
    {
        get
        {
            lock (_lock)
            {
                return _realized;
            }
        }
    }

    /// <summary>Gets cash set aside for working paper buys (value at the limit plus their courtage).</summary>
    public decimal Reserved
    {
        get
        {
            lock (_lock)
            {
                return _reserved.Values.Sum();
            }
        }
    }

    public IReadOnlyList<PaperPosition> Positions
    {
        get
        {
            lock (_lock)
            {
                return [.. _positions.Values.OrderBy(p => p.Ticker, StringComparer.Ordinal)];
            }
        }
    }

    public string? BookPath => _directory is null ? null : Path.Combine(_directory, FileName);

    /// <summary>Gets the last day whose dividends and splits were applied (plan 21); null before the first time.</summary>
    public DateOnly? CorporateThrough
    {
        get
        {
            lock (_lock)
            {
                return _corporateThrough;
            }
        }
    }

    /// <summary>Gets the dividends credited so far, in SEK (plan 21).</summary>
    public decimal DividendsReceived
    {
        get
        {
            lock (_lock)
            {
                return _dividends;
            }
        }
    }

    /// <summary>
    /// Gets the positions held back at the last valuation (plan 21): their live price is a split-like ratio away from
    /// their last close mark with no split applied, so they are valued at that mark and not traded until the price is
    /// back in line or the owner resolves it.
    /// </summary>
    public IReadOnlySet<OrderbookId> HeldBack
    {
        get
        {
            lock (_lock)
            {
                return new HashSet<OrderbookId>(_heldBack.Where(_positions.ContainsKey));
            }
        }
    }

    /// <summary>
    /// Gets the shares the owner trades by hand (plan 23): the strategy leaves them alone (no targets, no orders) until
    /// the owner releases them. A share stays manual after it is sold to zero, so the strategy does not buy it back.
    /// </summary>
    public IReadOnlySet<OrderbookId> ManualHolds
    {
        get
        {
            lock (_lock)
            {
                return new HashSet<OrderbookId>(_manual);
            }
        }
    }

    /// <summary>Gets the splits the owner applied by hand that Avanza's share count has not shown yet (plan 21).</summary>
    public IReadOnlyList<SplitByHand> SplitsByHand
    {
        get
        {
            lock (_lock)
            {
                return [.. _splitsByHand.Values];
            }
        }
    }

    /// <summary>A book that is never saved (tests and dry runs).</summary>
    public static PaperBook InMemory(decimal cash, string costs, IQuoteSource? marks, TimeProvider time, IFxRates? fx = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cash);
        return new PaperBook(null, costs, cash, marks, time, fx);
    }

    /// <summary>
    /// Opens <c>book.json</c> in <paramref name="directory"/>, or starts a new book from the config. An existing book
    /// wins over the config (it has history); <paramref name="notes"/> says when they differ.
    /// </summary>
    public static PaperBook OpenOrCreate(string directory, PaperConfig config, IQuoteSource? marks, TimeProvider time, out IReadOnlyList<string> notes, IFxRates? fx = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        var said = new List<string>();
        notes = said;
        if (!File.Exists(path))
        {
            var fresh = new PaperBook(directory, config.Costs, config.Cash, marks, time, fx);
            fresh.Save();
            said.Add($"new paper book: {config.Cash:N0} SEK, courtage class {config.Costs}");
            return fresh;
        }

        BookFile file;
        try
        {
            file = JsonSerializer.Deserialize<BookFile>(File.ReadAllText(path), Json) ?? throw new JsonException("empty file");
        }
        catch (JsonException ex)
        {
            throw new PaperBookException($"{path} is not a valid paper book ({ex.Message}). Move it away to start a new one.");
        }

        if (file.Format != Format)
        {
            throw new PaperBookException($"{path}: format must be {Format}.");
        }

        var book = new PaperBook(directory, file.Costs, file.StartingCash, marks, time, fx)
        {
            _cash = file.Cash,
            _fees = file.FeesPaid,
            _realized = file.RealizedPnl,
            _startOfDayDate = file.StartOfDayDate,
            _startOfDayValue = file.StartOfDayValue,
            _corporateThrough = file.CorporateThrough,
            _dividends = file.DividendsReceived,
        };
        foreach (PositionFile p in file.Positions)
        {
            var id = new OrderbookId(p.OrderbookId);
            book._positions[id] = new PaperPosition(id, p.Ticker, p.Quantity, p.CostBasis, p.LastFillPrice, p.Currency ?? "SEK", p.LastMark);
        }

        foreach (string manual in file.ManualHolds ?? [])
        {
            book._manual.Add(new OrderbookId(manual));
        }

        foreach (SplitFile s in file.SplitsByHand ?? [])
        {
            var id = new OrderbookId(s.OrderbookId);
            book._splitsByHand[id] = new SplitByHand(id, s.Ratio, s.Day);
        }

        if (file.Costs != config.Costs || file.StartingCash != config.Cash)
        {
            said.Add($"the existing book ({file.StartingCash:N0} SEK, {file.Costs}) differs from config/paper.json ({config.Cash:N0} SEK, {config.Costs}); the book is kept. Move {path} away to start over.");
        }

        return book;
    }

    /// <summary>
    /// The positions in <c>book.json</c> of <paramref name="directory"/>, read without opening the book (plan 21: whether a
    /// share taken off the list is still held). Empty without a book.
    /// </summary>
    /// <summary>Plan 23: the manual shares of the book in <paramref name="directory"/>, read without opening it; empty without a book.</summary>
    public static IReadOnlySet<OrderbookId> ManualIn(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return new HashSet<OrderbookId>();
        }

        try
        {
            BookFile file = JsonSerializer.Deserialize<BookFile>(File.ReadAllText(path), Json) ?? throw new JsonException("empty file");
            return new HashSet<OrderbookId>((file.ManualHolds ?? []).Select(id => new OrderbookId(id)));
        }
        catch (JsonException ex)
        {
            throw new PaperBookException($"{path} is not a valid paper book ({ex.Message}).");
        }
    }

    public static IReadOnlyDictionary<OrderbookId, long> HeldIn(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return new Dictionary<OrderbookId, long>();
        }

        try
        {
            BookFile file = JsonSerializer.Deserialize<BookFile>(File.ReadAllText(path), Json) ?? throw new JsonException("empty file");
            return file.Positions.Where(p => p.Quantity > 0).ToDictionary(p => new OrderbookId(p.OrderbookId), p => p.Quantity);
        }
        catch (JsonException ex)
        {
            throw new PaperBookException($"{path} is not a valid paper book ({ex.Message}).");
        }
    }

    public long Position(OrderbookId id)
    {
        lock (_lock)
        {
            return _positions.TryGetValue(id, out PaperPosition? p) ? p.Quantity : 0;
        }
    }

    public Task<AccountSnapshot> GetAsync(CancellationToken ct) => Task.FromResult(Snapshot());

    /// <summary>
    /// Cash, positions and values now. Positions are marked at the last trade, else the mid, else the last close mark,
    /// else the last paper fill (plan 21: a split-like move from the last close mark is held back, see <see cref="HeldBack"/>).
    /// The first snapshot of a Stockholm day fixes that day's start value (R19).
    /// </summary>
    public AccountSnapshot Snapshot()
    {
        DateOnly today = OrderGateway.StockholmDate(_time.GetUtcNow());
        lock (_lock)
        {
            var quantities = new Dictionary<OrderbookId, long>();
            var values = new Dictionary<OrderbookId, decimal>();
            foreach (PaperPosition p in _positions.Values)
            {
                quantities[p.OrderbookId] = p.Quantity;
                values[p.OrderbookId] = ValueSek(p);
            }

            decimal value = _cash + values.Values.Sum();
            if (_startOfDayDate != today)
            {
                _startOfDayDate = today;
                _startOfDayValue = value;
                Save();
            }

            return new AccountSnapshot(Account, value, _cash - _reserved.Values.Sum(), quantities, values, _startOfDayValue);
        }
    }

    internal void Reserve(Guid clientOrderId, decimal amount)
    {
        lock (_lock)
        {
            if (amount <= 0)
            {
                _reserved.Remove(clientOrderId);
            }
            else
            {
                _reserved[clientOrderId] = amount;
            }
        }
    }

    internal void Release(Guid clientOrderId)
    {
        lock (_lock)
        {
            _reserved.Remove(clientOrderId);
        }
    }

    /// <summary>
    /// Books a fill: cash, position, cost basis, realised P&amp;L and fees. Persisted before it returns. The price is in the
    /// share's <paramref name="currency"/>; courtage and FX fee are SEK; the value is booked in SEK at <paramref name="sekPerUnit"/>.
    /// </summary>
    internal void ApplyFill(
        Guid clientOrderId, OrderbookId id, string ticker, OrderSide side, long quantity, decimal price, decimal courtage, decimal fxFee, DateTimeOffset atUtc,
        string currency = "SEK", decimal sekPerUnit = 1m)
    {
        if (quantity <= 0 || price <= 0 || courtage < 0 || fxFee < 0 || sekPerUnit <= 0)
        {
            throw new PaperBookException($"fill {quantity} @ {price} {currency} at {sekPerUnit} SEK (fees {courtage} + {fxFee}) is not valid");
        }

        decimal fees = courtage + fxFee;
        decimal value = sekPerUnit == 1m ? quantity * price : decimal.Round(quantity * price * sekPerUnit, 2, MidpointRounding.AwayFromZero);
        lock (_lock)
        {
            PaperPosition? held = _positions.GetValueOrDefault(id);
            if (side == OrderSide.Buy)
            {
                if (_cash - value - fees < 0)
                {
                    throw new PaperBookException($"a {value + fees:N2} SEK buy of {ticker} would take paper cash below zero ({_cash:N2})");
                }

                _cash -= value + fees;
                _positions[id] = new PaperPosition(id, ticker, (held?.Quantity ?? 0) + quantity, (held?.CostBasis ?? 0) + value + fees, price, currency, held?.LastMark);
            }
            else
            {
                if (held is null || held.Quantity < quantity)
                {
                    throw new PaperBookException($"a sell of {quantity} {ticker} is larger than the paper position ({held?.Quantity ?? 0})");
                }

                decimal costOut = held.CostBasis * quantity / held.Quantity;
                _cash += value - fees;
                _realized += value - fees - costOut;
                long left = held.Quantity - quantity;
                if (left == 0)
                {
                    _positions.Remove(id);
                }
                else
                {
                    _positions[id] = held with { Quantity = left, CostBasis = held.CostBasis - costOut, LastFillPrice = price };
                }
            }

            _fees += fees;
            Save();
            AppendFill(new FillLine(atUtc, clientOrderId, id.Value, ticker, side.ToString(), quantity, price, courtage, fxFee, _cash,
                Markets.IsForeign(currency) ? currency : null, Markets.IsForeign(currency) ? sekPerUnit : null));
        }
    }

    /// <summary>
    /// Plan 21: credits a dividend on a held position (quantity × <paramref name="perShare"/>, in SEK at
    /// <paramref name="sekPerUnit"/>), gross, on the ex-date. Returns the SEK credited; 0 when the share is not held.
    /// </summary>
    internal decimal CreditDividend(OrderbookId id, decimal perShare, decimal sekPerUnit)
    {
        if (perShare < 0 || sekPerUnit <= 0)
        {
            throw new PaperBookException($"a dividend of {perShare} at {sekPerUnit} SEK is not valid");
        }

        lock (_lock)
        {
            if (!_positions.TryGetValue(id, out PaperPosition? p))
            {
                return 0m;
            }

            decimal cash = decimal.Round(p.Quantity * perShare * sekPerUnit, 2, MidpointRounding.AwayFromZero);
            _cash += cash;
            _dividends += cash;
            Save();
            return cash;
        }
    }

    /// <summary>
    /// Plan 21: a split of <paramref name="ratio"/> new shares per old (0.5 for a 1:2 reverse split). The quantity is
    /// multiplied (a fraction a reverse split leaves is paid out in cash at the last price), prices are divided, the cost
    /// is unchanged. Returns the quantities before and after and the cash for the fraction. A split
    /// <paramref name="byHand"/> is remembered until Avanza's share count shows it (<see cref="TakeSplitByHand"/>), so
    /// it is not applied twice.
    /// </summary>
    internal (long Before, long After, decimal CashForFraction) ApplySplit(OrderbookId id, decimal ratio, bool byHand = false)
    {
        if (ratio <= 0 || ratio == 1)
        {
            throw new PaperBookException($"a split ratio of {ratio} is not valid");
        }

        lock (_lock)
        {
            if (!_positions.TryGetValue(id, out PaperPosition? p))
            {
                return (0, 0, 0m);
            }

            // A 1:k reverse split arrives as 1/k, which a decimal does not hold exactly: prices are multiplied by k instead.
            bool reverse = ratio < 1;
            decimal k = reverse ? decimal.Round(1 / ratio, 6) : ratio;
            decimal Price(decimal old) => reverse ? old * k : old / k;
            decimal exact = reverse ? p.Quantity / k : p.Quantity * k;
            long after = (long)decimal.Floor(exact);
            decimal newPrice = Price(p.LastMark ?? p.LastFillPrice);
            decimal cash = decimal.Round((exact - after) * newPrice * (_fx.SekPerUnit(p.Currency) ?? 1m), 2, MidpointRounding.AwayFromZero);
            _cash += cash;
            if (after == 0)
            {
                _positions.Remove(id);
            }
            else
            {
                _positions[id] = p with
                {
                    Quantity = after,
                    LastFillPrice = Price(p.LastFillPrice),
                    LastMark = p.LastMark is { } m ? Price(m) : null,
                };
            }

            _heldBack.Remove(id);
            if (byHand)
            {
                _splitsByHand[id] = new SplitByHand(id, ratio, OrderGateway.StockholmDate(_time.GetUtcNow()));
            }

            Save();
            return (p.Quantity, after, cash);
        }
    }

    /// <summary>Plan 21: the split the owner applied by hand to <paramref name="id"/> and Avanza's count had not shown yet, now forgotten; null if none.</summary>
    internal SplitByHand? TakeSplitByHand(OrderbookId id)
    {
        lock (_lock)
        {
            if (!_splitsByHand.Remove(id, out SplitByHand? s))
            {
                return null;
            }

            Save();
            return s;
        }
    }

    /// <summary>Plan 21: the owner says a held-back share's move is real (no split): its last close mark is dropped.</summary>
    internal bool AcceptPrice(OrderbookId id)
    {
        lock (_lock)
        {
            if (!_positions.TryGetValue(id, out PaperPosition? p) || p.LastMark is null)
            {
                return false;
            }

            _positions[id] = p with { LastMark = null };
            _heldBack.Remove(id);
            Save();
            return true;
        }
    }

    /// <summary>Plan 23: the owner trades <paramref name="id"/> by hand from now on; true when it was not manual before.</summary>
    internal bool HoldManually(OrderbookId id)
    {
        lock (_lock)
        {
            if (!_manual.Add(id))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    /// <summary>Plan 23: gives <paramref name="id"/> back to the strategy; false when it was not manual.</summary>
    internal bool ReleaseManual(OrderbookId id)
    {
        lock (_lock)
        {
            if (!_manual.Remove(id))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    /// <summary>Plan 21: the day of the last dividends and splits applied.</summary>
    internal void SetCorporateThrough(DateOnly day)
    {
        lock (_lock)
        {
            _corporateThrough = day;
            Save();
        }
    }

    /// <summary>
    /// Plan 21: at the close, each position's live price becomes its last close mark, which the next session's split guard
    /// compares with. A position whose price is still a split-like ratio from its mark keeps the old mark, so it stays
    /// held back until the owner resolves it.
    /// </summary>
    public void RecordCloseMarks()
    {
        lock (_lock)
        {
            foreach (PaperPosition p in _positions.Values.ToList())
            {
                if (LivePrice(p) is { } price && !IsSplitLike(p, price))
                {
                    _positions[p.OrderbookId] = p with { LastMark = price };
                }
            }

            Save();
        }
    }

    /// <summary>
    /// The price a position is valued at: the last trade, else the mid, else the last close mark, else the last paper
    /// fill. Plan 21: a live price a
    /// split-like ratio away from the last close mark holds the position back, valued at that mark (the daily loss stop
    /// does not fire on a split nobody has confirmed).
    /// </summary>
    private decimal Mark(PaperPosition p)
    {
        decimal? live = LivePrice(p);
        if (live is { } price && IsSplitLike(p, price))
        {
            _heldBack.Add(p.OrderbookId);
            return p.LastMark!.Value;
        }

        _heldBack.Remove(p.OrderbookId);
        return live ?? p.LastMark ?? p.LastFillPrice;
    }

    private static bool IsSplitLike(PaperPosition p, decimal price) => p.LastMark is { } last && CorporateActions.SplitLikeMove(last, price) is not null;

    private decimal? LivePrice(PaperPosition p)
    {
        Quote? q = _marks?.Latest(p.OrderbookId);
        return q?.Last ?? (q is { Bid: { } bid, Ask: { } ask } ? (bid + ask) / 2 : null);
    }

    /// <summary>A position's value in SEK: at the mark and the day's rate, or at cost when its currency has no rate.</summary>
    private decimal ValueSek(PaperPosition p) =>
        _fx.SekPerUnit(p.Currency) is { } rate ? p.Quantity * Mark(p) * rate : p.CostBasis;

    private void Save()
    {
        if (_directory is null)
        {
            return;
        }

        var file = new BookFile(
            Format,
            PaperConfig.AccountId,
            Costs,
            StartingCash,
            _cash,
            _fees,
            _realized,
            _startOfDayDate,
            _startOfDayValue,
            [.. _positions.Values.OrderBy(p => p.Ticker, StringComparer.Ordinal).Select(p => new PositionFile(p.OrderbookId.Value, p.Ticker, p.Quantity, p.CostBasis, p.LastFillPrice,
                Markets.IsForeign(p.Currency) ? p.Currency : null, p.LastMark))],
            _time.GetUtcNow(),
            _corporateThrough,
            _dividends,
            _splitsByHand.Count == 0 ? null : [.. _splitsByHand.Values.OrderBy(x => x.OrderbookId.Value, StringComparer.Ordinal).Select(x => new SplitFile(x.OrderbookId.Value, x.Ratio, x.Day))],
            _manual.Count == 0 ? null : [.. _manual.Select(m => m.Value).Order(StringComparer.Ordinal)]);
        string path = Path.Combine(_directory, FileName);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
        File.Move(temp, path, overwrite: true);
    }

    private void AppendFill(FillLine line)
    {
        if (_directory is not null)
        {
            File.AppendAllText(Path.Combine(_directory, FillsFileName), JsonSerializer.Serialize(line, JsonLine) + "\n");
        }
    }

    private sealed record BookFile(
        string Format,
        string Account,
        string Costs,
        decimal StartingCash,
        decimal Cash,
        decimal FeesPaid,
        decimal RealizedPnl,
        DateOnly? StartOfDayDate,
        decimal StartOfDayValue,
        IReadOnlyList<PositionFile> Positions,
        DateTimeOffset SavedUtc,
        DateOnly? CorporateThrough = null,
        decimal DividendsReceived = 0m,
        IReadOnlyList<SplitFile>? SplitsByHand = null,
        IReadOnlyList<string>? ManualHolds = null);

    private sealed record SplitFile(string OrderbookId, decimal Ratio, DateOnly Day);

    /// <summary>A position; <c>currency</c> is written for a foreign share only, so a Swedish book reads as before.</summary>
    private sealed record PositionFile(string OrderbookId, string Ticker, long Quantity, decimal CostBasis, decimal LastFillPrice, string? Currency = null, decimal? LastMark = null);

    private sealed record FillLine(
        DateTimeOffset AtUtc, Guid ClientOrderId, string OrderbookId, string Ticker, string Side, long Quantity, decimal Price, decimal Courtage, decimal FxFee, decimal CashAfter,
        string? Currency = null, decimal? SekPerUnit = null);
}
