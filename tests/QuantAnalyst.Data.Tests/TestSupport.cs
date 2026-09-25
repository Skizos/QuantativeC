using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Core.Orders;

namespace QuantAnalyst.Data.Tests;

/// <summary>An <see cref="IBrokerGateway"/> whose stream, poll and chart are driven by the test. Everything else is unsupported.</summary>
internal sealed class FakeGateway : IBrokerGateway
{
    private readonly Channel<MarketStreamEvent> _stream = Channel.CreateUnbounded<MarketStreamEvent>();

    /// <summary>Answers each poll; default: throws "not configured".</summary>
    public Func<OrderbookId, MarketSnapshot> Poll { get; set; } = _ => throw new BrokerUnavailableException("marketdata", "not configured");

    public Func<OrderbookId, ChartPeriod, ChartResolution?, PriceHistory> Chart { get; set; } = (_, _, _) => throw new NotSupportedException();

    public int Polls { get; private set; }

    public List<(ChartPeriod Period, ChartResolution? Resolution)> ChartRequests { get; } = [];

    public void Push(MarketStreamEvent e) => _stream.Writer.TryWrite(e);

    public void FailStream(Exception error) => _stream.Writer.TryComplete(error);

    public async IAsyncEnumerable<MarketStreamEvent> StreamOrderDepthAsync(OrderbookId id, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (MarketStreamEvent e in _stream.Reader.ReadAllAsync(ct))
        {
            yield return e;
        }
    }

    public Task<MarketSnapshot> GetMarketSnapshotAsync(OrderbookId id, CancellationToken ct)
    {
        Polls++;
        return Task.FromResult(Poll(id));
    }

    public Task<PriceHistory> GetPriceHistoryAsync(OrderbookId id, ChartPeriod period, ChartResolution? resolution, CancellationToken ct)
    {
        ChartRequests.Add((period, resolution));
        return Task.FromResult(Chart(id, period, resolution));
    }

    public Task<SessionHealth> GetSessionHealthAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<TradingAccount>> GetTradingAccountsAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<PortfolioSnapshot> GetPositionsAsync(AccountId? account, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<BrokerDeal>> GetDealsAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<BrokerTransaction>> GetTransactionsAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<InstrumentSearchHit>> SearchStocksAsync(string query, int maxHits, CancellationToken ct) => throw new NotSupportedException();

    public Task<InstrumentTradingParams> GetTradingParamsAsync(OrderbookId id, CancellationToken ct) => throw new NotSupportedException();

    public static MarketSnapshot Snapshot(OrderbookId id, decimal bid, decimal ask, decimal last, DateTimeOffset at) => new(
        id, bid, ask, last, null, null, null, null, null, 1000m, 0m, at, at,
        [new DepthLevel(bid, 100m, ask, 200m)], at, at);

    public static DepthEvent Depth(OrderbookId id, decimal bid, decimal ask, DateTimeOffset at) =>
        new(new OrderDepthUpdate(id, [new DepthLevel(bid, 500m, ask, 600m)], null, null, at));
}

internal sealed class NullLogger : ILogger
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines)
        {
            Lines.Add(formatter(state, exception));
        }
    }
}

internal static class Eventually
{
    /// <summary>Waits (real time, up to 5 s) for async continuations after a fake-time advance.</summary>
    public static async Task True(Func<bool> condition, string because)
    {
        for (int i = 0; i < 500; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail("Timed out waiting: " + because);
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qa-data-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
