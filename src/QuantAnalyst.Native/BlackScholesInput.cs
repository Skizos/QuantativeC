using System.Runtime.InteropServices;

namespace QuantAnalyst.Native;

/// <summary>
/// One European option under Black-Scholes-Merton. Blittable mirror of <c>qe_bs_input</c>
/// (56 bytes; layout verified against the native library by tests).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct BlackScholesInput : IEquatable<BlackScholesInput>
{
    private readonly double spot;
    private readonly double strike;
    private readonly double rate;
    private readonly double dividendYield;
    private readonly double volatility;
    private readonly double expiryYears;
    private readonly OptionType optionType;
    private readonly int reserved;

    /// <summary>Initializes a new instance. Domain checks happen natively, per element.</summary>
    /// <param name="spot">Spot price, &gt; 0.</param>
    /// <param name="strike">Strike, &gt; 0.</param>
    /// <param name="rate">Continuously compounded risk-free rate.</param>
    /// <param name="dividendYield">Continuously compounded dividend yield.</param>
    /// <param name="volatility">Annualized volatility, &gt;= 0.</param>
    /// <param name="expiryYears">Time to expiry in years, &gt;= 0.</param>
    /// <param name="optionType">Call or put.</param>
    public BlackScholesInput(
        double spot,
        double strike,
        double rate,
        double dividendYield,
        double volatility,
        double expiryYears,
        OptionType optionType)
    {
        this.spot = spot;
        this.strike = strike;
        this.rate = rate;
        this.dividendYield = dividendYield;
        this.volatility = volatility;
        this.expiryYears = expiryYears;
        this.optionType = optionType;
        reserved = 0;
    }

    /// <summary>Gets the spot price.</summary>
    public double Spot => spot;

    /// <summary>Gets the strike.</summary>
    public double Strike => strike;

    /// <summary>Gets the continuously compounded risk-free rate.</summary>
    public double Rate => rate;

    /// <summary>Gets the continuously compounded dividend yield.</summary>
    public double DividendYield => dividendYield;

    /// <summary>Gets the annualized volatility.</summary>
    public double Volatility => volatility;

    /// <summary>Gets the time to expiry in years.</summary>
    public double ExpiryYears => expiryYears;

    /// <summary>Gets the option type.</summary>
    public OptionType OptionType => optionType;

    /// <summary>Equality operator.</summary>
    public static bool operator ==(BlackScholesInput left, BlackScholesInput right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(BlackScholesInput left, BlackScholesInput right) => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(BlackScholesInput other) =>
        spot.Equals(other.spot) && strike.Equals(other.strike) && rate.Equals(other.rate) &&
        dividendYield.Equals(other.dividendYield) && volatility.Equals(other.volatility) &&
        expiryYears.Equals(other.expiryYears) && optionType == other.optionType && reserved == other.reserved;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BlackScholesInput other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(spot, strike, rate, dividendYield, volatility, expiryYears, optionType);
}
