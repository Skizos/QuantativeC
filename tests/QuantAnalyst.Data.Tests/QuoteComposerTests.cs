using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Live;

namespace QuantAnalyst.Data.Tests;

public sealed class QuoteComposerTests
{
    private static readonly OrderbookId Eric = new("5240");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeGateway _gateway = new();
    private readonly NullLogger _log = new();

    private QuoteComposer Composer() => new(_gateway, Eric, new QuoteComposerOptions(), _time, _log);

    private StreamStateChanged State(StreamState s, string? reason = null) => new(s, reason, _time.GetUtcNow());

    /// <summary>Advances fake time in 250 ms steps (the check interval), letting the loops run between steps.</summary>
    private async Task Advance(TimeSpan by)
    {
        for (TimeSpan done = TimeSpan.Zero; done < by; done += TimeSpan.FromMilliseconds(250))
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task NoDepthOrPollUpdateFor10Seconds_SetsTheStaleFlag_AndAnUpdateClearsIt()
    {
        _gateway.Poll = id => FakeGateway.Snapshot(id, 94.96m, 94.98m, 94.96m, _time.GetUtcNow());
        QuoteComposer composer = Composer();
        using var cts = new CancellationTokenSource();
        Task run = composer.RunAsync(cts.Token);

        _gateway.Push(State(StreamState.Connecting));
        _gateway.Push(State(StreamState.Connected));
        _gateway.Push(FakeGateway.Depth(Eric, 94.96m, 94.98m, _time.GetUtcNow()));
        await Eventually.True(() => composer.Current is { IsStale: false, BidAskSource: QuoteSource.Stream }, "a fresh quote");
        Assert.Equal(1, _gateway.Polls);

        // From now on polls fail (logged, not fatal) and the book is quiet.
        _gateway.Poll = _ => throw new BrokerUnavailableException("marketdata", "HTTP 503", 503);
        await Advance(TimeSpan.FromSeconds(9.5));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(composer.Current!.IsStale);

        await Advance(TimeSpan.FromSeconds(1));
        await Eventually.True(() => composer.Current!.IsStale, "stale after 10 s without updates");
        Assert.StartsWith("no depth or poll update for 10.", composer.Current!.StaleReason, StringComparison.Ordinal);
        Assert.Contains(_log.Lines, l => l.Contains("poll failed", StringComparison.Ordinal));

        _gateway.Push(FakeGateway.Depth(Eric, 95.00m, 95.02m, _time.GetUtcNow()));
        await Eventually.True(() => composer.Current is { IsStale: false, Bid: 95.00m }, "fresh again after a depth update");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task FreshPollsKeepTheQuoteFresh_ButADisconnectedStreamIsStale()
    {
        _gateway.Poll = id => FakeGateway.Snapshot(id, 94.96m, 94.98m, 94.96m, _time.GetUtcNow());
        QuoteComposer composer = Composer();
        using var cts = new CancellationTokenSource();
        Task run = composer.RunAsync(cts.Token);
        _gateway.Push(State(StreamState.Connected));
        await Eventually.True(() => composer.Current is { IsStale: false }, "fresh from the poll alone");
        Assert.Equal(QuoteSource.Poll, composer.Current!.BidAskSource);

        await Advance(TimeSpan.FromSeconds(20)); // polls every 5 s keep it fresh
        Assert.False(composer.Current!.IsStale);
        Assert.InRange(_gateway.Polls, 4, 6);

        _gateway.Push(State(StreamState.Reconnecting, "server closed the stream"));
        await Eventually.True(() => composer.Current!.IsStale, "stale while reconnecting");
        Assert.Equal("depth stream reconnecting (server closed the stream)", composer.Current!.StaleReason);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task TheNewerSourceProvidesBidAsk_LastTradeAlwaysFromThePoll()
    {
        _gateway.Poll = id => FakeGateway.Snapshot(id, 90m, 91m, 90.5m, _time.GetUtcNow());
        QuoteComposer composer = Composer();
        using var cts = new CancellationTokenSource();
        Task run = composer.RunAsync(cts.Token);
        _gateway.Push(State(StreamState.Connected));
        await Eventually.True(() => composer.Current is { BidAskSource: QuoteSource.Poll }, "poll quote");

        _time.Advance(TimeSpan.FromMilliseconds(200));
        _gateway.Push(FakeGateway.Depth(Eric, 92m, 93m, _time.GetUtcNow()));
        await Eventually.True(() => composer.Current is { BidAskSource: QuoteSource.Stream }, "stream is newer");
        Quote q = composer.Current!;
        Assert.Equal((92m, 500m, 93m, 600m), (q.Bid, q.BidVolume, q.Ask, q.AskVolume));
        Assert.Equal(90.5m, q.Last);
        Assert.Equal(q.DepthAtUtc, q.AsOfUtc);

        await Advance(TimeSpan.FromSeconds(5)); // next poll is newer again
        await Eventually.True(() => composer.Current is { BidAskSource: QuoteSource.Poll, Bid: 90m }, "poll is newer");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task NoDataYet_IsStale()
    {
        QuoteComposer composer = Composer();
        using var cts = new CancellationTokenSource();
        Task run = composer.RunAsync(cts.Token);
        _gateway.Push(State(StreamState.Connecting));
        await Eventually.True(() => composer.Current is not null, "first quote");
        Assert.True(composer.Current!.IsStale);
        Assert.Equal("no data yet", composer.Current.StaleReason);
        Assert.Null(composer.Current.Bid);
        await cts.CancelAsync();
        await run;
    }

    [Theory]
    [InlineData("drift")]
    [InlineData("session")]
    public async Task TerminalStreamFailure_StopsTheComposer_AndReachesSubscribers(string kind)
    {
        _gateway.Poll = id => FakeGateway.Snapshot(id, 90m, 91m, 90.5m, _time.GetUtcNow());
        QuoteComposer composer = Composer();
        using Broadcaster<Quote>.Subscription sub = composer.Quotes.Subscribe();
        Task run = composer.RunAsync(TestContext.Current.CancellationToken);
        BrokerException error = kind == "drift"
            ? new SchemaDriftException("order-depth-stream", "v", DtoTier.A, ["$.levels[0].newField"], "1 unknown field(s)")
            : new SessionExpiredException("order-depth-stream", 401);
        _gateway.FailStream(error);

        Exception thrown = await Assert.ThrowsAnyAsync<BrokerException>(() => run);
        Assert.Same(error, thrown);
        Exception completion = await Assert.ThrowsAnyAsync<BrokerException>(async () =>
        {
            while (await sub.Reader.WaitToReadAsync(TestContext.Current.CancellationToken))
            {
                sub.Reader.TryRead(out _);
            }
        });
        Assert.Same(error, completion);
    }

    [Fact]
    public async Task TerminalPollFailure_StopsTheComposer()
    {
        _gateway.Poll = _ => throw new EndpointGoneException("marketdata");
        QuoteComposer composer = Composer();
        await Assert.ThrowsAsync<EndpointGoneException>(() => composer.RunAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OptionsAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuoteComposer(_gateway, Eric, new QuoteComposerOptions { PollInterval = TimeSpan.FromMilliseconds(100) }, _time, _log));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuoteComposer(_gateway, Eric, new QuoteComposerOptions { CheckInterval = TimeSpan.FromSeconds(30) }, _time, _log));
    }
}

public sealed class BroadcasterTests
{
    [Fact]
    public async Task SlowSubscriberDropsOldest_FastOneGetsEverything()
    {
        var hub = new Broadcaster<int>();
        using Broadcaster<int>.Subscription fast = hub.Subscribe(capacity: 1000);
        using Broadcaster<int>.Subscription slow = hub.Subscribe(capacity: 4);
        for (int i = 0; i < 100; i++)
        {
            hub.Publish(i);
        }

        hub.Complete();
        List<int> fastItems = [.. await Drain(fast.Reader)];
        List<int> slowItems = [.. await Drain(slow.Reader)];
        Assert.Equal(Enumerable.Range(0, 100), fastItems);
        Assert.Equal([96, 97, 98, 99], slowItems);
        Assert.Equal(96, slow.Dropped);
        Assert.Equal(0, fast.Dropped);
    }

    [Fact]
    public async Task NeverDropSubscriber_IsStoppedWhenItLags_OthersContinue()
    {
        var hub = new Broadcaster<int>();
        using Broadcaster<int>.Subscription strict = hub.Subscribe(capacity: 2, BoundedChannelFullMode.Wait);
        using Broadcaster<int>.Subscription relaxed = hub.Subscribe(capacity: 10);
        hub.Publish(1);
        hub.Publish(2);
        hub.Publish(3); // strict is full ⇒ stopped
        Assert.Equal(1, hub.SubscriberCount);
        await Assert.ThrowsAsync<ConsumerLaggedException>(async () => await Drain(strict.Reader));
        hub.Publish(4);
        hub.Complete();
        Assert.Equal([1, 2, 3, 4], await Drain(relaxed.Reader));
    }

    [Fact]
    public async Task ErrorsReachCurrentAndLateSubscribers_DisposeUnsubscribes()
    {
        var hub = new Broadcaster<int>();
        Broadcaster<int>.Subscription early = hub.Subscribe();
        Broadcaster<int>.Subscription gone = hub.Subscribe();
        gone.Dispose();
        Assert.Equal(1, hub.SubscriberCount);

        var error = new InvalidOperationException("halt");
        hub.Complete(error);
        hub.Publish(1); // ignored after completion
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(async () => await Drain(early.Reader)));
        using Broadcaster<int>.Subscription late = hub.Subscribe();
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(async () => await Drain(late.Reader)));
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.Subscribe(mode: BoundedChannelFullMode.DropWrite));
        early.Dispose();
    }

    private static async Task<List<int>> Drain(ChannelReader<int> reader)
    {
        var items = new List<int>();
        await foreach (int i in reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            items.Add(i);
        }

        return items;
    }
}
