using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Data.History;

/// <summary>What the stored prices do on one ex-date (plan 21).</summary>
/// <param name="Yield">The dividend over the close before the ex-date.</param>
/// <param name="Gap">The ex-date's open over the close before it, minus one: about −<see cref="Yield"/> in a price-only series, about 0 in one adjusted for dividends.</param>
/// <param name="Reading">"price-only", "adjusted", or why the day can't be read.</param>
public sealed record DividendGap(DividendEvent Dividend, decimal? PreviousClose, decimal? ExOpen, decimal? Yield, decimal? Gap, string Reading);

/// <summary>What a share's stored daily history says about dividends.</summary>
public enum DividendVerdict
{
    /// <summary>No ex-date could be read (none past, no bars around them, or too small to see).</summary>
    NoData,

    /// <summary>The prices drop by about the dividend on ex-dates: a backtest on them leaves the dividends out.</summary>
    PriceOnly,

    /// <summary>The prices do not drop on ex-dates: the history is adjusted for dividends (a total-return series).</summary>
    Adjusted,

    /// <summary>The ex-dates disagree.</summary>
    Unclear,
}

/// <summary>
/// Plan 21: does a backtest on the stored history include dividends? On each past ex-date the overnight gap (the
/// ex-date's open against the close before) is compared with the dividend's yield: a price-only series gaps down by
/// about the yield, a dividend-adjusted one does not. The overnight gap is used, not the day's return, because the
/// rest of the day's move is noise of the same size as a dividend.
/// </summary>
public static class DividendCheck
{
    /// <summary>A dividend below this share of the price is lost in the noise of a normal overnight gap.</summary>
    public const decimal MinYield = 0.005m;

    public static (IReadOnlyList<DividendGap> Gaps, DividendVerdict Verdict) Check(IReadOnlyList<DividendEvent> dividends, IReadOnlyList<DailyBar> bars, string currency)
    {
        ArgumentNullException.ThrowIfNull(dividends);
        ArgumentNullException.ThrowIfNull(bars);
        DailyBar[] ordered = [.. bars.OrderBy(b => b.Date)];
        DateOnly? last = ordered.Length == 0 ? null : ordered[^1].Date;
        var gaps = new List<DividendGap>();
        foreach (DividendEvent d in dividends.OrderBy(d => d.ExDate))
        {
            int at = Array.FindIndex(ordered, b => b.Date == d.ExDate);
            if (last is { } end && d.ExDate > end)
            {
                gaps.Add(new DividendGap(d, null, null, null, null, "not yet in the history"));
                continue;
            }

            if (at <= 0)
            {
                gaps.Add(new DividendGap(d, null, null, null, null, "no bars around it"));
                continue;
            }

            decimal previous = ordered[at - 1].Close, open = ordered[at].Open;
            if (d.Currency != currency)
            {
                gaps.Add(new DividendGap(d, previous, open, null, null, $"paid in {d.Currency}, the share trades in {currency}"));
                continue;
            }

            decimal yield = decimal.Round(d.Amount / previous, 6);
            decimal gap = decimal.Round((open / previous) - 1, 6);
            string reading = yield < MinYield ? "too small to see"
                : Math.Abs(gap + yield) < Math.Abs(gap) ? "price-only"
                : "adjusted";
            gaps.Add(new DividendGap(d, previous, open, yield, gap, reading));
        }

        int priceOnly = gaps.Count(g => g.Reading == "price-only"), adjusted = gaps.Count(g => g.Reading == "adjusted");
        DividendVerdict verdict = priceOnly + adjusted == 0 ? DividendVerdict.NoData
            : priceOnly >= 2 * adjusted ? DividendVerdict.PriceOnly
            : adjusted >= 2 * priceOnly ? DividendVerdict.Adjusted
            : DividendVerdict.Unclear;
        return (gaps, verdict);
    }

    /// <summary>What the verdict means for the backtest, in a sentence.</summary>
    public static string Describe(DividendVerdict verdict) => verdict switch
    {
        DividendVerdict.PriceOnly => "The stored history is price-only: it drops by about the dividend on ex-dates, so a backtest on it leaves the dividends out (its returns are too low by about the yield). The Paper book credits them.",
        DividendVerdict.Adjusted => "The stored history is adjusted for dividends: it does not drop on ex-dates, so a backtest on it includes them.",
        DividendVerdict.Unclear => "The ex-dates disagree: the history can't be read either way.",
        _ => "No past ex-date could be read against the stored history.",
    };
}
