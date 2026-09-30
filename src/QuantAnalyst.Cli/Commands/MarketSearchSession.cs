using System.Threading.Channels;
using QuantAnalyst.Avanza;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Cli.Commands;

/// <summary>A share added from the search: its allowlist entry and what was imported.</summary>
internal sealed record AddedShare(UniverseEntry Entry, InstrumentImportResult Import);

/// <summary>
/// The Windows app's share search (docs/plans/15-share-search.md): <b>one</b> Avanza login, then any number of adds,
/// removes and (when Avanza wants a login to search, see <see cref="SearchPublicAsync"/>) searches, one at a time in the order asked, until <see cref="Close"/>, <see cref="IdleTimeout"/> without a request, or
/// a failure. A request that fails on its own (no such ticker, not in kronor, the list full) fails alone; a session-level
/// failure (the login, an expired session, a stop) ends the session and every waiting request fails with it. A new
/// search after that is a new session with its own login: never a login loop. Read-only at Avanza (search, orderbook,
/// price chart); locally it writes the price store and <c>config/universe.json</c>, like <c>qa history import</c> and
/// <c>qa universe add</c>.
/// </summary>
internal sealed class MarketSearchSession
{
    private readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private volatile bool _ended;
    private IFxRateSource? _fx;

    /// <summary>Gets how long the login is kept without a request.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets a value indicating whether the session no longer takes requests.</summary>
    public bool HasEnded => _ended;

    /// <summary>Stocks matching <paramref name="query"/> (name or ticker), at most <paramref name="maxHits"/>.</summary>
    public Task<IReadOnlyList<InstrumentSearchHit>> SearchAsync(string query, int maxHits = 20) =>
        Enqueue((connection, ct) => connection.Gateway.SearchStocksAsync(query, maxHits, ct));

    /// <summary>
    /// The same search <b>without a login</b> (no session needed, no BankID). Throws <see cref="SessionExpiredException"/>
    /// when Avanza wants a login for it after all; the app then searches in a session instead.
    /// </summary>
    public static Task<IReadOnlyList<InstrumentSearchHit>> SearchPublicAsync(AvanzaCliServices services, string stateDirectory, string query, int maxHits = 20) =>
        AvanzaCommands.PublicQueryAsync(services, stateDirectory, (connection, ct) => connection.Gateway.SearchStocksAsync(query, maxHits, ct));

    /// <summary>
    /// Imports <paramref name="id"/>'s daily prices for <see cref="InstrumentImport.AppYears"/> years up to
    /// <paramref name="today"/> and puts it on the allowlist. The allowlist rules are checked first, so a share that
    /// can't be added costs no import.
    /// </summary>
    public Task<AddedShare> AddAsync(OrderbookId id, string storePath, string configDir, DateOnly today) =>
        Enqueue(async (connection, ct) =>
        {
            IBrokerGateway gateway = connection.Gateway;
            InstrumentTradingParams p = await gateway.GetTradingParamsAsync(id, ct).ConfigureAwait(false);
            string ticker = p.TickerSymbol ?? throw new ArgumentException($"{p.Name} has no ticker at Avanza, so it can't be added.");
            // Plan 22: a share outside XSTO is measured first (one public chart call), so a refused one costs no import.
            InstrumentRecord record;
            TradingModelEvidence? evidence;
            using (HistoryStore? stored = File.Exists(storePath) ? HistoryStore.Open(storePath) : null) // a quiet week keeps what the store knows
            {
                (record, evidence) = await InstrumentImport.RecordAsync(gateway, p, stored, ct).ConfigureAwait(false);
            }

            Allowlist.Check(Universe.Load(Path.Combine(configDir, Universe.FileName)), record, evidence);
            InstrumentImportResult imported = await InstrumentImport.ImportAsync(
                gateway, _fx ?? throw new InvalidOperationException("The search session has not started."), p, storePath, configDir,
                today.AddYears(-InstrumentImport.AppYears), today, ct, evidence).ConfigureAwait(false);
            UniverseEntry entry = Allowlist.AddAndSave(configDir, storePath, ticker);
            return new AddedShare(entry, imported);
        });

    /// <summary>
    /// Takes <paramref name="ticker"/> off the allowlist, in turn with the adds (so a full list can make room without a
    /// second login). Local only: nothing is asked of Avanza.
    /// </summary>
    /// <returns>What happened, e.g. that a held share moves to the exiting list (plan 21).</returns>
    public Task<string> RemoveAsync(string ticker, string configDir, string? paperDir = null) =>
        Enqueue((_, _) =>
        {
            (UniverseEntry entry, bool exiting) = Allowlist.RemoveAndSave(configDir, ticker, paperDir);
            return Task.FromResult(Allowlist.Removed(entry, exiting));
        });

    /// <summary>
    /// Runs the session on the caller's thread: the one login, then the requests as they come. Returns 0 when closed or
    /// idle; throws what ended it otherwise (the waiting requests fail with the same).
    /// </summary>
    public async Task<int> RunAsync(AvanzaCliServices services, string stateDirectory, string login)
    {
        ArgumentNullException.ThrowIfNull(services);
        _fx = services.FxRates();
        try
        {
            return await AvanzaCommands.QueryAsync(services, stateDirectory, login, async (connection, ct) =>
            {
                while (await NextAsync(ct).ConfigureAwait(false) is { } job)
                {
                    await job.RunAsync(connection, ct).ConfigureAwait(false);
                }

                return 0;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            End(ex);
            throw;
        }
        finally
        {
            End(Ended());
        }
    }

    /// <summary>Ends the session after the requests already asked for (the <b>Done</b> button).</summary>
    public void Close() => _jobs.Writer.TryComplete();

    /// <summary>Ends the session now: it takes no more requests and the waiting ones fail with <paramref name="reason"/>.</summary>
    public void End(Exception reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        _ended = true;
        _jobs.Writer.TryComplete();
        while (_jobs.Reader.TryRead(out Job? job))
        {
            job.Fail(reason);
        }
    }

    private static InvalidOperationException Ended() => new("The search has ended; search again to start a new one.");

    private async Task<Job?> NextAsync(CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);
        try
        {
            return await _jobs.Reader.WaitToReadAsync(idle.Token).ConfigureAwait(false) && _jobs.Reader.TryRead(out Job? job) ? job : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // idle: the login is let go
        }
    }

    private Task<T> Enqueue<T>(Func<AvanzaConnection, CancellationToken, Task<T>> work)
    {
        var job = new Job<T>(work);
        if (_ended || !_jobs.Writer.TryWrite(job))
        {
            job.Fail(Ended());
        }

        return job.Done;
    }

    private abstract class Job
    {
        public abstract Task RunAsync(AvanzaConnection connection, CancellationToken ct);

        public abstract void Fail(Exception reason);
    }

    private sealed class Job<T>(Func<AvanzaConnection, CancellationToken, Task<T>> work) : Job
    {
        private readonly TaskCompletionSource<T> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Done => _done.Task;

        public override async Task RunAsync(AvanzaConnection connection, CancellationToken ct)
        {
            try
            {
                _done.TrySetResult(await work(connection, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _done.TrySetException(ex);
                if (EndsTheSession(ex))
                {
                    throw;
                }
            }
        }

        public override void Fail(Exception reason) => _done.TrySetException(reason);

        /// <summary>A stop, or a login or session problem (ADR 0002): the session ends rather than trying again.</summary>
        private static bool EndsTheSession(Exception ex) =>
            ex is OperationCanceledException or SessionExpiredException or LoginFailedException or LoginLockedException or EndpointGoneException;
    }
}
