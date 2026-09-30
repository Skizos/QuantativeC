using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Data.Tests;

/// <summary>Plan 22: a share outside XSTO is continuous when it trades between Nasdaq's First North auction times.</summary>
public sealed class TradingModelCheckTests
{
    /// <summary>A 10-minute bar at <paramref name="hour"/>:<paramref name="minute"/> Stockholm time (CEST, UTC+2) on 2026-09-<paramref name="day"/>.</summary>
    private static Bar At(int day, int hour, int minute, long volume = 100) =>
        new(new DateTimeOffset(2026, 9, day, hour - 2, minute, 0, TimeSpan.Zero), 10m, 10.1m, 9.9m, 10m, volume);

    private static Bar[] Auctions(int day) => [At(day, 9, 0), At(day, 11, 0), At(day, 13, 0), At(day, 15, 0), At(day, 17, 20), At(day, 17, 30)];

    [Fact]
    public void TradesBetweenTheAuctions_OnTwoDays_AreContinuous()
    {
        TradingModelEvidence e = TradingModelCheck.Classify([.. Auctions(21), At(21, 10, 30), .. Auctions(22), .. Auctions(23), At(23, 14, 50)]);
        Assert.Equal((TradingModel.Continuous, 3, 2), (e.Model, e.DaysWithTrades, e.DaysWithContinuousTrades));
        Assert.Equal("it traded outside the auction times on 2 of 3 days last week", e.Reason);
    }

    [Fact]
    public void TradesOnlyAtTheAuctions_OnThreeDays_AreTheAuctionModel()
    {
        // A trade stamped a few minutes into an auction slot, and a bar without volume at 10:30, change nothing.
        TradingModelEvidence e = TradingModelCheck.Classify([.. Auctions(21), .. Auctions(22), At(22, 11, 4), .. Auctions(24), At(24, 10, 30, volume: 0)]);
        Assert.Equal((TradingModel.PeriodicAuction, 3, 0), (e.Model, e.DaysWithTrades, e.DaysWithContinuousTrades));
        Assert.Contains("only at the auction times", e.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, true)] // one day between the auctions: not enough
    [InlineData(2, false)] // two days of auctions only: not enough either
    public void TooFewTrades_CannotBeTold(int days, bool continuous)
    {
        Bar[] bars = [.. Enumerable.Range(21, days).SelectMany(d => continuous ? [At(d, 10, 30)] : Auctions(d))];
        TradingModelEvidence e = TradingModelCheck.Classify(bars);
        Assert.Equal(TradingModel.Unknown, e.Model);
        Assert.StartsWith("too few trades last week to tell", e.Reason, StringComparison.Ordinal);
        Assert.Equal(TradingModel.Unknown, TradingModelCheck.Classify([]).Model);
    }

    [Fact]
    public void AQuietWeek_KeepsTheStoredModel_OnlyAuctionsChangeIt()
    {
        InstrumentRecord fresh = new(new OrderbookId("1"), null, "AIRA", "Aira", "SEK", "FNSE", "STOCK", TradingModel.Unknown, 1m, "[]", new DateOnly(2026, 9, 30));
        var quiet = new TradingModelEvidence(TradingModel.Unknown, 1, 0, "too few");
        var auctions = new TradingModelEvidence(TradingModel.PeriodicAuction, 4, 0, "auctions");

        Assert.Equal(TradingModel.Continuous, TradingModelCheck.Apply(fresh, quiet, TradingModel.Continuous).TradingModel);
        Assert.Equal(TradingModel.Unknown, TradingModelCheck.Apply(fresh, quiet, null).TradingModel);
        Assert.Equal(TradingModel.PeriodicAuction, TradingModelCheck.Apply(fresh, auctions, TradingModel.Continuous).TradingModel);
    }

    [Theory]
    [InlineData("XSTO", "SEK", true)]
    [InlineData("FNSE", "SEK", false)]
    [InlineData("XNYS", "USD", true)]
    [InlineData("XTSE", "CAD", true)]
    public void OnlySharesOutsideXstoAreMeasured(string marketPlace, string currency, bool known) =>
        Assert.Equal(known, TradingModelCheck.KnownContinuous(marketPlace, currency));

    [Fact]
    public async Task Measure_AsksForLastWeeksTenMinuteBars_OnceAndPublicly()
    {
        var gateway = new FakeGateway
        {
            Chart = (_, _, resolution) => resolution == ChartResolution.TenMinutes
                ? new PriceHistory([.. Auctions(21), At(21, 10, 30), .. Auctions(22), At(22, 12, 10)], ChartResolution.TenMinutes, null)
                : throw new InvalidOperationException("only 10-minute bars are asked for"),
        };

        TradingModelEvidence e = await TradingModelCheck.MeasureAsync(gateway, new OrderbookId("1"), TestContext.Current.CancellationToken);

        Assert.Equal(TradingModel.Continuous, e.Model);
        Assert.Equal([(ChartPeriod.OneWeek, (ChartResolution?)ChartResolution.TenMinutes)], gateway.ChartRequests);

        var hourly = new FakeGateway { Chart = (_, _, _) => new PriceHistory([At(21, 10, 0)], ChartResolution.Hour, null) };
        TradingModelEvidence other = await TradingModelCheck.MeasureAsync(hourly, new OrderbookId("1"), TestContext.Current.CancellationToken);
        Assert.Equal(TradingModel.Unknown, other.Model);
        Assert.Contains("answered with Hour bars", other.Reason, StringComparison.Ordinal);
    }
}
