using System.Buffers.Binary;
using System.Security.Cryptography;

namespace QuantAnalyst.Avanza.Auth;

public enum TotpAlgorithm
{
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>
/// RFC 6238 TOTP over RFC 4226 HOTP. Avanza uses the defaults: HMAC-SHA1, 30 s step, 6 digits, Base32 secret
/// (avanza-endpoints.md §1). Codes are secrets: never log them.
/// </summary>
internal static class Totp
{
    public const int DefaultStepSeconds = 30;
    public const int DefaultDigits = 6;

    /// <summary>Time step counter T = floor(unixSeconds / step).</summary>
    public static long Counter(DateTimeOffset utcNow, int stepSeconds = DefaultStepSeconds) =>
        utcNow.ToUnixTimeSeconds() / stepSeconds;

    /// <summary>Seconds left before the current code expires.</summary>
    public static int SecondsRemaining(DateTimeOffset utcNow, int stepSeconds = DefaultStepSeconds) =>
        stepSeconds - (int)(utcNow.ToUnixTimeSeconds() % stepSeconds);

    public static string Compute(
        ReadOnlySpan<byte> key,
        DateTimeOffset utcNow,
        int digits = DefaultDigits,
        int stepSeconds = DefaultStepSeconds,
        TotpAlgorithm algorithm = TotpAlgorithm.Sha1) =>
        Hotp(key, Counter(utcNow, stepSeconds), digits, algorithm);

    /// <summary>RFC 4226 §5.3: HMAC over the big-endian counter, dynamic truncation, modulo 10^digits.</summary>
    public static string Hotp(ReadOnlySpan<byte> key, long counter, int digits = DefaultDigits, TotpAlgorithm algorithm = TotpAlgorithm.Sha1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digits, 6);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 9);
        if (key.IsEmpty)
        {
            throw new ArgumentException("TOTP key is empty.", nameof(key));
        }

        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);
        Span<byte> hash = stackalloc byte[64];
        int length = algorithm switch
        {
#pragma warning disable CA5350 // HMAC-SHA1 is what RFC 6238 and Avanza's TOTP use; HOTP truncation is not affected by SHA-1 collisions.
            TotpAlgorithm.Sha1 => HMACSHA1.HashData(key, message, hash),
#pragma warning restore CA5350
            TotpAlgorithm.Sha256 => HMACSHA256.HashData(key, message, hash),
            TotpAlgorithm.Sha512 => HMACSHA512.HashData(key, message, hash),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null),
        };

        int offset = hash[length - 1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24)
                     | (hash[offset + 1] << 16)
                     | (hash[offset + 2] << 8)
                     | hash[offset + 3];
        int modulo = 1;
        for (int i = 0; i < digits; i++)
        {
            modulo *= 10;
        }

        CryptographicOperations.ZeroMemory(hash);
        return (binary % modulo).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }
}
