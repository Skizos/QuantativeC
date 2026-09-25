using System.Text;
using QuantAnalyst.Avanza.Auth;

namespace QuantAnalyst.Avanza.Tests;

public sealed class TotpTests
{
    private static readonly byte[] Sha1Key = Encoding.ASCII.GetBytes("12345678901234567890");
    private static readonly byte[] Sha256Key = Encoding.ASCII.GetBytes("12345678901234567890123456789012");
    private static readonly byte[] Sha512Key = Encoding.ASCII.GetBytes("1234567890123456789012345678901234567890123456789012345678901234");

    // RFC 6238 Appendix B (8 digits, 30 s step). Cross-checked with Python's hmac module.
    [Theory]
    [InlineData(59L, "94287082", "46119246", "90693936")]
    [InlineData(1111111109L, "07081804", "68084774", "25091201")]
    [InlineData(1111111111L, "14050471", "67062674", "99943326")]
    [InlineData(1234567890L, "89005924", "91819424", "93441116")]
    [InlineData(2000000000L, "69279037", "90698825", "38618901")]
    [InlineData(20000000000L, "65353130", "77737706", "47863826")]
    public void Rfc6238AppendixB(long unixSeconds, string sha1, string sha256, string sha512)
    {
        DateTimeOffset t = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        Assert.Equal(sha1, Totp.Compute(Sha1Key, t, digits: 8));
        Assert.Equal(sha256, Totp.Compute(Sha256Key, t, digits: 8, algorithm: TotpAlgorithm.Sha256));
        Assert.Equal(sha512, Totp.Compute(Sha512Key, t, digits: 8, algorithm: TotpAlgorithm.Sha512));
    }

    // RFC 4226 Appendix D (HOTP, 6 digits).
    [Fact]
    public void Rfc4226AppendixD()
    {
        string[] expected = ["755224", "287082", "359152", "969429", "338314", "254676", "287922", "162583", "399871", "520489"];
        for (int c = 0; c < expected.Length; c++)
        {
            Assert.Equal(expected[c], Totp.Hotp(Sha1Key, c));
        }
    }

    [Fact]
    public void DefaultIsSixDigitSha1FromBase32Secret()
    {
        byte[] key = Base32.Decode("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
        Assert.Equal(Sha1Key, key);
        Assert.Equal("287082", Totp.Compute(key, DateTimeOffset.FromUnixTimeSeconds(59)));
    }

    [Theory]
    [InlineData(0L, 30)]
    [InlineData(29L, 1)]
    [InlineData(30L, 30)]
    [InlineData(59L, 1)]
    public void SecondsRemaining(long unixSeconds, int expected) =>
        Assert.Equal(expected, Totp.SecondsRemaining(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));

    [Theory]
    [InlineData("gezd gnbv-gy3t qojq gezd gnbv gy3t qojq")]
    [InlineData("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ====")]
    public void Base32_IgnoresCaseSpacesHyphensAndPadding(string text) =>
        Assert.Equal(Sha1Key, Base32.Decode(text));

    [Theory]
    [InlineData("")]
    [InlineData("GEZ1")]
    [InlineData("GE=ZD")]
    public void Base32_RejectsInvalidInputWithoutEchoingIt(string text)
    {
        var ex = Assert.Throws<FormatException>(() => Base32.Decode(text));
        if (text.Length > 0)
        {
            Assert.DoesNotContain(text, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Base32_RoundTrips()
    {
        var rng = new Random(20260925);
        for (int n = 1; n < 64; n++)
        {
            byte[] data = new byte[n];
            rng.NextBytes(data);
            Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
        }
    }
}
