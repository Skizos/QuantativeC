using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Desktop.Core.Engine;

/// <summary>A share the search added: on the allowlist now, with how many daily prices were stored.</summary>
public sealed record ShareAdded(string Ticker, string Name, int NewBars, DateOnly? LastDate, string? CalendarNote);

/// <summary>
/// Where the Instruments page searches Avanza's market and adds a share (docs/plans/15-share-search.md); tests pass a
/// fake. The first request opens one login that every later one uses, until <see cref="Close"/>, a few idle minutes or
/// a problem.
/// </summary>
public interface IMarketSearch
{
    /// <summary>Gets a value indicating whether a search login is open (so the page's buttons stay on while it runs).</summary>
    bool IsOpen { get; }

    /// <summary>Stocks whose name or ticker matches <paramref name="query"/>.</summary>
    Task<IReadOnlyList<InstrumentSearchHit>> SearchAsync(string query);

    /// <summary>Imports the share's daily prices and puts it on the allowlist.</summary>
    Task<ShareAdded> AddAsync(OrderbookId id);

    /// <summary>Takes a name off the allowlist while the search is open (in turn with the adds; no login).</summary>
    Task RemoveAsync(string ticker);

    /// <summary>Lets the login go (the <b>Done</b> button).</summary>
    void Close();
}

/// <summary>
/// The real search: the CLI's <see cref="MarketSearchSession"/>, run in-process by the engine like any Avanza read (one
/// thing at a time, the BankID QR code in the window, <b>Stop</b> ends it). Read-only at Avanza; locally it writes
/// the price store and <c>config/universe.json</c>.
/// </summary>
public sealed class EngineMarketSearch(QaEngine engine, Workspace workspace, TimeProvider time, Func<string> login) : IMarketSearch
{
    /// <summary>How long the login is kept without a search: short, because nothing else can run meanwhile.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private MarketSearchSession? _session;

    public bool IsOpen => _session is { HasEnded: false };

    public Task<IReadOnlyList<InstrumentSearchHit>> SearchAsync(string query) => Ask(session => session.SearchAsync(query));

    public async Task<ShareAdded> AddAsync(OrderbookId id)
    {
        DateOnly today = DateOnly.FromDateTime(MarketTime.ToStockholm(time.GetUtcNow()).DateTime);
        AddedShare added = await Ask(session => session.AddAsync(id, workspace.Store, workspace.ConfigDir, today)).ConfigureAwait(true);
        return new ShareAdded(added.Entry.Ticker, added.Entry.Name, added.Import.Report.Bars.New, added.Import.Report.LastDate, added.Import.CalendarNote);
    }

    public Task RemoveAsync(string ticker) =>
        _session is { HasEnded: false } open
            ? open.RemoveAsync(ticker, workspace.ConfigDir)
            : throw new InvalidOperationException("No search is open.");

    public void Close() => _session?.Close();

    /// <summary>
    /// Queues the request in the open session, or opens one: the request is queued <b>before</b> the session starts, so
    /// a login that fails fails it too instead of leaving it waiting.
    /// </summary>
    private Task<T> Ask<T>(Func<MarketSearchSession, Task<T>> request)
    {
        if (_session is { HasEnded: false } open)
        {
            return request(open);
        }

        if (engine.IsBusy)
        {
            throw new InvalidOperationException($"'{engine.CurrentCommand}' is running; search when it has finished.");
        }

        var session = new MarketSearchSession { IdleTimeout = IdleTimeout };
        _session = session;
        Task<T> result = request(session);
        Task<int> run = engine.QueryAsync("Search Avanza", services => session.RunAsync(services, workspace.StateDir, login()));
        _ = run.ContinueWith(
            t => session.End(t.Exception?.GetBaseException() ?? new InvalidOperationException("The search has ended; search again to start a new one.")),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return result;
    }
}
