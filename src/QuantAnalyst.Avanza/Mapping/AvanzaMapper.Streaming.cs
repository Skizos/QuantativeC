using QuantAnalyst.Avanza.Dto;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Avanza.Mapping;

internal static partial class AvanzaMapper
{
    /// <summary>
    /// One <c>ORDER_DEPTH</c> snapshot. The payload must be for the subscribed orderbook. A side is empty (null price,
    /// zero volume) when its price is missing, or when price and volume are both zero; a zero or negative price with
    /// volume, or a negative volume, is drift.
    /// </summary>
    public static OrderDepthUpdate ToOrderDepth(OrderbookId subscribed, OrderDepthPushDto dto, DateTimeOffset receivedUtc)
    {
        AvanzaRoute route = AvanzaRoutes.OrderDepthStream;
        if (!string.Equals(dto.OrderbookId, subscribed.Value, StringComparison.Ordinal))
        {
            throw Drift(route, OrderDepthPushDto.Version, DtoTier.A, "$.orderbookId", "the event is for another orderbook than the subscribed one");
        }

        var levels = new List<DepthLevel>(dto.Levels.Count);
        for (int i = 0; i < dto.Levels.Count; i++)
        {
            OrderDepthPushLevelDto l = dto.Levels[i];
            (decimal? bid, decimal bidVolume) = Side(l.BuyPrice, l.BuyVolume, $"$.levels[{i}].buy");
            (decimal? ask, decimal askVolume) = Side(l.SellPrice, l.SellVolume, $"$.levels[{i}].sell");
            levels.Add(new DepthLevel(bid, bidVolume, ask, askVolume));
        }

        return new OrderDepthUpdate(subscribed, levels, dto.MarketMakerLevelInBid, dto.MarketMakerLevelInAsk, receivedUtc);

        (decimal? Price, decimal Volume) Side(decimal? price, decimal? volume, string path)
        {
            decimal v = volume ?? 0m;
            if (v < 0m)
            {
                throw Drift(route, OrderDepthPushDto.Version, DtoTier.A, path + "Volume", "negative volume");
            }

            if (price is null || (price == 0m && v == 0m))
            {
                return (null, 0m);
            }

            if (price <= 0m)
            {
                throw Drift(route, OrderDepthPushDto.Version, DtoTier.A, path + "Price", "non-positive price with volume");
            }

            return (price, v);
        }
    }

    /// <summary>Chart bars plus the resolution Avanza actually used (live "day", provisional fixture "DAY").</summary>
    public static PriceHistory ToPriceHistory(PriceChartDto dto)
    {
        string wire = dto.Metadata.Resolution.ChartResolution;
        ChartResolution resolution = ParseResolution(wire)
            ?? throw Drift(AvanzaRoutes.PriceChart, PriceChartDto.Version, DtoTier.B, "$.metadata.resolution.chartResolution", "unknown chart resolution");
        return new PriceHistory(ToBars(dto), resolution, dto.PreviousClosingPrice)
        {
            AvailableResolutions = [.. dto.Metadata.Resolution.AvailableResolutions ?? []],
        };
    }

    /// <summary>"day", "DAY", "five_minutes", "FIVE_MINUTES" → the enum; anything else → null.</summary>
    internal static ChartResolution? ParseResolution(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire))
        {
            return null;
        }

        string key = wire.Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (ChartResolution r in Enum.GetValues<ChartResolution>())
        {
            if (string.Equals(r.ToString(), key, StringComparison.OrdinalIgnoreCase))
            {
                return r;
            }
        }

        return null;
    }
}
