namespace QuantAnalyst.Analytics.Statistics;

/// <summary>Result of combinatorially symmetric cross-validation.</summary>
/// <param name="Probability">PBO: share of splits in which the best in-sample configuration ranks at or below the out-of-sample median.</param>
/// <param name="ProbabilityOfOosLoss">Share of splits in which the best in-sample configuration has a negative out-of-sample Sharpe.</param>
/// <param name="Logits">λ = ln(ω/(1−ω)) per split, in combination order.</param>
public sealed record PboResult(double Probability, double ProbabilityOfOosLoss, int Blocks, int Combinations, IReadOnlyList<double> Logits)
{
    public double LogitMean => Logits.Count == 0 ? double.NaN : Logits.Average();
}

/// <summary>
/// Probability of Backtest Overfitting by CSCV (Bailey, Borwein, López de Prado, Zhu 2017), with the per-period Sharpe
/// ratio as the performance metric:
/// <list type="number">
/// <item>the T×N matrix of per-period returns (rows in time order, one column per configuration) is cut into S equal
/// blocks; the oldest T mod S rows are dropped</item>
/// <item>for every choice of S/2 blocks as in-sample (the rest out-of-sample) the best in-sample configuration n* is taken
/// (first on ties) and its out-of-sample rank r (average ranks, 1 = worst) gives ω = r/(N+1), λ = ln(ω/(1−ω))</item>
/// <item>PBO = share of λ ≤ 0</item>
/// </list>
/// Checked against an independent numpy implementation and pypbo (docs/plans/05-phase5-backtesting.md).
/// </summary>
public static class Pbo
{
    public const int DefaultBlocks = 16;

    /// <param name="returns">Row-major T×N matrix: <c>returns[t * n + j]</c> is configuration j's return in period t.</param>
    public static PboResult Cscv(ReadOnlySpan<double> returns, int periods, int configurations, int blocks = DefaultBlocks)
    {
        if (configurations < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(configurations), configurations, "PBO needs at least 2 configurations.");
        }

        if (blocks < 2 || blocks % 2 != 0 || blocks > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(blocks), blocks, "S must be an even number between 2 and 24.");
        }

        if (returns.Length != periods * configurations)
        {
            throw new ArgumentException("The matrix size does not match periods × configurations.", nameof(returns));
        }

        int skip = periods % blocks;
        int size = periods / blocks;
        if (size < 2)
        {
            throw new ArgumentException($"{periods} periods are too few for {blocks} blocks (need at least 2 per block).", nameof(periods));
        }

        // Per block and configuration: count, sum and sum of squares, so each split's Sharpe is O(S·N).
        int n = configurations;
        double[] sum = new double[blocks * n];
        double[] sumSq = new double[blocks * n];
        for (int b = 0; b < blocks; b++)
        {
            for (int t = skip + (b * size); t < skip + ((b + 1) * size); t++)
            {
                for (int j = 0; j < n; j++)
                {
                    double r = returns[(t * n) + j];
                    sum[(b * n) + j] += r;
                    sumSq[(b * n) + j] += r * r;
                }
            }
        }

        var logits = new List<double>();
        int overfit = 0, losses = 0;
        double[] isSharpe = new double[n], oosSharpe = new double[n];
        bool[] inSample = new bool[blocks];
        foreach (int[] combination in Combinations(blocks, blocks / 2))
        {
            Array.Clear(inSample);
            foreach (int b in combination)
            {
                inSample[b] = true;
            }

            for (int j = 0; j < n; j++)
            {
                double sIs = 0, qIs = 0, sOos = 0, qOos = 0;
                for (int b = 0; b < blocks; b++)
                {
                    if (inSample[b])
                    {
                        sIs += sum[(b * n) + j];
                        qIs += sumSq[(b * n) + j];
                    }
                    else
                    {
                        sOos += sum[(b * n) + j];
                        qOos += sumSq[(b * n) + j];
                    }
                }

                int half = size * (blocks / 2);
                isSharpe[j] = SharpeFromSums(sIs, qIs, half);
                oosSharpe[j] = SharpeFromSums(sOos, qOos, half);
            }

            int best = 0;
            for (int j = 1; j < n; j++)
            {
                if (isSharpe[j] > isSharpe[best])
                {
                    best = j;
                }
            }

            double rank = AverageRank(oosSharpe, best);
            double omega = rank / (n + 1);
            double lambda = Math.Log(omega / (1 - omega));
            logits.Add(lambda);
            overfit += lambda <= 0 ? 1 : 0;
            losses += oosSharpe[best] < 0 ? 1 : 0;
        }

        return new PboResult((double)overfit / logits.Count, (double)losses / logits.Count, blocks, logits.Count, logits);
    }

    /// <summary>Largest even S ≤ <paramref name="preferred"/> that leaves at least <paramref name="minPerBlock"/> periods per block.</summary>
    public static int ChooseBlocks(int periods, int preferred = DefaultBlocks, int minPerBlock = 20)
    {
        for (int s = preferred - (preferred % 2); s >= 2; s -= 2)
        {
            if (periods / s >= minPerBlock)
            {
                return s;
            }
        }

        throw new ArgumentException($"{periods} periods are too few for PBO (need {2 * minPerBlock}).", nameof(periods));
    }

    private static double SharpeFromSums(double sum, double sumSq, int count)
    {
        double mean = sum / count;
        double variance = (sumSq - (count * mean * mean)) / (count - 1);
        return variance > 0 ? mean / Math.Sqrt(variance) : double.NaN;
    }

    /// <summary>Average (fractional) rank of element <paramref name="index"/>, 1 = smallest, as scipy's rankdata.</summary>
    private static double AverageRank(double[] values, int index)
    {
        double v = values[index];
        int less = 0, equal = 0;
        foreach (double x in values)
        {
            if (x < v)
            {
                less++;
            }
            else if (x == v)
            {
                equal++;
            }
        }

        return less + ((equal + 1) / 2.0);
    }

    /// <summary>All k-subsets of 0..n−1 in lexicographic order (the order of itertools.combinations).</summary>
    internal static IEnumerable<int[]> Combinations(int n, int k)
    {
        int[] c = [.. Enumerable.Range(0, k)];
        while (true)
        {
            yield return (int[])c.Clone();
            int i = k - 1;
            while (i >= 0 && c[i] == n - k + i)
            {
                i--;
            }

            if (i < 0)
            {
                yield break;
            }

            c[i]++;
            for (int j = i + 1; j < k; j++)
            {
                c[j] = c[j - 1] + 1;
            }
        }
    }
}
