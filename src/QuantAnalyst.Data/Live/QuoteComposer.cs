using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Data.Live;

public sealed class QuoteComposerOptions
{
    /// <summary>Market-data poll interval per instrument (ADR 0002 §3: 5 s for active instruments, 60 s for the rest).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A quote whose newest depth or poll update is older than this is stale (ADR 0002 §3: 10 s).</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the stale transition is checked when nothing arrives (the flag lags the threshold by at most this).</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Gets a value indicating whether the pushed order depth is used (default). False: polls only, and a quote is stale
    /// only when no poll arrived within <see cref="StaleAfter"/>. Paper runs so since 2026-09-30 (owner's decision:
    /// Avanza refuses the order-depth stream with HTTP 429); Confirm and Auto keep requiring the stream.
    /// </summary>
    public bool DepthStream { get; init; } = true;

    public void Validate()
    {
        if (PollInterval < TimeSpan.FromSeconds(1) || StaleAfter <= TimeSpan.Zero || CheckInterval < TimeSpan.FromMilliseconds(50) || CheckInterval > StaleAfter)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval), "Poll ≥ 1 s; stale threshold and check interval positive, check ≤ threshold.");
        }
    }
}

/// <summary>
/// Composes a live <see cref="Quote"/> for one orderbook (ADR 0002 §3) from the pushed order depth and a market-data
/// poll, and publishes it through <see cref="Quotes"/>:
/// <list type="bullet">
/// <item>bid/ask/depth from the <b>newer</b> of the last depth event and the last poll; last trade and volume from the poll</item>
/// <item><b>stale</b> ⇔ no data yet, or the depth stream is not connected, or <c>now − max(depthAt, pollAt) &gt; StaleAfter</c>
/// (receipt times on our clock, not Avanza's <c>updated</c>); with <see cref="QuoteComposerOptions.DepthStream"/> off, polls
/// alone: stale ⇔ no poll within <c>StaleAfter</c></item>
/// <item>a quote is published on every depth event, stream state change and poll, and on every fresh⇄stale transition
/// (checked every <see cref="QuoteComposerOptions.CheckInterval"/>, 250 ms by default)</item>
/// <item>a failed poll (<see cref="BrokerUnavailableException"/>) is logged and the quote goes stale on its own; session
/// expiry, schema drift or a gone endpoint stops the composer and is rethrown (halt)</item>
/// </list>
/// </summary>
public sealed class QuoteComposer
{
    private readonly IBrokerGateway _gateway;
    private readonly QuoteComposerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private OrderDepthUpdate? _depth;
    private MarketSnapshot? _poll;
    private DateTimeOffset? _pollAt;
    private StreamState? _streamState;
    private string? _streamReason;
    private bool? _lastPublishedStale;
    private Quote? _current;

