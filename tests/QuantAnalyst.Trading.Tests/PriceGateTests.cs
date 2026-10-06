using QuantAnalyst.Trading.Scheduling;

namespace QuantAnalyst.Trading.Tests;

/// <summary>
/// A day's decision waits for live prices: a minute when some shares have one, half an hour while none has (plan 18), and
/// says so once each way, naming what is missing.
/// </summary>
public sealed class PriceGateTests
{
    private static readonly DateOnly Day = new(2026, 9, 30);
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 7, 54, 30, TimeSpan.Zero);

    private static readonly PriceCoverage NoneYet = new(0, 3, "ERIC B: no quote yet");
    private static readonly PriceCoverage TwoOfThree = new(2, 3, "FASTAT: stale: no poll update for 12 s");
    private static readonly PriceCoverage Every = new(3, 3, null);

    [Fact]
    public void ItWaits_UntilThePricesAreThere_AndStartsAgainTheNextDay()
    {
        PriceCoverage now = NoneYet;
        var said = new List<string>();
        var gate = new PriceGate(_ => now);

        Assert.False(gate.Open(Day, T0, said.Add));
        Assert.False(gate.Open(Day, T0.AddSeconds(30), said.Add));
        Assert.Equal(
            ["waiting for live prices before deciding (0 of 3 ready; ERIC B: no quote yet): at most 60 s, or 30 minutes while none has one."],
            said);

        now = Every;
        Assert.True(gate.Open(Day, T0.AddSeconds(31), said.Add));
        Assert.Single(said);

        now = TwoOfThree;
        DateOnly next = Day.AddDays(1);
        DateTimeOffset t1 = T0.AddDays(1);
        Assert.False(gate.Open(next, t1, said.Add));
        Assert.False(gate.Open(next, t1.AddSeconds(59), said.Add));
        Assert.True(gate.Open(next, t1.AddSeconds(60), said.Add));
        Assert.Equal(
            "no usable live price for 1 of 3 share(s) after 60 s (FASTAT: stale: no poll update for 12 s); deciding anyway (they are skipped today).",
            said[^1]);
        Assert.Equal(3, said.Count);
    }

    [Fact]
    public void WhileNoShareHasAPrice_ItWaitsHalfAnHour_SoAShortOutageDoesNotCostTheDay()
    {
        PriceCoverage now = NoneYet;
        var said = new List<string>();
        var gate = new PriceGate(_ => now);

        Assert.False(gate.Open(Day, T0, said.Add));
        Assert.False(gate.Open(Day, T0.AddMinutes(10), said.Add)); // well past a minute: still waiting
        Assert.Single(said);

        now = TwoOfThree; // the feed is back for two of them: the minute has long passed, so decide
        Assert.True(gate.Open(Day, T0.AddMinutes(10).AddSeconds(5), said.Add));
        Assert.StartsWith("no usable live price for 1 of 3 share(s)", said[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AfterHalfAnHourWithoutAnyPrice_ItDecidesAnyway_AndSaysWhy()
    {
        var said = new List<string>();
        var gate = new PriceGate(_ => NoneYet);

        Assert.False(gate.Open(Day, T0, said.Add));
        Assert.False(gate.Open(Day, T0.AddMinutes(30).AddSeconds(-1), said.Add));
        Assert.True(gate.Open(Day, T0.AddMinutes(30), said.Add));
        Assert.Equal(
            "no usable live price for any share after 30 minutes (ERIC B: no quote yet); deciding anyway (they are skipped today).",
            said[^1]);
        Assert.Equal(2, said.Count);
    }

    [Fact]
    public void WithoutAPriceCheck_ItNeverWaits()
    {
        var said = new List<string>();
        Assert.True(new PriceGate(null).Open(Day, T0, said.Add));
        Assert.Empty(said);
    }

    [Fact]
    public void AnEmptyDecision_NeedsNoPrice()
    {
        var said = new List<string>();
        Assert.True(new PriceGate(_ => new PriceCoverage(0, 0, null)).Open(Day, T0, said.Add));
        Assert.Empty(said);
    }
}
