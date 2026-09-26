using System.Security.Cryptography;

namespace QuantAnalyst.Avanza.Auth;

/// <summary>Checks a TOTP secret before it is stored, without revealing it in the error.</summary>
public static class TotpSecretValidator
{
    /// <summary>Throws <see cref="ArgumentException"/> unless the secret is Base32 that yields a usable HMAC key.</summary>
    public static void Validate(string base32Secret)
    {
        byte[] key;
        try
        {
            key = Base32.Decode(base32Secret);
        }
        catch (FormatException)
        {
            throw new ArgumentException("The TOTP secret is not valid Base32 (letters A-Z and digits 2-7).");
        }

        try
        {
            if (key.Length < 10)
            {
                throw new ArgumentException("The TOTP secret is too short (expected at least 80 bits).");
            }

            _ = Totp.Compute(key, DateTimeOffset.UtcNow);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
