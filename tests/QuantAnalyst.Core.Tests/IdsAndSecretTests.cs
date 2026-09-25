using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Core.Tests;

public sealed class IdsAndSecretTests
{
    [Fact]
    public void AccountId_ToStringIsMaskedToLastThreeDigits()
    {
        var id = new AccountId("1234567");
        Assert.Equal("***567", id.ToString());
        Assert.Equal("***567", $"{id}");
        Assert.Equal("1234567", id.Value);
        Assert.True(id.EndsWith("567"));
        Assert.False(id.EndsWith("566"));
    }

    [Theory]
    [InlineData("12", "***")]
    [InlineData("", "***")]
    [InlineData(null, "***")]
    [InlineData("9876543210", "***210")]
    public void AccountId_MaskHandlesShortAndLongValues(string? value, string expected) =>
        Assert.Equal(expected, AccountId.Mask(value));

    [Fact]
    public void AccountId_RecordEqualityUsesValue()
    {
        Assert.Equal(new AccountId(" 1234567 "), new AccountId("1234567"));
        Assert.NotEqual(new AccountId("1234567"), new AccountId("7654567"));
    }

    [Fact]
    public void Secret_NeverFormatsItsValue()
    {
        var s = new Secret("hunter2-FAKE");
        Assert.Equal("***", s.ToString());
        Assert.Equal("***", $"{s}");
        Assert.Equal("hunter2-FAKE", s.Reveal());
        Assert.Throws<ArgumentException>(() => new Secret(string.Empty));
    }

    [Fact]
    public void SchemaDrift_MessageListsPathsAndTierAHalts()
    {
        var ex = new SchemaDriftException("positions", "positions/2026-09-25", DtoTier.A, ["$.a", "$.b[0].c"], "unknown fields");
        Assert.Contains("$.a, $.b[0].c", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.HaltsTrading);
        Assert.False(new SchemaDriftException("search", "v", DtoTier.B, [], "x").HaltsTrading);
    }
}
