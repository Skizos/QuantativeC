namespace QuantAnalyst.Avanza.Auth;

/// <summary>RFC 4648 Base32 decoding (the format authenticator apps use for TOTP secrets).</summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// Decodes a Base32 string. Case-insensitive; spaces, hyphens and trailing '=' padding are ignored.
    /// Throws <see cref="FormatException"/> without echoing the input (it is a secret).
    /// </summary>
    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0;
        int bits = 0;
        bool padding = false;
        foreach (char raw in text)
        {
            if (raw is ' ' or '-')
            {
                continue;
            }

            if (raw == '=')
            {
                padding = true;
                continue;
            }

            if (padding)
            {
                throw new FormatException("Base32: data after '=' padding.");
            }

            int value = Alphabet.IndexOf(char.ToUpperInvariant(raw), StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException("Base32: invalid character (allowed: A-Z, 2-7).");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        if (output.Count == 0)
        {
            throw new FormatException("Base32: empty secret.");
        }

        return [.. output];
    }

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 31]);
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }
}
