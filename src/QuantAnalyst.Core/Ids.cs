namespace QuantAnalyst.Core;

/// <summary>
/// A broker account id. <see cref="ToString"/> is masked to the last 3 characters, so string
/// interpolation and logging never reveal the full id (CLAUDE.md "Absolute safety rules").
/// Use <see cref="Value"/> only where the full id must be sent to the broker.
/// </summary>
public readonly record struct AccountId
{
    public AccountId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    /// <summary>The full id. Never log it; <see cref="ToString"/> is the loggable form.</summary>
    public string Value { get; }

    /// <summary>"***" followed by the last 3 characters.</summary>
    public string Masked => Mask(Value);

    public override string ToString() => Masked;

    /// <summary>Masks any account-id-like string to its last 3 characters.</summary>
    public static string Mask(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 3 ? "***" : string.Concat("***", value.AsSpan(value.Length - 3));

    /// <summary>True when <paramref name="suffix"/> (e.g. the 3 digits a user sees) ends this id.</summary>
    public bool EndsWith(string suffix) =>
        !string.IsNullOrEmpty(suffix) && Value.EndsWith(suffix.Trim(), StringComparison.Ordinal);
}

/// <summary>Avanza orderbook id: the primary instrument key (mapped to ISIN and ticker in the store).</summary>
public readonly record struct OrderbookId
{
    public OrderbookId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Broker order id.</summary>
public readonly record struct OrderId
{
    public OrderId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public enum OrderSide
{
    Buy,
    Sell,
}
