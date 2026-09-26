namespace QuantAnalyst.Analytics.Statistics;

/// <summary>
/// Standard normal distribution for the statistics layer (.NET has no erf). Accuracy is checked against scipy in
/// <c>tests/QuantAnalyst.Analytics.Tests/Reference/phase5_reference.json</c>.
/// </summary>
public static class Normal
{
    private const double InvSqrt2Pi = 0.39894228040143267794;

    public static double Pdf(double x) => InvSqrt2Pi * Math.Exp(-0.5 * x * x);

    /// <summary>
    /// Φ(x). |x| ≤ 3: Marsaglia's Taylor series Φ(x) = ½ + φ(x)·(x + x³/3 + x⁵/15 + …) (Marsaglia 2004,
    /// "Evaluating the Normal Distribution", J. Stat. Software 11(4)). |x| &gt; 3: the Mills-ratio continued fraction
    /// (Lentz), which keeps full relative accuracy in the tails.
    /// </summary>
    public static double Cdf(double x)
    {
        if (double.IsNaN(x))
        {
            return double.NaN;
        }

        if (x < -38.5)
        {
            return 0.0;
        }

        if (x > 38.5)
        {
            return 1.0;
        }

        if (Math.Abs(x) <= 3.0)
        {
            double sum = x, term = x, x2 = x * x;
            for (int i = 3; i < 400; i += 2)
            {
                term *= x2 / i;
                double next = sum + term;
                if (next == sum)
                {
                    break;
                }

                sum = next;
            }

            return 0.5 + (Pdf(x) * sum);
        }

        double tail = Pdf(x) * MillsRatio(Math.Abs(x));
        return x < 0 ? tail : 1.0 - tail;
    }

    /// <summary>
    /// Φ⁻¹(p) for p in (0, 1): Acklam's rational approximation refined with Halley steps on <see cref="Cdf"/>; the upper
    /// half by symmetry (−Φ⁻¹(1−p)), as scipy's ndtri does.
    /// </summary>
    public static double Quantile(double p)
    {
        if (!(p > 0.0 && p < 1.0))
        {
            throw new ArgumentOutOfRangeException(nameof(p), p, "p must be in (0, 1).");
        }

        if (p > 0.5)
        {
            return -Quantile(1.0 - p); // 1 − p is exact here (Sterbenz); the lower tail keeps full relative accuracy
        }

        double x = Acklam(p);
        for (int i = 0; i < 3; i++)
        {
            double e = Cdf(x) - p;
            double u = e / Pdf(x);
            double step = u / (1.0 + (0.5 * x * u));
            x -= step;
            if (Math.Abs(step) <= 1e-15 * Math.Max(1.0, Math.Abs(x)))
            {
                break;
            }
        }

        return x;
    }

    /// <summary>R(x) = (1 − Φ(x)) / φ(x) for x &gt; 0, by the continued fraction 1/(x + 1/(x + 2/(x + 3/(x + …)))).</summary>
    private static double MillsRatio(double x)
    {
        const double tiny = 1e-300;
        double f = x, c = x, d = 0.0;
        for (int k = 1; k < 500; k++)
        {
            d = x + (k * d);
            d = Math.Abs(d) < tiny ? tiny : d;
            c = x + (k / c);
            c = Math.Abs(c) < tiny ? tiny : c;
            d = 1.0 / d;
            double delta = c * d;
            f *= delta;
            if (Math.Abs(delta - 1.0) < 1e-16)
            {
                break;
            }
        }

        return 1.0 / f;
    }

    // P. J. Acklam's algorithm (relative error < 1.15e-9 before refinement).
    private static double Acklam(double p)
    {
        ReadOnlySpan<double> a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        ReadOnlySpan<double> b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01, 1.0];
        ReadOnlySpan<double> c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        ReadOnlySpan<double> d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00, 1.0];
        const double low = 0.02425;
        if (p < low)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            return Horner(c, q) / Horner(d, q);
        }

        if (p > 1 - low)
        {
            double q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -Horner(c, q) / Horner(d, q);
        }

        double r = p - 0.5, s = r * r;
        return Horner(a, s) * r / Horner(b, s);
    }

    /// <summary>Evaluates c[0]·x^(n−1) + … + c[n−1].</summary>
    private static double Horner(ReadOnlySpan<double> coefficients, double x)
    {
        double y = 0;
        foreach (double c in coefficients)
        {
            y = (y * x) + c;
        }

        return y;
    }
}
