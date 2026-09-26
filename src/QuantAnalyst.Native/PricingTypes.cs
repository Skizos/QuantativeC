using System.Runtime.InteropServices;

namespace QuantAnalyst.Native;

/// <summary>Price and Greeks (mirror of <c>qe_bs_greeks</c>, 56 bytes). Units: vega per 1.00 vol,
/// theta = -dV/dT per year, rho per 1.00 rate. Values are NaN unless <see cref="Status"/> is OK.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct BlackScholesGreeks
{
    private readonly double price;
    private readonly double delta;
    private readonly double gamma;
    private readonly double vega;
    private readonly double theta;
    private readonly double rho;
    private readonly QeStatus status;
    private readonly int reserved;

    internal BlackScholesGreeks(double price, QeStatus status)
    {
        this.price = price;
        delta = gamma = vega = theta = rho = double.NaN;
        this.status = status;
        reserved = 0;
    }

    /// <summary>Gets the option price.</summary>
    public double Price => price;

    /// <summary>Gets dV/dS.</summary>
    public double Delta => delta;

    /// <summary>Gets d2V/dS2.</summary>
    public double Gamma => gamma;

    /// <summary>Gets dV/dvol per 1.00 of volatility.</summary>
    public double Vega => vega;

    /// <summary>Gets -dV/dT per year.</summary>
    public double Theta => theta;

    /// <summary>Gets dV/dr per 1.00 of rate.</summary>
    public double Rho => rho;

    /// <summary>Gets the per-element status.</summary>
    public QeStatus Status => status;

    /// <summary>Gets a value indicating whether the element was computed.</summary>
    public bool IsOk => status == QeStatus.Ok;
}

/// <summary>Implied-volatility request (mirror of <c>qe_iv_input</c>, 56 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ImpliedVolInput
{
    private readonly double spot;
    private readonly double strike;
    private readonly double rate;
    private readonly double dividendYield;
    private readonly double expiryYears;
    private readonly double price;
    private readonly OptionType optionType;
    private readonly int reserved;

    /// <summary>Initializes a new instance.</summary>
    public ImpliedVolInput(
        double spot, double strike, double rate, double dividendYield, double expiryYears, double price, OptionType optionType)
    {
        this.spot = spot;
        this.strike = strike;
        this.rate = rate;
        this.dividendYield = dividendYield;
        this.expiryYears = expiryYears;
        this.price = price;
        this.optionType = optionType;
        reserved = 0;
    }

    /// <summary>Gets the spot price.</summary>
    public double Spot => spot;

    /// <summary>Gets the strike.</summary>
    public double Strike => strike;

    /// <summary>Gets the target option price.</summary>
    public double Price => price;

    /// <summary>Gets the option type.</summary>
    public OptionType OptionType => optionType;
}

/// <summary>Implied-volatility result (mirror of <c>qe_iv_output</c>, 16 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ImpliedVolOutput
{
    private readonly double volatility;
    private readonly QeStatus status;
    private readonly int iterations;

    internal ImpliedVolOutput(double volatility, QeStatus status, int iterations)
    {
        this.volatility = volatility;
        this.status = status;
        this.iterations = iterations;
    }

    /// <summary>Gets the implied volatility, NaN on failure.</summary>
    public double Volatility => volatility;

    /// <summary>Gets the per-element status.</summary>
    public QeStatus Status => status;

    /// <summary>Gets the Brent iteration count.</summary>
    public int Iterations => iterations;
}

/// <summary>Lattice request (mirror of <c>qe_lattice_input</c>, 64 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct LatticeInput
{
    private readonly BlackScholesInput option;
    private readonly int steps;
    private readonly ExerciseStyle exercise;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="option">Option; volatility and expiry must be &gt; 0.</param>
    /// <param name="steps">CRR steps, 1..100000.</param>
    /// <param name="exercise">European or American.</param>
    public LatticeInput(BlackScholesInput option, int steps, ExerciseStyle exercise)
    {
        this.option = option;
        this.steps = steps;
        this.exercise = exercise;
    }

    /// <summary>Gets the option.</summary>
    public BlackScholesInput Option => option;

    /// <summary>Gets the number of steps.</summary>
    public int Steps => steps;

    /// <summary>Gets the exercise style.</summary>
    public ExerciseStyle Exercise => exercise;
}

/// <summary>Monte Carlo result (mirror of <c>qe_mc_result</c>, 32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct MonteCarloResult
{
    private readonly double price;
    private readonly double stdError;
    private readonly long paths;
    private readonly ulong seed;

    internal MonteCarloResult(double price, double stdError, long paths, ulong seed)
    {
        this.price = price;
        this.stdError = stdError;
        this.paths = paths;
        this.seed = seed;
    }

    /// <summary>Gets the price estimate.</summary>
    public double Price => price;

    /// <summary>Gets the standard error of the estimate.</summary>
    public double StdError => stdError;

    /// <summary>Gets the number of simulated paths.</summary>
    public long Paths => paths;

    /// <summary>Gets the seed actually used.</summary>
    public ulong Seed => seed;
}

/// <summary>Monte Carlo configuration. <see cref="Seed"/> 0 selects the engine seed.</summary>
public sealed record MonteCarloOptions
{
    /// <summary>Gets the number of simulated paths (antithetic partners count).</summary>
    public long Paths { get; init; } = 100_000;

    /// <summary>Gets the seed; 0 = engine seed.</summary>
    public ulong Seed { get; init; }

    /// <summary>Gets a value indicating whether to use antithetic pairs.</summary>
    public bool Antithetic { get; init; }

    /// <summary>Gets a value indicating whether to use the discounted terminal price as control variate.</summary>
    public bool ControlVariate { get; init; }

    /// <summary>Gets a value indicating whether to use randomized Sobol QMC.</summary>
    public bool Sobol { get; init; }

    /// <summary>Gets the number of Sobol digital shifts (&gt;= 2) used for the standard error.</summary>
    public int Replications { get; init; } = 16;

    /// <summary>Gets the time steps per path.</summary>
    public int Steps { get; init; } = 1;
}
