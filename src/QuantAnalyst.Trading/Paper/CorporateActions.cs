using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Trading.Audit;

namespace QuantAnalyst.Trading.Paper;

/// <summary>
/// Plan 21: dividends and splits in the Paper book, applied at a session's start, <b>after</b> the day's start value is
/// fixed (R19) at the prices before them: the start value is yesterday's, and the day's return is the total return (the
/// ex-date's price drop is matched by the cash, a split changes neither the value nor the return).
/// <list type="bullet">
/// <item>Splits first (dividend amounts are per current share): a held share whose count of shares changed by a clean
/// ratio since the last stored count is split in the book, unless the owner already split it by hand.</item>
/// <item>Dividends: every ex-date after the last session's day and up to today, on the positions held (they cannot have
/// changed between sessions), gross, on the ex-date.</item>
/// </list>
/// Each is audited (<c>split</c>, <c>dividend</c>), so the end-of-day report shows it.
/// </summary>
public static class CorporateActions
{
    /// <summary>The largest split ratio recognised (k:1 or 1:k).</summary>
    public const int MaxRatio = 20;

    /// <summary>A split applied by hand is taken as the one Avanza's share count shows within this many days.</summary>
    public const int SplitByHandDays = 30;

    /// <summary>
    /// A share count that changed by a clean k (2…20, within 0.5 %) is a k:1 split; by a clean 1/k, a 1:k reverse split.
    /// Returns new shares per old share, or null. A buyback or an issue moves the count by far less.
    /// </summary>
    public static decimal? SplitRatio(decimal sharesBefore, decimal sharesAfter) => CleanRatio(sharesBefore, sharesAfter, 0.005m, 1.5m);

    /// <summary>
    /// The split guard: a live price that is a clean ratio (within 10 %) of the last close mark, beyond ±40 %, is a
    /// split-like move; returns the ratio the price moved by (0.5 for halved), or null. A normal day never moves that far.
    /// </summary>
    public static decimal? SplitLikeMove(decimal lastMark, decimal price)
    {
        // The price moves the other way: halved price = 2:1 split. CleanRatio(price, lastMark) gives new shares per old.
        return lastMark > 0 && price > 0 && CleanRatio(price, lastMark, 0.10m, 1.65m) is { } shares ? 1 / shares : null;
    }

    /// <summary>"2:1" for 2 new shares per old, "1:10" for a 1:10 reverse split.</summary>
    public static string Describe(decimal ratio) =>
        ratio >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{ratio:0}:1") : string.Create(CultureInfo.InvariantCulture, $"1:{1 / ratio:0}");

