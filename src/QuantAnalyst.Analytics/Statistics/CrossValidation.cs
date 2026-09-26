namespace QuantAnalyst.Analytics.Statistics;

/// <summary>One train/test split: indices into the observation sequence, in increasing order.</summary>
public sealed record TrainTestSplit(IReadOnlyList<int> Train, IReadOnlyList<int> Test);

/// <summary>
/// Time-series cross-validation without leakage (López de Prado 2018, <i>Advances in Financial Machine Learning</i>,
/// ch. 7):
/// <list type="bullet">
/// <item><b>Purged K-fold:</b> K contiguous test folds. A training observation is dropped (purged) when its label window
/// [i, i + horizon] overlaps the test fold's span, and an embargo of h observations after each test fold is also
/// dropped, because serially correlated features there still carry information about the test period.</item>
/// <item><b>Walk-forward:</b> train on a window that ends before each test window (anchored or rolling); only the past
/// ever trains the future.</item>
/// </list>
/// </summary>
public static class CrossValidation
{
    /// <param name="observations">Number of observations in time order.</param>
    /// <param name="folds">K ≥ 2.</param>
    /// <param name="labelHorizon">Observations a label looks ahead (0 = the label is known at the observation).</param>
    /// <param name="embargo">Observations dropped after each test fold.</param>
    public static IReadOnlyList<TrainTestSplit> PurgedKFold(int observations, int folds, int labelHorizon = 0, int embargo = 0)
    {
        if (folds < 2 || observations < folds)
        {
            throw new ArgumentOutOfRangeException(nameof(folds), folds, "Need 2 ≤ K ≤ observations.");
        }

        if (labelHorizon < 0 || embargo < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(labelHorizon), "Horizon and embargo must be ≥ 0.");
        }

        var splits = new List<TrainTestSplit>(folds);
        for (int k = 0; k < folds; k++)
        {
            int start = (int)((long)k * observations / folds);
            int end = (int)((long)(k + 1) * observations / folds); // exclusive
            var test = Enumerable.Range(start, end - start).ToList();
            int testLast = end - 1;
            var train = new List<int>(observations - test.Count);
            for (int i = 0; i < observations; i++)
            {
                bool inTest = i >= start && i < end;
                bool labelOverlapsTest = i < start && i + labelHorizon >= start; // before the fold, label reaches into it
                bool inEmbargo = i > testLast && i <= testLast + labelHorizon + embargo; // after the fold: purge + embargo
                if (!inTest && !labelOverlapsTest && !inEmbargo)
                {
                    train.Add(i);
                }
            }

            splits.Add(new TrainTestSplit(train, test));
        }

        return splits;
    }

    /// <param name="trainSize">Training window length (rolling), or the minimum length when <paramref name="anchored"/>.</param>
    /// <param name="testSize">Test window length; windows step forward by this much.</param>
    /// <param name="gap">Observations skipped between train and test (label horizon + embargo).</param>
    public static IReadOnlyList<TrainTestSplit> WalkForward(int observations, int trainSize, int testSize, int gap = 0, bool anchored = false)
    {
        if (trainSize < 1 || testSize < 1 || gap < 0 || trainSize + gap + testSize > observations)
        {
            throw new ArgumentOutOfRangeException(nameof(trainSize), "Windows do not fit the observations.");
        }

        var splits = new List<TrainTestSplit>();
        for (int testStart = trainSize + gap; testStart + testSize <= observations; testStart += testSize)
        {
            int trainEnd = testStart - gap; // exclusive
            int trainStart = anchored ? 0 : trainEnd - trainSize;
            splits.Add(new TrainTestSplit(
                Enumerable.Range(trainStart, trainEnd - trainStart).ToList(),
                Enumerable.Range(testStart, testSize).ToList()));
        }

        return splits;
    }
}
