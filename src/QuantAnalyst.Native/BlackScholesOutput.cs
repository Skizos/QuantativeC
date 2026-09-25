using System.Runtime.InteropServices;

namespace QuantAnalyst.Native;

/// <summary>
/// Result for one <see cref="BlackScholesInput"/>. Blittable mirror of <c>qe_bs_output</c>
/// (16 bytes). <see cref="Price"/> is NaN unless <see cref="Status"/> is <see cref="QeStatus.Ok"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct BlackScholesOutput : IEquatable<BlackScholesOutput>
{
    private readonly double price;
    private readonly QeStatus status;
    private readonly int reserved;

    internal BlackScholesOutput(double price, QeStatus status)
    {
        this.price = price;
        this.status = status;
        reserved = 0;
    }

    /// <summary>Gets the option price, NaN on failure.</summary>
    public double Price => price;

    /// <summary>Gets the per-element status.</summary>
    public QeStatus Status => status;

    /// <summary>Gets a value indicating whether this element was priced successfully.</summary>
    public bool IsOk => status == QeStatus.Ok;

    /// <summary>Equality operator.</summary>
    public static bool operator ==(BlackScholesOutput left, BlackScholesOutput right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(BlackScholesOutput left, BlackScholesOutput right) => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(BlackScholesOutput other) =>
        price.Equals(other.price) && status == other.status && reserved == other.reserved;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BlackScholesOutput other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(price, status);
}
