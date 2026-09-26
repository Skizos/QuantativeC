using System.Diagnostics;

namespace QuantAnalyst.Core;

/// <summary>
/// A credential or token held in memory. <see cref="ToString"/> returns "***", so accidental
/// interpolation, logging or debugger display never shows the value. Call <see cref="Reveal"/>
/// only at the point where the value is sent (HTTP body/header, HMAC key).
/// </summary>
[DebuggerDisplay("***")]
public sealed class Secret
{
    private readonly string _value;

    public Secret(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    public string Reveal() => _value;

    public override string ToString() => "***";
}
