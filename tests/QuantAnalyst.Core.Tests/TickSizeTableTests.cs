using QuantAnalyst.Core.Instruments;

namespace QuantAnalyst.Core.Tests;

public sealed class TickSizeTableTests
{
    // Shape of an Avanza tickSizeList ("gap" style: max = next min - tick). Values are illustrative.
    internal static TickSizeTable Sample() => new(
    [
        new(0m, 0.4999m, 0.0001m),
        new(0.5m, 0.9995m, 0.0005m),
        new(1m, 4.999m, 0.001m),
        new(5m, 9.995m, 0.005m),
        new(10m, 49.99m, 0.01m),
        new(50m, 99.95m, 0.05m),
        new(100m, 499.9m, 0.1m),
        new(500m, 999.5m, 0.5m),
        new(1000m, 999999m, 1m),
    ]);

    [Theory]
    [InlineData("123.456", "123.4", "123.5", "123.5")]
    [InlineData("100", "100", "100", "100")]
    [InlineData("99.97", "99.95", "100", "99.95")]
    [InlineData("0.49995", "0.4999", "0.5", "0.4999")]
    [InlineData("49.995", "49.99", "50", "49.99")]
    [InlineData("100.05", "100", "100.1", "100")] // tie -> down
    [InlineData("7.1234", "7.12", "7.125", "7.125")]
    public void Round_MatchesHandComputedValues(string price, string down, string up, string nearest)
    {
        TickSizeTable t = Sample();
        decimal p = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(decimal.Parse(down, System.Globalization.CultureInfo.InvariantCulture), t.Round(p, TickRounding.Down));
        Assert.Equal(decimal.Parse(up, System.Globalization.CultureInfo.InvariantCulture), t.Round(p, TickRounding.Up));
        Assert.Equal(decimal.Parse(nearest, System.Globalization.CultureInfo.InvariantCulture), t.Round(p, TickRounding.Nearest));
    }

    [Fact]
    public void RoundForOrder_BuyRoundsDownSellRoundsUp()
    {
        TickSizeTable t = Sample();
        Assert.Equal(123.4m, t.RoundForOrder(123.456m, OrderSide.Buy));
        Assert.Equal(123.5m, t.RoundForOrder(123.456m, OrderSide.Sell));
    }

    [Fact]
    public void TickAt_UsesLastBandStartingAtOrBelowPrice()
    {
        TickSizeTable t = Sample();
        Assert.Equal(0.01m, t.TickAt(49.995m)); // in the gap -> lower band
        Assert.Equal(0.05m, t.TickAt(50m));
        Assert.Equal(1m, t.TickAt(1000m));
    }

    [Fact]
    public void IsOnTick_ChecksTheBandGrid()
    {
        TickSizeTable t = Sample();
        Assert.True(t.IsOnTick(123.4m));
        Assert.False(t.IsOnTick(123.45m));
        Assert.True(t.IsOnTick(50.05m));
        Assert.False(t.IsOnTick(50.01m));
        Assert.False(t.IsOnTick(0m));
        Assert.False(t.IsOnTick(1_000_000m));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000000")]
    [InlineData("0.00001")] // rounds down to zero
    public void Round_RejectsPricesOutsideTheTable(string price)
    {
        decimal p = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentOutOfRangeException>(() => Sample().Round(p, TickRounding.Down));
    }

    [Fact]
    public void Round_RandomPricesLandOnTickAndBracketTheInput()
    {
        TickSizeTable t = Sample();
        var rng = new Random(20260925);
        for (int i = 0; i < 5000; i++)
        {
            decimal p = Math.Round((decimal)(Math.Exp(rng.NextDouble() * 13.0 - 6.0)), 6);
            if (p < 0.0001m || p > 999999m)
            {
                continue;
            }

            decimal down = t.Round(p, TickRounding.Down);
            decimal up = t.Round(p, TickRounding.Up);
            Assert.True(down <= p && p <= up, $"{down} <= {p} <= {up}");
            Assert.True(t.IsOnTick(down), $"down {down} for {p}");
            Assert.True(t.IsOnTick(up), $"up {up} for {p}");
            Assert.True(up - down <= Math.Max(t.TickAt(down), t.TickAt(up)), $"gap {down}..{up} for {p}");
        }
    }

    [Fact]
    public void Constructor_RejectsInvalidTables()
    {
        Assert.Throws<ArgumentException>(() => new TickSizeTable([]));
        Assert.Throws<ArgumentException>(() => new TickSizeTable([new(0m, 10m, 0m)]));
        Assert.Throws<ArgumentException>(() => new TickSizeTable([new(0m, 10m, 0.01m), new(5m, 20m, 0.05m)]));
        Assert.Throws<ArgumentException>(() => new TickSizeTable([new(10m, 5m, 0.01m)]));
    }

    [Fact]
    public void Constructor_AcceptsSharedEdgeStyle()
    {
        var t = new TickSizeTable([new(0m, 10m, 0.01m), new(10m, 100m, 0.05m)]);
        Assert.Equal(0.05m, t.TickAt(10m));
        Assert.Equal(10.05m, t.Round(10.01m, TickRounding.Up));
    }
}
