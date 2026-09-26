using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Analytics.Backtesting;

/// <summary>
/// xoshiro256** (Blackman &amp; Vigna 2018) seeded through SplitMix64: a fixed algorithm, so a seed gives the same
/// numbers on every .NET version and machine (CLAUDE.md: all randomness seeded, seed stored with every result).
/// </summary>
public sealed class SeededRandom
{
    private ulong _s0, _s1, _s2, _s3;
    private double _spare = double.NaN;

    public SeededRandom(ulong seed)
    {
        Seed = seed;
        ulong x = seed;
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
    }

    public ulong Seed { get; }

    public ulong NextUInt64()
    {
        ulong result = ulong.RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = ulong.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, 1) with 53 random bits.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Standard normal (Marsaglia polar method).</summary>
    public double NextGaussian()
    {
        if (!double.IsNaN(_spare))
        {
            double s = _spare;
            _spare = double.NaN;
            return s;
        }

        double u, v, q;
        do
        {
            u = (2.0 * NextDouble()) - 1.0;
            v = (2.0 * NextDouble()) - 1.0;
            q = (u * u) + (v * v);
        }
        while (q >= 1.0 || q == 0.0);

        double f = Math.Sqrt(-2.0 * Math.Log(q) / q);
        _spare = v * f;
        return u * f;
    }

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

/// <summary>Parameters of the synthetic market.</summary>
public sealed record SyntheticMarketOptions
{
    public int Instruments { get; init; } = 10;

    public int Periods { get; init; } = 2520;

    public ulong Seed { get; init; } = 1;

    /// <summary>Gets the first date; bars fall on weekdays from here (no holidays: the data is synthetic).</summary>
    public DateOnly Start { get; init; } = new(2015, 1, 5);

    /// <summary>Gets the annual drift of every instrument (0 = random walk).</summary>
    public double AnnualDrift { get; init; }

    /// <summary>Gets the range annual volatilities are drawn from, uniformly per instrument.</summary>
    public (double Min, double Max) AnnualVolatility { get; init; } = (0.15, 0.45);

    /// <summary>Gets the median daily volume in shares (lognormal, σ = 0.5 around it).</summary>
    public double MedianVolume { get; init; } = 1_000_000;
}

/// <summary>
/// Seeded geometric-Brownian-motion daily bars with consistent OHLC: the close-to-close log return splits into an
/// overnight gap (20 % of the variance) and an intraday move; the high and low extend past the open and close by
/// half-normal amounts. Point-in-time and survivorship-free by construction (nothing is delisted or restated).
/// </summary>
public static class SyntheticMarket
{
    public const string SourceName = "synthetic-gbm";

    /// <summary>A flat 0.01 tick for synthetic instruments (real instruments use their own table from the store).</summary>
    public static readonly TickSizeTable CentTicks = new([new TickSizeBand(0m, 1_000_000_000m, 0.01m)]);

    public static MarketPanel Generate(SyntheticMarketOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Instruments < 1 || options.Periods < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Need at least 1 instrument and 2 periods.");
        }

        var rng = new SeededRandom(options.Seed);
        int n = options.Instruments, periods = options.Periods;
        var dates = new DateOnly[periods];
        DateOnly d = options.Start;
        for (int t = 0; t < periods; t++)
        {
            while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                d = d.AddDays(1);
            }

            dates[t] = d;
            d = d.AddDays(1);
        }

        double[] open = new double[n * periods], high = new double[n * periods], low = new double[n * periods];
        double[] close = new double[n * periods], volume = new double[n * periods];
        var instruments = new PanelInstrument[n];
        for (int i = 0; i < n; i++)
        {
            instruments[i] = new PanelInstrument($"SYN{i + 1:0000}", 1, false, CentTicks);
            double sigma = (options.AnnualVolatility.Min + ((options.AnnualVolatility.Max - options.AnnualVolatility.Min) * rng.NextDouble())) / Math.Sqrt(252);
            double mu = (options.AnnualDrift / 252) - (0.5 * sigma * sigma);
            double price = 20 + (480 * rng.NextDouble());
            for (int t = 0; t < periods; t++)
            {
                int k = (i * periods) + t;
                double gap = (0.2 * mu) + (Math.Sqrt(0.2) * sigma * rng.NextGaussian());
                double day = (0.8 * mu) + (Math.Sqrt(0.8) * sigma * rng.NextGaussian());
                double o = t == 0 ? price : price * Math.Exp(gap);
                double c = o * Math.Exp(day);
                open[k] = o;
                close[k] = c;
                high[k] = Math.Max(o, c) * Math.Exp(0.5 * sigma * Math.Abs(rng.NextGaussian()));
                low[k] = Math.Min(o, c) * Math.Exp(-0.5 * sigma * Math.Abs(rng.NextGaussian()));
                volume[k] = Math.Round(options.MedianVolume * Math.Exp(0.5 * rng.NextGaussian()));
                price = c;
            }
        }

        var source = new DataSourceInfo(
            SourceName,
            PointInTime: true,
            SurvivorshipFree: true,
            Notes: $"seeded GBM, seed {options.Seed}, drift {options.AnnualDrift:0.####}/yr, vol {options.AnnualVolatility.Min:0.##}–{options.AnnualVolatility.Max:0.##}/yr");
        return new MarketPanel(dates, instruments, open, high, low, close, volume, source);
    }
}
