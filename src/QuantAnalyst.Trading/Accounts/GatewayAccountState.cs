using System.Text.Json;
using System.Text.Json.Serialization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Model;

namespace QuantAnalyst.Trading.Accounts;

/// <summary>The live account can't be used for orders now: it changed (R1) or its data is unusable. Masked.</summary>
public sealed class AccountStateException(string message) : Exception(message);

/// <summary>
/// The live account state (Phase 7; the risk engine's R6–R9 and R19 inputs), read through the <b>read</b> gateway
/// for the one allowed account (<see cref="AccountAllowlist"/>):
/// <list type="bullet">
/// <item>Cash for R9 is <c>availableForPurchase</c> (Avanza already sets aside cash for working buys), never more than
/// the credit-free figure.</item>
/// <item>Positions and cash come from the positions read. Whole-share positions with an orderbook id are the
/// positions R4 may sell; fractional holdings (funds) and holdings without an orderbook count towards the value only.</item>
/// <item>Value is every holding at the composed quote (last trade, else the mid) plus cash. Holdings without a quote,
/// or not in SEK, keep the broker's value (in SEK).</item>
/// <item>The first snapshot of a Stockholm day fixes that day's start value (R19). It is kept in
/// <see cref="FileName"/> so a restart cannot reset the daily loss stop.</item>
/// </list>
/// The broker is read again when the holdings are older than the maximum age (30 s by default, the reconciliation
/// interval) or after <see cref="Invalidate"/>, and each read checks the R1 conditions again. Broker failures
/// (session expired, schema drift) propagate; a changed or unusable account throws <see cref="AccountStateException"/>.
/// </summary>
public sealed class GatewayAccountState : IAccountState
{
    public const string FileName = "live-start-of-day.json";
    private const string Format = "qa-live-start-of-day/1";
    private const string Sek = "SEK";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IBrokerGateway _gateway;
    private readonly IQuoteSource? _quotes;
    private readonly TimeProvider _time;
    private readonly string _stateDirectory;
    private readonly TimeSpan _maxHoldingsAge;
    private readonly Lock _lock = new();
    private Holdings? _holdings;
    private bool _startOfDayLoaded;
    private DateOnly? _startOfDayDate;
    private decimal _startOfDayValue;

    /// <param name="account">The allowed account (<see cref="AllowlistResult.Account"/>).</param>
    /// <param name="stateDirectory">Where <see cref="FileName"/> is kept (<c>state/</c>).</param>
    /// <param name="maxHoldingsAge">How long a broker read is reused; 30 s when null.</param>
    public GatewayAccountState(IBrokerGateway gateway, AccountId account, IQuoteSource? quotes, TimeProvider time, string stateDirectory, TimeSpan? maxHoldingsAge = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _gateway = gateway;
        Account = account;
        _quotes = quotes;
        _time = time;
        _stateDirectory = stateDirectory;
        _maxHoldingsAge = maxHoldingsAge ?? TimeSpan.FromSeconds(30);
    }

    public AccountId Account { get; }

    public string StartOfDayPath => Path.Combine(_stateDirectory, FileName);

    /// <summary>Gets when the broker was last read, or null before the first read.</summary>
    public DateTimeOffset? ReadAtUtc
    {
        get
        {
            lock (_lock)
            {
                return _holdings?.ReadAtUtc;
            }
        }
    }

