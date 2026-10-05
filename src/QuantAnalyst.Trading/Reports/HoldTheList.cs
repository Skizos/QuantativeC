using System.Globalization;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Paper;

namespace QuantAnalyst.Trading.Reports;

/// <summary>One Paper day against the list held equally over the same interval (plan 24).</summary>
/// <param name="From">The previous Paper close the day runs from (Paper's start value is the book at those prices).</param>
/// <param name="PaperReturn">Paper's return that day (the account, cash included).</param>
/// <param name="Return">The list's return: the average of its shares' close-to-close returns in SEK, dividends included.</param>
/// <param name="Exposure">The share of the account Paper had in shares at <paramref name="From"/>'s close.</param>
/// <param name="Shares">The shares averaged.</param>
/// <param name="LeftOut">Listed shares left out that day and why, e.g. "ERIC B (split-like move)".</param>
public sealed record ListDay(DateOnly Date, DateOnly From, decimal PaperReturn, decimal Return, decimal Exposure, int Shares, IReadOnlyList<string> LeftOut);

/// <summary>Paper against holding the list over some days (plan 24).</summary>
/// <param name="ListReturn">The list fully invested, compounded.</param>
/// <param name="Exposure">Paper's average share invested at the close before each day.</param>
/// <param name="ListAtExposure">Each day's list return times that day's exposure, compounded: the list with Paper's cash.</param>
/// <param name="TStat">Of the daily differences (Paper minus the list at Paper's exposure); null below <see cref="HoldTheList.MinDaysForT"/> days or without spread.</param>
public sealed record PaperVsList(int Days, decimal PaperReturn, decimal ListReturn, decimal Exposure, decimal ListAtExposure, double? TStat)
{
    /// <summary>Gets Paper minus the list at Paper's exposure: what the strategy's choices and costs did with the money it had in shares.</summary>
    public decimal Difference => PaperReturn - ListAtExposure;

    public string Describe()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string gap = Difference == 0m
            ? "level with the list at the same exposure"
            : string.Create(c, $"{Math.Abs(Difference) * 100:0.00} points {(Difference > 0 ? "ahead" : "behind")} at the same exposure");
        string noise = TStat is not { } t ? string.Empty
            : Math.Abs(t) >= HoldTheList.TNoise ? string.Create(c, $" (t {t:+0.0;-0.0}: more than noise)")
            : string.Create(c, $" (t {t:+0.0;-0.0}: not yet more than noise)");
        return string.Create(c,
            $"{Days} day(s): Paper {PaperReturn:+0.00%;-0.00%;0.00%}; the list {ListReturn:+0.00%;-0.00%;0.00%} (at Paper's {Exposure:P0} invested {ListAtExposure:+0.00%;-0.00%;0.00%}): Paper {gap}{noise}");
    }
}

/// <summary>
/// Plan 24: does the strategy beat simply holding its own list? Each Paper day is compared with the shares on the list
/// that day held at equal weights from the previous Paper close to this one, at the same close prices Paper valued its
/// book with (the <c>close-mark</c> records), with their dividends, without costs.
/// </summary>
public static class HoldTheList
{
    /// <summary>Fewer days give no t-statistic: a week's difference is mostly noise.</summary>
    public const int MinDaysForT = 5;

    /// <summary>A difference is "more than noise" from |t| ≥ 2 (about 95 % two-sided).</summary>
    public const double TNoise = 2.0;

