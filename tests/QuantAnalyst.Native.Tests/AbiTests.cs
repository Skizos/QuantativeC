namespace QuantAnalyst.Native.Tests;

public sealed class AbiTests
{
    [Fact]
    public void NativeVersion_MatchesExpectedAbi()
    {
        Version native = QeAbi.NativeVersion;

        Assert.Equal(QeAbi.ExpectedMajor, native.Major);
        Assert.True(native.Minor >= QeAbi.ExpectedMinor);
        QeAbi.EnsureCompatible();
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(1, 7, true)]
    [InlineData(0, 9, false)]
    [InlineData(2, 0, false)]
    public void IsCompatible_RequiresSameMajorAndAtLeastExpectedMinor(int major, int minor, bool expected) =>
        Assert.Equal(expected, QeAbi.IsCompatible(major, minor));
}
