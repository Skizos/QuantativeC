using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Data.History;

/// <summary>What a share's recent bars say about how it trades (plan 22).</summary>
/// <param name="DaysWithTrades">Days with at least one traded 10-minute bar.</param>
/// <param name="DaysWithContinuousTrades">Of those, days with a trade outside the auction times.</param>
/// <param name="Reason">One sentence for the owner, e.g. "trades outside the auction times on 4 of 5 days".</param>
public sealed record TradingModelEvidence(TradingModel Model, int DaysWithTrades, int DaysWithContinuousTrades, string Reason);

/// <summary>
/// Plan 22: is a share outside Nasdaq Stockholm's main market traded continuously, or only in auctions? Nasdaq's First
/// North auction model trades a few illiquid shares in five daily auctions (09:00, 11:00, 13:00, 15:00 and the 17:30
/// close); every other share trades continuously. A 10-minute bar with volume outside those times is a continuous
/// trade. Measured on the last week's 10-minute bars of the public price chart (the route the intraday catch-up uses).
/// </summary>
public static class TradingModelCheck
{
    /// <summary>Trades outside the auction times on this many days make a share continuous.</summary>
    public const int MinContinuousDays = 2;

    /// <summary>Trades on this many days, none outside the auction times, make it an auction share.</summary>
    public const int MinAuctionDays = 3;

    /// <summary>
    /// The 10-minute slots (Stockholm time) an auction trade falls in: the open, the three intraday auctions, a half day's
    /// close (12:50) and the close (17:20, and 17:30 for an uncross stamped at the close).
    /// </summary>
    public static IReadOnlyList<TimeOnly> AuctionSlots { get; } =
        [new(9, 0), new(11, 0), new(12, 50), new(13, 0), new(15, 0), new(17, 20), new(17, 30)];

    /// <summary>Nasdaq Stockholm's main market, and the US and Canadian exchanges (ADR 0005), trade continuously: no evidence needed.</summary>
    public static bool KnownContinuous(string marketPlace, string currency) =>
        string.Equals(marketPlace, "XSTO", StringComparison.Ordinal) || currency is "USD" or "CAD";

    /// <summary>Classifies from 10-minute bars (any order; bars without volume are ignored).</summary>
    public static TradingModelEvidence Classify(IReadOnlyList<Bar> tenMinuteBars)
    {
        ArgumentNullException.ThrowIfNull(tenMinuteBars);
        var days = new Dictionary<DateOnly, bool>();
        foreach (Bar b in tenMinuteBars.Where(b => b.Volume > 0))
        {
            DateTime local = MarketTime.ToStockholm(b.TimestampUtc).DateTime;
            var day = DateOnly.FromDateTime(local);
            var slot = new TimeOnly(local.Hour, local.Minute / 10 * 10);
            bool continuous = !AuctionSlots.Contains(slot);
            days[day] = days.GetValueOrDefault(day) || continuous;
        }

        int traded = days.Count, continuousDays = days.Values.Count(c => c);
        CultureInfo c = CultureInfo.InvariantCulture;
        if (continuousDays >= MinContinuousDays)
        {
            return new TradingModelEvidence(TradingModel.Continuous, traded, continuousDays,
                string.Create(c, $"it traded outside the auction times on {continuousDays} of {traded} days last week"));
        }

        if (continuousDays == 0 && traded >= MinAuctionDays)
        {
            return new TradingModelEvidence(TradingModel.PeriodicAuction, traded, 0,
                string.Create(c, $"it traded only at the auction times (09:00, 11:00, 13:00, 15:00, the close) on all {traded} days last week: Nasdaq's First North auction model"));
        }

        return new TradingModelEvidence(TradingModel.Unknown, traded, continuousDays,
            string.Create(c, $"too few trades last week to tell ({traded} day(s) with trades, {continuousDays} outside the auction times; {MinContinuousDays} are needed)"));
    }

    /// <summary>One public chart call: the last week's 10-minute bars, classified.</summary>
    public static async Task<TradingModelEvidence> MeasureAsync(IBrokerGateway gateway, OrderbookId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        PriceHistory week = await gateway.GetPriceHistoryAsync(id, ChartPeriod.OneWeek, ChartResolution.TenMinutes, ct).ConfigureAwait(false);
        return week.Resolution == ChartResolution.TenMinutes
            ? Classify(week.Bars)
            : new TradingModelEvidence(TradingModel.Unknown, 0, 0, $"Avanza answered with {week.Resolution} bars for the last week, not 10-minute ones, so it can't be told");
    }

    /// <summary>
    /// <paramref name="fresh"/> with the measured model. An Unknown measurement (a quiet week) keeps a model already
    /// <paramref name="stored"/>: only evidence of auctions turns a continuous share into an auction one.
    /// </summary>
    public static InstrumentRecord Apply(InstrumentRecord fresh, TradingModelEvidence evidence, TradingModel? stored)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(evidence);
        TradingModel model = evidence.Model == TradingModel.Unknown && stored is { } known ? known : evidence.Model;
        return fresh with { TradingModel = model };
    }
}