    public QuoteComposer(IBrokerGateway gateway, OrderbookId orderbookId, QuoteComposerOptions options, TimeProvider time, ILogger logger)
    {
        options.Validate();
        _gateway = gateway;
        OrderbookId = orderbookId;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public OrderbookId OrderbookId { get; }

    public Broadcaster<Quote> Quotes { get; } = new();

    /// <summary>The latest published quote, or null before the first one.</summary>
    public Quote? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Runs until <paramref name="ct"/> is cancelled or an input fails terminally (then rethrows).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task[] loops = _options.DepthStream
            ? [StreamLoopAsync(stop.Token), PollLoopAsync(stop.Token), StaleLoopAsync(stop.Token)]
            : [PollLoopAsync(stop.Token), StaleLoopAsync(stop.Token)];
        Exception? failure = null;
        try
        {
            Task first = await Task.WhenAny(loops).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
            {
                failure = new InvalidOperationException("A quote input ended unexpectedly.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(loops).ConfigureAwait(false);
            }
            catch (Exception) when (failure is not null || ct.IsCancellationRequested)
            {
                // The first failure (or the cancellation) is what matters; the others are its consequences.
            }

            Quotes.Complete(failure);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>Composes the quote as of now (used by the loops and by tests).</summary>
    public Quote Compose()
    {
        lock (_gate)
        {
            return ComposeLocked(_time.GetUtcNow());
        }
    }

    private async Task StreamLoopAsync(CancellationToken ct)
    {
        await foreach (MarketStreamEvent e in _gateway.StreamOrderDepthAsync(OrderbookId, ct).ConfigureAwait(false))
        {
            bool publish;
            lock (_gate)
            {
                switch (e)
                {
                    case DepthEvent d:
                        _depth = d.Depth;
                        publish = true;
                        break;
                    case StreamStateChanged s:
                        _streamState = s.State;
                        _streamReason = s.Reason;
                        publish = true;
                        break;
                    default:
                        publish = false; // heartbeats keep the connection alive but are not market data
                        break;
                }
            }

            if (publish)
            {
                Publish(force: true);
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PollInterval, _time);
        do
        {
            try
            {
                MarketSnapshot snapshot = await _gateway.GetMarketSnapshotAsync(OrderbookId, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    _poll = snapshot;
                    _pollAt = _time.GetUtcNow();
                }

                Publish(force: true);
            }
            catch (BrokerUnavailableException ex)
            {
                QuoteLog.PollFailed(_logger, OrderbookId.Value, ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task StaleLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.CheckInterval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            Publish(force: false);
        }
    }

    /// <summary>Publishes the current composition; unless forced, only when the stale flag changed.</summary>
    private void Publish(bool force)
    {
        Quote quote;
        lock (_gate)
        {
            quote = ComposeLocked(_time.GetUtcNow());
            if (!force && _lastPublishedStale == quote.IsStale)
            {
                return;
            }

            _lastPublishedStale = quote.IsStale;
            _current = quote;
        }

        Quotes.Publish(quote);
    }

    private Quote ComposeLocked(DateTimeOffset now)
    {
        DateTimeOffset? depthAt = _depth?.ReceivedUtc;
        bool streamNewer = depthAt is not null && (_pollAt is null || depthAt >= _pollAt);
        IReadOnlyList<DepthLevel> levels = streamNewer ? _depth!.Levels : _poll?.Depth ?? [];
        QuoteSource source = streamNewer ? QuoteSource.Stream : _poll is not null ? QuoteSource.Poll : QuoteSource.None;

        decimal? bid = levels.Count > 0 ? levels[0].BidPrice : null;
        decimal? ask = levels.Count > 0 ? levels[0].AskPrice : null;
        decimal bidVolume = levels.Count > 0 ? levels[0].BidVolume : 0m;
        decimal askVolume = levels.Count > 0 ? levels[0].AskVolume : 0m;
        if (!streamNewer && _poll is not null && levels.Count == 0)
        {
            bid = _poll.Bid;
            ask = _poll.Ask;
        }

        DateTimeOffset? asOf = Max(depthAt, _pollAt);
        string? staleReason = StaleReason(now, asOf);
        return new Quote(
            OrderbookId,
            bid,
            bidVolume,
            ask,
            askVolume,
            _poll?.Last,
            _poll?.TimeOfLastUtc,
            _poll?.TotalVolumeTraded,
            levels,
            source,
            depthAt,
            _pollAt,
            asOf,
            now,
            staleReason is not null,
            staleReason,
            _poll?.High,
            _poll?.Low);
    }

    private string? StaleReason(DateTimeOffset now, DateTimeOffset? asOf)
    {
        if (asOf is null)
        {
            return "no data yet";
        }

        if (!_options.DepthStream)
        {
            TimeSpan sincePoll = now - asOf.Value;
            return sincePoll > _options.StaleAfter
                ? string.Create(CultureInfo.InvariantCulture, $"no poll update for {sincePoll.TotalSeconds:0.0} s")
                : null;
        }

        if (_streamState != StreamState.Connected)
        {
            string state = _streamState switch
            {
                StreamState.Reconnecting => "reconnecting",
                StreamState.Connecting => "connecting",
                _ => "not started",
            };
            return _streamReason is null ? $"depth stream {state}" : $"depth stream {state} ({_streamReason})";
        }

        TimeSpan age = now - asOf.Value;
        return age > _options.StaleAfter
            ? string.Create(CultureInfo.InvariantCulture, $"no depth or poll update for {age.TotalSeconds:0.0} s")
            : null;
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}

internal static partial class QuoteLog
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "quote {OrderbookId}: poll failed ({Error}); the quote goes stale if this persists")]
    public static partial void PollFailed(ILogger logger, string orderbookId, string error);
}
