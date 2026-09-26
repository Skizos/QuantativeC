namespace QuantAnalyst.Core.Market;

/// <summary>
/// A full order-depth snapshot pushed by the broker's stream (Avanza <c>ORDER_DEPTH</c>). Levels are best first; an
/// empty side has a null price and zero volume. <see cref="ReceivedUtc"/> is <em>our</em> receipt time: the stream
/// carries no timestamp.
/// </summary>
public sealed record OrderDepthUpdate(
    OrderbookId OrderbookId,
    IReadOnlyList<DepthLevel> Levels,
    int? MarketMakerLevelBid,
    int? MarketMakerLevelAsk,
    DateTimeOffset ReceivedUtc);

public enum StreamState
{
    /// <summary>First connection attempt in progress.</summary>
    Connecting,

    /// <summary>The server answered 200 with an event stream.</summary>
    Connected,

    /// <summary>The connection dropped; waiting for the backoff delay before reconnecting.</summary>
    Reconnecting,
}

/// <summary>
/// Something that happened on a market-data stream. Terminal failures (session expired, schema drift, endpoint gone)
/// are not events: the enumeration throws them.
/// </summary>
public abstract record MarketStreamEvent(DateTimeOffset ReceivedUtc);

public sealed record DepthEvent(OrderDepthUpdate Depth) : MarketStreamEvent(Depth.ReceivedUtc);

/// <summary>A keep-alive from the server. It proves the connection is alive but carries no market data.</summary>
public sealed record StreamHeartbeat(DateTimeOffset ReceivedUtc) : MarketStreamEvent(ReceivedUtc);

public sealed record StreamStateChanged(StreamState State, string? Reason, DateTimeOffset ReceivedUtc) : MarketStreamEvent(ReceivedUtc);