    /// <summary>Parses "2:1", "2" (both 2 new per old) or "1:10" (a reverse split).</summary>
    public static decimal ParseRatio(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] parts = text.Trim().Split(':');
        CultureInfo c = CultureInfo.InvariantCulture;
        int a = 0, b = 1;
        bool ok = parts.Length switch
        {
            1 => int.TryParse(parts[0], NumberStyles.None, c, out a) && a is >= 2 and <= MaxRatio,
            2 => int.TryParse(parts[0], NumberStyles.None, c, out a) && int.TryParse(parts[1], NumberStyles.None, c, out b)
                 && ((b == 1 && a is >= 2 and <= MaxRatio) || (a == 1 && b is >= 2 and <= MaxRatio)),
            _ => false,
        };
        return ok
            ? (decimal)a / b
            : throw new ArgumentException($"'{text}' is not a split ratio such as 2:1 (two new shares per old) or 1:10 (a reverse split), up to {MaxRatio}.");
    }

    /// <summary>Applies the day's splits and dividends to <paramref name="book"/>; returns one line per action for the session log.</summary>
    public static IReadOnlyList<string> Apply(PaperBook book, IReadOnlyList<CorporateSnapshot> snapshots, DateOnly today, AuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(audit);
        CultureInfo c = CultureInfo.InvariantCulture;
        var said = new List<string>();
        _ = book.Snapshot(); // fixes the day's start value (R19) at the prices before today's actions, if not fixed yet
        DateOnly since = book.CorporateThrough ?? today.AddDays(-1);
        bool splitsDue = since < today; // a second session the same day sees the same count change: it is applied once
        foreach (CorporateSnapshot s in snapshots)
        {
            OrderbookId id = s.Data.OrderbookId;
            PaperPosition? held = book.Positions.FirstOrDefault(p => p.OrderbookId == id);
            if (held is null)
            {
                continue;
            }

            if (splitsDue && s.PreviousShares is { } before && s.Data.SharesOutstanding is { } after && SplitRatio(before, after) is { } ratio
                && !SplitAlreadyByHand(book, id, ratio, today, held.Ticker, said))
            {
                (long was, long now, decimal cash) = book.ApplySplit(id, ratio);
                audit.Append("split", new { orderbookId = id.Value, held.Ticker, ratio, before = was, after = now, cashForFraction = cash, source = "share count", sharesBefore = before, sharesAfter = after });
                said.Add(string.Create(c,
                    $"{held.Ticker}: split {Describe(ratio)} (Avanza's share count went from {before:N0} to {after:N0}); the position goes from {was} to {now}{(cash > 0 ? $", {cash:N2} SEK for the fraction" : string.Empty)}."));
                held = book.Positions.FirstOrDefault(p => p.OrderbookId == id);
                if (held is null)
                {
                    continue;
                }
            }

            foreach (DividendEvent d in s.Data.Dividends.Where(d => d.ExDate > since && d.ExDate <= today && d.Amount > 0))
            {
                decimal? rate = d.Currency == "SEK" ? 1m : book.Fx.SekPerUnit(d.Currency);
                if (rate is not { } sekPerUnit)
                {
                    said.Add(string.Create(c, $"{held.Ticker}: the dividend of {d.Amount} {d.Currency} (ex-date {d.ExDate:yyyy-MM-dd}) is not credited: no {d.Currency}/SEK rate today."));
                    continue;
                }

                decimal cash = book.CreditDividend(id, d.Amount, sekPerUnit);
                audit.Append("dividend", new
                {
                    orderbookId = id.Value,
                    held.Ticker,
                    exDate = d.ExDate,
                    paymentDate = d.PaymentDate,
                    amountPerShare = d.Amount,
                    d.Currency,
                    quantity = held.Quantity,
                    cashSek = cash,
                    gross = true,
                });
                said.Add(string.Create(c,
                    $"{held.Ticker}: dividend {d.Amount} {d.Currency} × {held.Quantity} (ex-date {d.ExDate:yyyy-MM-dd}) = {cash:N2} SEK credited (gross; paid {(d.PaymentDate is { } p ? p.ToString("yyyy-MM-dd", c) : "on a date not yet announced")})."));
            }
        }

        book.SetCorporateThrough(today);
        return said;
    }

    /// <summary>Plan 21: a split the owner checked (<c>qa paper split</c>), applied to the book and audited.</summary>
    public static string SplitByOwner(PaperBook book, OrderbookId id, decimal ratio, AuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(audit);
        PaperPosition held = book.Positions.FirstOrDefault(p => p.OrderbookId == id) ?? throw new ArgumentException($"The paper book does not hold {id}.");
        (long was, long now, decimal cash) = book.ApplySplit(id, ratio, byHand: true);
        audit.Append("split", new { orderbookId = id.Value, held.Ticker, ratio, before = was, after = now, cashForFraction = cash, source = "owner" });
        return string.Create(CultureInfo.InvariantCulture,
            $"{held.Ticker}: split {Describe(ratio)} applied; the position goes from {was} to {now}{(cash > 0 ? $", {cash:N2} SEK for the fraction" : string.Empty)}.");
    }

    /// <summary>Plan 21: the owner says a held-back share's move is real (<c>qa paper accept-price</c>); audited.</summary>
    public static string AcceptPriceByOwner(PaperBook book, OrderbookId id, AuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(audit);
        PaperPosition held = book.Positions.FirstOrDefault(p => p.OrderbookId == id) ?? throw new ArgumentException($"The paper book does not hold {id}.");
        if (!book.AcceptPrice(id))
        {
            return $"{held.Ticker} has no close mark to compare with: nothing to accept.";
        }

        audit.Append("price-accepted", new { orderbookId = id.Value, held.Ticker, lastMark = held.LastMark, source = "owner" });
        return $"{held.Ticker}: its move is taken as real; the next session values and trades it at its live price.";
    }

    /// <summary>
    /// True when the owner split <paramref name="id"/> by hand in the last <see cref="SplitByHandDays"/> days: the split
    /// Avanza's count now shows is that one, so it is not applied again (a different ratio is not applied either: the
    /// owner checks it). The hand split is forgotten either way.
    /// </summary>
    private static bool SplitAlreadyByHand(PaperBook book, OrderbookId id, decimal ratio, DateOnly today, string ticker, List<string> said)
    {
        if (book.TakeSplitByHand(id) is not { } byHand || byHand.Day < today.AddDays(-SplitByHandDays))
        {
            return false;
        }

        said.Add(byHand.Ratio == ratio
            ? $"{ticker}: Avanza's share count now shows the {Describe(ratio)} split applied by hand on {byHand.Day:yyyy-MM-dd}; not applied again."
            : $"{ticker}: Avanza's share count shows a {Describe(ratio)} split, but {Describe(byHand.Ratio)} was applied by hand on {byHand.Day:yyyy-MM-dd}: not applied. Check the position; the price guard holds it back if it is wrong.");
        return true;
    }

    private static decimal? CleanRatio(decimal before, decimal after, decimal tolerance, decimal threshold)
    {
        if (before <= 0 || after <= 0)
        {
            return null;
        }

        decimal r = after / before;
        if (r >= threshold)
        {
            decimal k = decimal.Round(r);
            return k is >= 2 and <= MaxRatio && Math.Abs((r / k) - 1) <= tolerance ? k : null;
        }

        if (r <= 1 / threshold)
        {
            decimal k = decimal.Round(1 / r);
            return k is >= 2 and <= MaxRatio && Math.Abs((r * k) - 1) <= tolerance ? 1 / k : null;
        }

        return null;
    }
}
