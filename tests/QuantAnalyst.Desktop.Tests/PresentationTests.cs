using QuantAnalyst.Desktop.Core.Presentation;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>Numbers and colours as the window shows them (docs/plans/11-app-redesign.md).</summary>
public sealed class PresentationTests
{
    private const string Nbsp = " ";

    [Fact]
    public void Money_ReadsSwedish_WithASpaceBetweenThousands_AndADecimalComma()
    {
        Assert.Equal($"5{Nbsp}000,00 kr", Fmt.Sek(5_000m));
        Assert.Equal($"1{Nbsp}234{Nbsp}567,89", Fmt.Amount(1_234_567.891m));
        Assert.Equal("0,00 kr", Fmt.Sek(0m));
        Assert.Equal($"{Fmt.Minus}3,00 kr", Fmt.Sek(-3m));
    }

    [Fact]
    public void Changes_CarryTheirSign_AndPercentagesTheirUnit()
    {
        Assert.Equal("+12,50 kr", Fmt.ChangeSek(12.5m));
        Assert.Equal($"{Fmt.Minus}3,00 kr", Fmt.ChangeSek(-3m));
        Assert.Equal("0,00 kr", Fmt.ChangeSek(0m));
        Assert.Equal($"+1,23{Nbsp}%", Fmt.ChangePct(0.0123m));
        Assert.Equal($"{Fmt.Minus}0,40{Nbsp}%", Fmt.ChangePct(-0.004m));
        Assert.Equal($"20,0{Nbsp}%", Fmt.Pct(0.2m));
        Assert.Equal($"▲ +1,23{Nbsp}%", Fmt.Arrow(0.0123m));
        Assert.Equal($"▼ {Fmt.Minus}0,40{Nbsp}%", Fmt.Arrow(-0.004m));
        Assert.Equal($"0,00{Nbsp}%", Fmt.Arrow(0m));
    }

    [Fact]
    public void Prices_KeepTwoToFourDecimals_AndShareCountsNone()
    {
        Assert.Equal("70,80", Fmt.Price(70.8m));
        Assert.Equal("0,1234", Fmt.Price(0.1234m));
        Assert.Equal($"1{Nbsp}020,50", Fmt.Price(1020.5m));
        Assert.Equal($"1{Nbsp}200", Fmt.Count(1200));
    }

    [Theory]
    [InlineData("ok", ToneKind.Positive)]
    [InlineData("CLEAN", ToneKind.Positive)]
    [InlineData("Filled", ToneKind.Positive)]
    [InlineData("up", ToneKind.Positive)]
    [InlineData("todo", ToneKind.Info)]
    [InlineData("Working", ToneKind.Info)]
    [InlineData("warn", ToneKind.Warning)]
    [InlineData("INCOMPLETE", ToneKind.Warning)]
    [InlineData("PartiallyFilled", ToneKind.Warning)]
    [InlineData("FAIL", ToneKind.Negative)]
    [InlineData("NOT CLEAN", ToneKind.Negative)]
    [InlineData("Rejected", ToneKind.Negative)]
    [InlineData("down", ToneKind.Negative)]
    [InlineData("Cancelled", ToneKind.Neutral)]
    [InlineData(null, ToneKind.Neutral)]
    public void EachStateWord_HasItsTone(string? word, ToneKind tone) => Assert.Equal(tone, Tone.Of(word));

    [Fact]
    public void ADirection_IsTheWordItsToneColours()
    {
        Assert.Equal(ToneKind.Positive, Tone.Of(Tone.Direction(0.5m)));
        Assert.Equal(ToneKind.Negative, Tone.Of(Tone.Direction(-0.5m)));
        Assert.Equal(ToneKind.Neutral, Tone.Of(Tone.Direction(0m)));
    }
}
