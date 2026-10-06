using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Desktop.Core.Charts;

/// <summary>
/// One candle: the open, high, low and close of its period, which starts at <see cref="At"/>. <see cref="Volume"/> is
/// null when it is not known (candles built from a session's quotes).
/// </summary>
public readonly record struct Candle(DateTimeOffset At, double Open, double High, double Low, double Close, long? Volume)
{
    /// <summary>Gets a value indicating whether the candle closed at or above its open (drawn green).</summary>
    public bool IsUp => Close >= Open;
}

public enum CandleUnit
{
    Minute,
    Day,
    Week,
    Month,
}

/// <summary>
/// A candle length as the buttons above the chart offer it (docs/plans/12-charts-window.md): Day, Week or Month for the
/// stored history; 1, 5 or 15 minutes for today's session. Periods start in Stockholm time: days at midnight, weeks on
/// Monday, months on the 1st, minutes on the whole minute.
/// </summary>
public sealed record CandlePeriod(string Label, CandleUnit Unit, int Minutes = 0)
{
    public static CandlePeriod Day { get; } = new("Day", CandleUnit.Day);

    public static CandlePeriod Week { get; } = new("Week", CandleUnit.Week);

    public static CandlePeriod Month { get; } = new("Month", CandleUnit.Month);

    public static CandlePeriod OneMinute { get; } = new("1 min", CandleUnit.Minute, 1);

    public static CandlePeriod FiveMinutes { get; } = new("5 min", CandleUnit.Minute, 5);

    public static CandlePeriod FifteenMinutes { get; } = new("15 min", CandleUnit.Minute, 15);

    /// <summary>Gets the periods of the stored daily history.</summary>
    public static IReadOnlyList<CandlePeriod> Daily { get; } = [Day, Week, Month];

    /// <summary>Gets the periods of today's candles.</summary>
    public static IReadOnlyList<CandlePeriod> Intraday { get; } = [OneMinute, FiveMinutes, FifteenMinutes];

    public bool IsIntraday => Unit == CandleUnit.Minute;

    /// <summary>The start of the period <paramref name="at"/> falls in.</summary>
    public DateTimeOffset StartOf(DateTimeOffset at)
    {
        if (Unit == CandleUnit.Minute)
        {
            // Stockholm is a whole number of hours from UTC, so minute periods start at the same instants in both.
            long ticks = TimeSpan.TicksPerMinute * Math.Max(1, Minutes);
            long utc = at.UtcTicks;
            return new DateTimeOffset(utc - (utc % ticks), TimeSpan.Zero);
        }

        DateOnly date = DateOnly.FromDateTime(MarketTime.ToStockholm(at).DateTime);
        return Midnight(Unit switch
        {
            CandleUnit.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
            CandleUnit.Month => new DateOnly(date.Year, date.Month, 1),
            _ => date,
        });
    }

    /// <summary>The start of the period after the one that starts at <paramref name="start"/>.</summary>
    public DateTimeOffset Next(DateTimeOffset start)
    {
        if (Unit == CandleUnit.Minute)
        {
            return start.AddMinutes(Math.Max(1, Minutes));
        }

        DateOnly date = DateOnly.FromDateTime(MarketTime.ToStockholm(start).DateTime);
        return Midnight(Unit switch
        {
            CandleUnit.Week => date.AddDays(7),
            CandleUnit.Month => date.AddMonths(1),
            _ => date.AddDays(1),
        });
    }

    /// <summary>Midnight in Stockholm at the start of <paramref name="date"/>, as a UTC instant.</summary>
    public static DateTimeOffset Midnight(DateOnly date) =>
        MarketTime.TryStockholmToUtc(date.ToDateTime(TimeOnly.MinValue), out DateTimeOffset utc)
            ? utc
            : new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    public override string ToString() => Label;
}

/// <summary>Building candles: from the store's daily bars, into longer periods, and from a session's quotes.</summary>
public static class Candles
{
    /// <summary>The store's daily bars as day candles, each starting at its date's midnight (Stockholm).</summary>
    public static IReadOnlyList<Candle> FromDaily(IEnumerable<DailyBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        return
        [
            .. bars.OrderBy(b => b.Date)
                .Select(b => new Candle(CandlePeriod.Midnight(b.Date), (double)b.Open, (double)b.High, (double)b.Low, (double)b.Close, b.Volume)),
        ];
    }

    /// <summary>
    /// Shorter candles (oldest first) combined into <paramref name="period"/>: the first open, the highest high, the
    /// lowest low, the last close and the summed volume (unknown when any part's is). Each starts at its period's start.
    /// </summary>
    public static IReadOnlyList<Candle> Aggregate(IReadOnlyList<Candle> candles, CandlePeriod period)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(period);
        var result = new List<Candle>();
        foreach (Candle c in candles)
        {
            DateTimeOffset start = period.StartOf(c.At);
            if (result.Count > 0 && result[^1].At == start)
            {
                Candle last = result[^1];
                result[^1] = last with
                {
                    High = Math.Max(last.High, c.High),
                    Low = Math.Min(last.Low, c.Low),
                    Close = c.Close,
                    Volume = last.Volume is { } v && c.Volume is { } w ? v + w : null,
                };
            }
            else
            {
                result.Add(c with { At = start });
            }
        }

        return result;
    }

    /// <summary>The closes as points (for moving averages over the candles).</summary>
    public static IReadOnlyList<ChartPoint> Closes(IReadOnlyList<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        return [.. candles.Select(c => new ChartPoint(c.At, c.Close))];
    }
}

/// <summary>
/// One-minute candles from a stream of prices (a Paper session's quotes, about one a second per name): the first price
/// of a minute opens its candle, every price moves its high, low and close. A price older than the last candle's
/// minute updates its own minute's candle if there is one, else it is dropped.
/// </summary>
public sealed class CandleBuilder
{
    private readonly List<Candle> _candles = [];

    public IReadOnlyList<Candle> Candles => _candles;

    public void Add(DateTimeOffset at, double price)
    {
        if (double.IsNaN(price) || double.IsInfinity(price))
        {
            return;
        }

        DateTimeOffset start = CandlePeriod.OneMinute.StartOf(at);
        int index = _candles.Count > 0 && _candles[^1].At == start ? _candles.Count - 1 : -1;
        if (index < 0 && _candles.Count > 0 && start < _candles[^1].At)
        {
            index = _candles.FindLastIndex(c => c.At == start);
            if (index < 0)
            {
                return;
            }
        }

        if (index < 0)
        {
            _candles.Add(new Candle(start, price, price, price, price, null));
            return;
        }

        Candle c = _candles[index];
        _candles[index] = c with { High = Math.Max(c.High, price), Low = Math.Min(c.Low, price), Close = index == _candles.Count - 1 ? price : c.Close };
    }

    public void Clear() => _candles.Clear();
}
