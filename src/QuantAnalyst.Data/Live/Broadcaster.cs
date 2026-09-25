using System.Threading.Channels;

namespace QuantAnalyst.Data.Live;

/// <summary>A subscriber could not keep up with a stream that must not drop items; that subscription is ended.</summary>
public sealed class ConsumerLaggedException(int capacity)
    : Exception($"The consumer fell {capacity} items behind a stream that must not drop items; the subscription was stopped.");

/// <summary>
/// Fan-out over <see cref="Channel{T}"/> (ADR 0002 §3). Every subscriber gets its own bounded channel, so a slow
/// consumer never blocks the producer or other subscribers:
/// <list type="bullet">
/// <item><see cref="BoundedChannelFullMode.DropOldest"/> (quotes, depth): the oldest item is dropped and counted.</item>
/// <item><see cref="BoundedChannelFullMode.Wait"/> (own-order events, Phase 6): nothing may be dropped, so a full
/// channel ends that subscription with <see cref="ConsumerLaggedException"/>.</item>
/// </list>
/// <see cref="Complete"/> (with or without an error) reaches every subscriber, including later ones.
/// </summary>
public sealed class Broadcaster<T>
{
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private bool _completed;
    private Exception? _error;

    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Count;
            }
        }
    }

    public Subscription Subscribe(int capacity = 64, BoundedChannelFullMode mode = BoundedChannelFullMode.DropOldest)
    {
        if (mode is not (BoundedChannelFullMode.DropOldest or BoundedChannelFullMode.Wait))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Use DropOldest (may drop) or Wait (never drops; a lagging consumer is stopped).");
        }

        var subscription = new Subscription(this, capacity, mode);
        lock (_gate)
        {
            if (_completed)
            {
                subscription.Writer.TryComplete(_error);
            }
            else
            {
                _subscriptions.Add(subscription);
            }
        }

        return subscription;
    }

    public void Publish(T item)
    {
        Subscription[] targets;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            targets = [.. _subscriptions];
        }

        foreach (Subscription s in targets)
        {
            if (!s.Writer.TryWrite(item) && s.Mode == BoundedChannelFullMode.Wait)
            {
                s.Writer.TryComplete(new ConsumerLaggedException(s.Capacity));
                Remove(s);
            }
        }
    }

    public void Complete(Exception? error = null)
    {
        Subscription[] targets;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _error = error;
            targets = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (Subscription s in targets)
        {
            s.Writer.TryComplete(error);
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly Broadcaster<T> _owner;
        private readonly Channel<T> _channel;
        private long _dropped;

        internal Subscription(Broadcaster<T> owner, int capacity, BoundedChannelFullMode mode)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            _owner = owner;
            Capacity = capacity;
            Mode = mode;
            _channel = Channel.CreateBounded<T>(
                new BoundedChannelOptions(capacity) { FullMode = mode, SingleReader = true, SingleWriter = false },
                _ => Interlocked.Increment(ref _dropped));
        }

        public ChannelReader<T> Reader => _channel.Reader;

        public int Capacity { get; }

        public BoundedChannelFullMode Mode { get; }

        /// <summary>Items dropped because this subscriber was behind (DropOldest mode only).</summary>
        public long Dropped => Interlocked.Read(ref _dropped);

        internal ChannelWriter<T> Writer => _channel.Writer;

        public void Dispose()
        {
            _owner.Remove(this);
            _channel.Writer.TryComplete();
        }
    }
}
