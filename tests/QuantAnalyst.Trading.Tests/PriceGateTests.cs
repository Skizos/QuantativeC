using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Trading.Tests;

/// <summary>A day's decision waits for live prices, at most a minute, and says so once each way.</summary>
public sealed class PriceGateTests
{
    private static readonly DateOnly Day = new(2026, 9, 30);
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 7, 54, 30, TimeSpan.Zero);

    [Fact]
    public void ItWaits_UntilThePricesAreThere_OrAMinuteHasPassed_AndStartsAgainTheNextDay()
    {
        bool priced = false;
        var said = new List<string>();
        var gate = new PriceGate(_ => priced);

        Assert.False(gate.Open(Day, T0, said.Add));
        Assert.False(gate.Open(Day, T0.AddSeconds(30), said.Add));
        Assert.Equal(["waiting for live prices before deciding (at most 60 s)."], said);

        priced = true;
        Assert.True(gate.Open(Day, T0.AddSeconds(31), said.Add));

        priced = false;
        DateOnly next = Day.AddDays(1);
        DateTimeOffset t1 = T0.AddDays(1);
        Assert.False(gate.Open(next, t1, said.Add));
        Assert.False(gate.Open(next, t1.AddSeconds(59), said.Add));
        Assert.True(gate.Open(next, t1.AddSeconds(60), said.Add));
        Assert.Equal("no live price for every share after 60 s; deciding anyway (a share without one is skipped).", said[^1]);
        Assert.Equal(3, said.Count);
    }

    [Fact]
    public void WithoutAPriceCheck_ItNeverWaits()
    {
        var said = new List<string>();
        Assert.True(new PriceGate(null).Open(Day, T0, said.Add));
        Assert.Empty(said);
    }
}