    /// <summary>
    /// The list's days: every Paper day with an account and close marks that has an earlier such day. A share counts on a
    /// day when it has a close on both days and no split-like move between them (plan 21's guard).
    /// </summary>
    /// <param name="dividends">Each share's dividends by orderbook id (any range); null leaves them out.</param>
    public static IReadOnlyList<ListDay> Days(IEnumerable<EodReport> reports, IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? dividends)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var result = new List<ListDay>();
        EodReport? previous = null;
        foreach (EodReport r in reports.Where(IsPaperWithMarks).OrderBy(r => r.Date))
        {
            if (previous is { Account: { } before } p)
            {
                Dictionary<string, EodCloseMark> from = p.CloseMarks.ToDictionary(m => m.OrderbookId, StringComparer.Ordinal);
                var returns = new List<decimal>();
                var leftOut = new List<string>();
                foreach (EodCloseMark end in r.CloseMarks)
                {
                    if (!from.TryGetValue(end.OrderbookId, out EodCloseMark? start))
                    {
                        leftOut.Add(string.Create(CultureInfo.InvariantCulture, $"{end.Ticker} (not on the list on {p.Date:yyyy-MM-dd})"));
                    }
                    else if (start.Close is not > 0m || end.Close is not > 0m)
                    {
                        leftOut.Add($"{end.Ticker} (no price at a close)");
                    }
                    else if (CorporateActions.SplitLikeMove(start.Close.Value, end.Close.Value) is not null)
                    {
                        leftOut.Add($"{end.Ticker} (split-like move)");
                    }
                    else
                    {
                        decimal startSek = start.Close.Value * start.SekPerUnit;
                        decimal endSek = end.Close.Value * end.SekPerUnit + DividendsSek(dividends, end, p.Date, r.Date);
                        returns.Add(endSek / startSek - 1m);
                    }
                }

                if (returns.Count > 0)
                {
                    decimal exposure = before.EndValue > 0 ? decimal.Round((before.EndValue - before.Cash) / before.EndValue, 4) : 0m;
                    result.Add(new ListDay(r.Date, p.Date, r.Account!.PnlPct, decimal.Round(returns.Average(), 6), exposure, returns.Count, leftOut));
                }
            }

            previous = r;
        }

        return result;
    }

    /// <summary>The days compounded; null without any.</summary>
    public static PaperVsList? Compare(IReadOnlyList<ListDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        if (days.Count == 0)
        {
            return null;
        }

        decimal paper = Compound(days.Select(d => d.PaperReturn));
        decimal list = Compound(days.Select(d => d.Return));
        decimal atExposure = Compound(days.Select(d => d.Exposure * d.Return));
        decimal exposure = decimal.Round(days.Average(d => d.Exposure), 4);
        return new PaperVsList(days.Count, paper, list, exposure, atExposure, TStat([.. days.Select(d => (double)(d.PaperReturn - d.Exposure * d.Return))]));
    }

    private static bool IsPaperWithMarks(EodReport r) =>
        r.Account is not null && r.CloseMarks.Count > 0 && r.Modes.Contains("Paper") && !r.Modes.Any(m => m is "Confirm" or "Auto");

    /// <summary>A share's dividends with an ex-date after <paramref name="from"/> and on or before <paramref name="to"/>, per share in SEK.</summary>
    private static decimal DividendsSek(IReadOnlyDictionary<string, IReadOnlyList<DividendEvent>>? dividends, EodCloseMark mark, DateOnly from, DateOnly to)
    {
        if (dividends is null || !dividends.TryGetValue(mark.OrderbookId, out IReadOnlyList<DividendEvent>? events))
        {
            return 0m;
        }

        // Paid in the share's own currency (or SEK); one in a third currency has no rate here and is left out.
        return events.Where(d => d.ExDate > from && d.ExDate <= to && d.Amount > 0)
            .Sum(d => d.Currency == "SEK" ? d.Amount : d.Currency == mark.Currency ? d.Amount * mark.SekPerUnit : 0m);
    }

    private static decimal Compound(IEnumerable<decimal> returns) => decimal.Round(returns.Aggregate(1m, (acc, x) => acc * (1 + x)) - 1, 6);

    private static double? TStat(double[] differences)
    {
        if (differences.Length < MinDaysForT)
        {
            return null;
        }

        double mean = differences.Average();
        double sd = Math.Sqrt(differences.Sum(x => (x - mean) * (x - mean)) / (differences.Length - 1));
        return sd > 0 ? Math.Round(mean / sd * Math.Sqrt(differences.Length), 2) : null;
    }
}
