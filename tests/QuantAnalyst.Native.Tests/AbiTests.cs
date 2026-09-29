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
    [InlineData(1, 3, true)]
    [InlineData(1, 2, false)] // 1.3 adds qe_bt_set_courtage, which the backtest runner needs
    [InlineData(1, 7, true)]
    [InlineData(1, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(0, 9, false)]
    [InlineData(2, 0, false)]
    public void IsCompatible_RequiresSameMajorAndAtLeastExpectedMinor(int major, int minor, bool expected) =>
        Assert.Equal(expected, QeAbi.IsCompatible(major, minor));
}
