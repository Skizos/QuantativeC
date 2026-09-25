using System.Net;
using QuantAnalyst.Core;

namespace QuantAnalyst.Avanza;

/// <summary>In-memory session: cookie jar and security token. Never persisted to disk, never logged.</summary>
internal sealed class AvanzaSession
{
    public const string SecurityTokenHeader = "X-SecurityToken";
    public const string SecurityTokenCookie = "AZACSRF";

    public CookieContainer Cookies { get; } = new();

    public Secret? SecurityToken { get; private set; }

    /// <summary>"header" or "cookie": where the token came from (ADR 0002 §2). Safe to log.</summary>
    public string? TokenSource { get; private set; }

    public void SetToken(Secret token, string source)
    {
        SecurityToken = token;
        TokenSource = source;
    }
}