    /// <summary>Makes the next <see cref="GetAsync"/> read the broker (after a fill, a reject or an order).</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _holdings = null;
        }
    }

    public async Task<AccountSnapshot> GetAsync(CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();
        Holdings? holdings;
        lock (_lock)
        {
            holdings = _holdings is { } h && now - h.ReadAtUtc < _maxHoldingsAge ? h : null;
        }

        if (holdings is null)
        {
            holdings = await ReadAsync(now, ct).ConfigureAwait(false);
            lock (_lock)
            {
                _holdings = holdings;
            }
        }

        return Snapshot(holdings, now);
    }

    private async Task<Holdings> ReadAsync(DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<TradingAccount> accounts = await _gateway.GetTradingAccountsAsync(ct).ConfigureAwait(false);
        TradingAccount account = accounts.FirstOrDefault(a => a.Id == Account)
            ?? throw new AccountStateException($"account {Account.Masked} is no longer among the trading accounts; live orders stop (R1).");
        IReadOnlyList<string> problems = AccountAllowlist.Problems(account);
        if (problems.Count > 0)
        {
            throw new AccountStateException($"account {Account.Masked} may no longer trade (R1): {string.Join(" ", problems)}");
        }

        PortfolioSnapshot portfolio = await _gateway.GetPositionsAsync(Account, ct).ConfigureAwait(false);
        var positions = new List<Holding>();
        foreach (Position p in portfolio.Positions.Where(p => p.Account == Account && p.Volume != 0))
        {
            if (p.Volume < 0)
            {
                throw new AccountStateException($"account {Account.Masked} reports a negative position in {p.InstrumentName} ({p.Volume}); an ISK can't be short.");
            }

            positions.Add(new Holding(p.OrderbookId, p.Currency, p.Volume, p.Value));
        }

        return new Holdings(
            Math.Min(account.AvailableForPurchase, account.AvailableForPurchaseWithoutCredit ?? account.AvailableForPurchase),
            Cash(account, portfolio),
            positions,
            now);
    }

    /// <summary>The account's SEK cash from the positions read, else from the trading account's balances.</summary>
    private decimal Cash(TradingAccount account, PortfolioSnapshot portfolio)
    {
        CashPosition[] cash = [.. portfolio.Cash.Where(c => c.Account == Account && c.Balance != 0)];
        if (cash.FirstOrDefault(c => !string.Equals(c.Currency, Sek, StringComparison.Ordinal)) is { } other)
        {
            throw new AccountStateException($"account {Account.Masked} holds cash in {other.Currency}; only SEK is supported in Phase 7.");
        }

        if (cash.Length > 0 || portfolio.Cash.Any(c => c.Account == Account))
        {
            return cash.Sum(c => c.Balance);
        }

        return account.CurrencyBalances.FirstOrDefault(b => string.Equals(b.Currency, Sek, StringComparison.Ordinal))?.Balance
            ?? throw new AccountStateException($"account {Account.Masked}: neither the positions read nor the trading account report a SEK cash balance.");
    }

    private AccountSnapshot Snapshot(Holdings holdings, DateTimeOffset now)
    {
        var quantities = new Dictionary<OrderbookId, long>();
        var values = new Dictionary<OrderbookId, decimal>();
        decimal value = holdings.Cash;
        foreach (Holding h in holdings.Positions)
        {
            decimal marked = Mark(h);
            value += marked;
            if (h.OrderbookId is not { } id)
            {
                continue;
            }

            values[id] = values.GetValueOrDefault(id) + marked;
            if (h.Volume == decimal.Truncate(h.Volume))
            {
                quantities[id] = quantities.GetValueOrDefault(id) + (long)h.Volume;
            }
        }

        return new AccountSnapshot(Account, value, holdings.AvailableCash, quantities, values, StartOfDay(value, now));
    }

    private decimal Mark(Holding h)
    {
        if (h.OrderbookId is not { } id || !string.Equals(h.Currency, Sek, StringComparison.Ordinal))
        {
            return h.BrokerValue;
        }

        Quote? q = _quotes?.Latest(id);
        decimal? price = q?.Last ?? (q is { Bid: { } bid, Ask: { } ask } ? (bid + ask) / 2 : null);
        return price is > 0 ? h.Volume * price.Value : h.BrokerValue;
    }

    private decimal StartOfDay(decimal value, DateTimeOffset now)
    {
        DateOnly today = OrderGateway.StockholmDate(now);
        lock (_lock)
        {
            if (!_startOfDayLoaded)
            {
                Load();
                _startOfDayLoaded = true;
            }

            if (_startOfDayDate != today)
            {
                _startOfDayDate = today;
                _startOfDayValue = value;
                Save(now);
            }

            return _startOfDayValue;
        }
    }

    private void Load()
    {
        string path = StartOfDayPath;
        if (!File.Exists(path))
        {
            return;
        }

        StartOfDayFile file;
        try
        {
            file = JsonSerializer.Deserialize<StartOfDayFile>(File.ReadAllText(path), Json) ?? throw new JsonException("empty file");
        }
        catch (JsonException ex)
        {
            throw new AccountStateException($"{path} is not a valid start-of-day record ({ex.Message}). Move it away; the next snapshot starts the day again.");
        }

        if (file.Format != Format)
        {
            throw new AccountStateException($"{path}: format must be {Format}.");
        }

        // Another account's record (the allowlist changed) is not this account's start of the day.
        if (file.Account == Account.Masked)
        {
            _startOfDayDate = file.Date;
            _startOfDayValue = file.Value;
        }
    }

    private void Save(DateTimeOffset now)
    {
        Directory.CreateDirectory(_stateDirectory);
        string path = StartOfDayPath;
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new StartOfDayFile(Format, Account.Masked, _startOfDayDate!.Value, _startOfDayValue, now), Json));
        File.Move(temp, path, overwrite: true);
    }

    private sealed record Holding(OrderbookId? OrderbookId, string Currency, decimal Volume, decimal BrokerValue);

    private sealed record Holdings(decimal AvailableCash, decimal Cash, IReadOnlyList<Holding> Positions, DateTimeOffset ReadAtUtc);

    private sealed record StartOfDayFile(string Format, string Account, DateOnly Date, decimal Value, DateTimeOffset SavedUtc);
}
