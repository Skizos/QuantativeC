using System.Text.Json;
using System.Text.Json.Serialization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Model;

namespace QuantAnalyst.Trading.Paper;

/// <summary>A paper book operation that must not happen (a sell above the position, cash below zero): a bug upstream.</summary>
public sealed class PaperBookException(string message) : Exception(message);

public sealed record PaperPosition(OrderbookId OrderbookId, string Ticker, long Quantity, decimal CostBasis, decimal LastFillPrice);

/// <summary>
/// The paper account: cash, positions and fees, in SEK. It is the Paper source of truth for the account state (the
/// risk engine's inputs) and for reconciliation. With a directory it persists to <c>state/paper/book.json</c> after
/// every change (atomic replace) and appends each fill to <c>fills.jsonl</c>.
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
    private readonly TimeProvider _time;
    private readonly Dictionary<OrderbookId, PaperPosition> _positions = [];
    private readonly Dictionary<Guid, decimal> _reserved = [];
    private decimal _cash;
    private decimal _fees;
    private decimal _realized;
    private DateOnly? _startOfDayDate;
    private decimal _startOfDayValue;

    private PaperBook(string? directory, string costs, decimal startingCash, IQuoteSource? marks, TimeProvider time)
    {
        _directory = directory;
        Costs = costs;
        StartingCash = startingCash;
        _cash = startingCash;
        _marks = marks;
        _time = time;
    }

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

    /// <summary>A book that is never saved (tests and dry runs).</summary>
    public static PaperBook InMemory(decimal cash, string costs, IQuoteSource? marks, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cash);
        return new PaperBook(null, costs, cash, marks, time);
    }

    /// <summary>
    /// Opens <c>book.json</c> in <paramref name="directory"/>, or starts a new book from the config. An existing book
    /// wins over the config (it has history); <paramref name="notes"/> says when they differ.
    /// </summary>
    public static PaperBook OpenOrCreate(string directory, PaperConfig config, IQuoteSource? marks, TimeProvider time, out IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(config);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        var said = new List<string>();
        notes = said;
        if (!File.Exists(path))
        {
            var fresh = new PaperBook(directory, config.Costs, config.Cash, marks, time);
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

        var book = new PaperBook(directory, file.Costs, file.StartingCash, marks, time)
        {
            _cash = file.Cash,
            _fees = file.FeesPaid,
            _realized = file.RealizedPnl,
            _startOfDayDate = file.StartOfDayDate,
            _startOfDayValue = file.StartOfDayValue,
        };
        foreach (PositionFile p in file.Positions)
        {
            var id = new OrderbookId(p.OrderbookId);
            book._positions[id] = new PaperPosition(id, p.Ticker, p.Quantity, p.CostBasis, p.LastFillPrice);
        }

        if (file.Costs != config.Costs || file.StartingCash != config.Cash)
        {
            said.Add($"the existing book ({file.StartingCash:N0} SEK, {file.Costs}) differs from config/paper.json ({config.Cash:N0} SEK, {config.Costs}); the book is kept. Move {path} away to start over.");
        }

        return book;
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
    /// Cash, positions and values now. Positions are marked at the last trade, else the mid, else the last paper fill.
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
                values[p.OrderbookId] = p.Quantity * Mark(p);
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

    /// <summary>Books a fill: cash, position, cost basis, realised P&amp;L and fees. Persisted before it returns.</summary>
    internal void ApplyFill(Guid clientOrderId, OrderbookId id, string ticker, OrderSide side, long quantity, decimal price, decimal courtage, decimal fxFee, DateTimeOffset atUtc)
    {
        if (quantity <= 0 || price <= 0 || courtage < 0 || fxFee < 0)
        {
            throw new PaperBookException($"fill {quantity} @ {price} (fees {courtage} + {fxFee}) is not valid");
        }

        decimal fees = courtage + fxFee;
        decimal value = quantity * price;
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
                _positions[id] = new PaperPosition(id, ticker, (held?.Quantity ?? 0) + quantity, (held?.CostBasis ?? 0) + value + fees, price);
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
            AppendFill(new FillLine(atUtc, clientOrderId, id.Value, ticker, side.ToString(), quantity, price, courtage, fxFee, _cash));
        }
    }

    private decimal Mark(PaperPosition p)
    {
        Quote? q = _marks?.Latest(p.OrderbookId);
        return q?.Last ?? (q is { Bid: { } bid, Ask: { } ask } ? (bid + ask) / 2 : p.LastFillPrice);
    }

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
            [.. _positions.Values.OrderBy(p => p.Ticker, StringComparer.Ordinal).Select(p => new PositionFile(p.OrderbookId.Value, p.Ticker, p.Quantity, p.CostBasis, p.LastFillPrice))],
            _time.GetUtcNow());
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
        DateTimeOffset SavedUtc);

    private sealed record PositionFile(string OrderbookId, string Ticker, long Quantity, decimal CostBasis, decimal LastFillPrice);

    private sealed record FillLine(DateTimeOffset AtUtc, Guid ClientOrderId, string OrderbookId, string Ticker, string Side, long Quantity, decimal Price, decimal Courtage, decimal FxFee, decimal CashAfter);
}
