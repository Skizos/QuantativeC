using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Core.Orders;
using QuantAnalyst.Data.History;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Reports;

namespace QuantAnalyst.Trading.Tests;

/// <summary>Plan 21: dividends and splits in the Paper book, and the split guard.</summary>
public sealed class CorporateActionsTests : IDisposable
{
    private static readonly OrderbookId Eric = RiskEngineTests.Eric;
    private static readonly OrderbookId Nvda = new("4478");
    private static readonly DateOnly Today = new(2026, 9, 28); // RiskEngineTests.Now is 10:00 in Stockholm that day

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now);
    private readonly SettableQuotes _quotes = new();
    private readonly AuditLog _audit;

    public CorporateActionsTests() => _audit = new AuditLog(_dir.File("audit"), _time);

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(100, 200, 2.0)]
    [InlineData(100, 1000, 10.0)]
    [InlineData(100, 300.9, 3.0)] // within 0.5 %
    [InlineData(1000, 100, 0.1)] // a 1:10 reverse split
    [InlineData(100, 101, null)] // an issue
    [InlineData(100, 97, null)] // a buyback
    [InlineData(100, 150, null)] // not a clean ratio
    [InlineData(100, 305, null)] // 3 × 1.0167: not within 0.5 %
    [InlineData(100, 2100, null)] // beyond 20:1
    [InlineData(0, 100, null)]
    public void SplitRatio_IsACleanChangeInTheShareCount(double before, double after, double? expected) =>
        Assert.Equal(Rounded(expected), Rounded((double?)CorporateActions.SplitRatio((decimal)before, (decimal)after)));

    [Theory]
    [InlineData(100, 50, 0.5)]
    [InlineData(100, 46, 0.5)] // a 2:1 split and a bad day
    [InlineData(100, 34, 0.333333)]
    [InlineData(100, 10.2, 0.1)]
    [InlineData(100, 200, 2.0)] // a 1:2 reverse split
    [InlineData(100, 60, null)] // a large fall, not a clean ratio
    [InlineData(100, 70, null)]
    [InlineData(100, 130, null)]
    [InlineData(0, 50, null)]
    public void SplitLikeMove_IsACleanRatioFromTheLastCloseMark(double mark, double price, double? expected) =>
        Assert.Equal(Rounded(expected), Rounded((double?)CorporateActions.SplitLikeMove((decimal)mark, (decimal)price)));

    [Theory]
    [InlineData("2:1", 2.0, "2:1")]
    [InlineData("2", 2.0, "2:1")]
    [InlineData(" 10:1 ", 10.0, "10:1")]
    [InlineData("1:10", 0.1, "1:10")]
    [InlineData("1:3", 0.333333, "1:3")]
    public void ParseRatio_AndDescribe_RoundTrip(string text, double expected, string described)
    {
        decimal ratio = CorporateActions.ParseRatio(text);
        Assert.Equal(decimal.Round((decimal)expected, 6), decimal.Round(ratio, 6));
        Assert.Equal(described, CorporateActions.Describe(ratio));
    }

    [Theory]
    [InlineData("3:2")]
    [InlineData("1:1")]
    [InlineData("1")]
    [InlineData("21:1")]
    [InlineData("-2")]
    [InlineData("two")]
    [InlineData("2:1:1")]
    public void ParseRatio_RefusesAnythingButKForOneOrOneForK(string text) =>
        Assert.Throws<ArgumentException>(() => CorporateActions.ParseRatio(text));

    [Fact]
    public void ADividend_IsCreditedOnceOnItsExDate_AfterTheDayStartValueIsFixed()
    {
        PaperBook book = Holding(100, price: 100m);
        decimal cashBefore = book.Cash;
        CorporateSnapshot eric = Snapshot(Eric, [
            new DividendEvent(Today.AddDays(-3), null, 1.40m, "SEK", "ORDINARY"), // before the last session: not credited again
            new DividendEvent(Today, new DateOnly(2026, 10, 1), 1.45m, "SEK", "ORDINARY"),
            new DividendEvent(Today.AddDays(1), null, 9m, "SEK", "EXTRA"), // not yet
        ]);

        IReadOnlyList<string> said = CorporateActions.Apply(book, [eric], Today, _audit);

        Assert.Equal(cashBefore + 145m, book.Cash);
        Assert.Equal(145m, book.DividendsReceived);
        Assert.Equal(Today, book.CorporateThrough);
        Assert.Contains("dividend 1.45 SEK × 100 (ex-date 2026-09-28) = 145.00 SEK credited (gross; paid 2026-10-01)", Assert.Single(said), StringComparison.Ordinal);

        // The day starts at yesterday's close (before the credit), so the ex-date's drop, once priced, is no loss.
        AccountSnapshot a = book.Snapshot();
        Assert.Equal(cashBefore + 10_000m, a.StartOfDayValue);
        Assert.Equal(a.StartOfDayValue + 145m, a.AccountValue);
        _quotes.Set(Eric, _time.GetUtcNow(), 98.5m, 100, 98.6m, 100, 98.55m, 1000);
        Assert.Equal(a.StartOfDayValue, book.Snapshot().AccountValue);

        // A second session the same day credits nothing.
        Assert.Empty(CorporateActions.Apply(book, [eric], Today, _audit));
        Assert.Equal(145m, book.DividendsReceived);

        JsonElement record = Assert.Single(Records("dividend"));
        Assert.Equal((100, 145m, "2026-09-28", true), (record.GetProperty("quantity").GetInt64(), record.GetProperty("cashSek").GetDecimal(),
            record.GetProperty("exDate").GetString(), record.GetProperty("gross").GetBoolean()));
    }

    [Fact]
    public void Dividends_SinceTheLastSession_AreCredited_OnlyForSharesHeld()
    {
        PaperBook book = Holding(10, price: 100m);
        book.SetCorporateThrough(Today.AddDays(-4)); // the last session was on Thursday; Friday's ex-date was missed
        CorporateSnapshot eric = Snapshot(Eric, [new DividendEvent(Today.AddDays(-3), null, 2m, "SEK", "ORDINARY")]);
        CorporateSnapshot notHeld = Snapshot(new OrderbookId("5239"), [new DividendEvent(Today, null, 5m, "SEK", "ORDINARY")]);

        CorporateActions.Apply(book, [eric, notHeld], Today, _audit);

        Assert.Equal(20m, book.DividendsReceived);
        Assert.Single(Records("dividend"));
    }

    [Fact]
    public void AForeignDividend_IsCreditedAtTheDayRate_OrSaidWhenThereIsNone()
    {
        var fx = new FxTable(new Dictionary<string, decimal> { ["USD"] = 10m });
        PaperBook book = PaperBook.InMemory(100_000m, PaperOrderChannelTests.Mini.Name, _quotes, _time, fx);
        book.ApplyFill(Guid.NewGuid(), Nvda, "NVDA", OrderSide.Buy, 40, 180m, 1m, 0m, _time.GetUtcNow(), "USD", 10m);

        CorporateActions.Apply(book, [Snapshot(Nvda, [new DividendEvent(Today, null, 0.25m, "USD", "ORDINARY")])], Today, _audit);
        Assert.Equal(100m, book.DividendsReceived); // 40 × 0.25 USD × 10

        PaperBook noRate = PaperBook.InMemory(100_000m, PaperOrderChannelTests.Mini.Name, _quotes, _time, fx);
        noRate.ApplyFill(Guid.NewGuid(), Nvda, "NVDA", OrderSide.Buy, 40, 180m, 1m, 0m, _time.GetUtcNow(), "USD", 10m);
        IReadOnlyList<string> said = CorporateActions.Apply(noRate, [Snapshot(Nvda, [new DividendEvent(Today, null, 0.5m, "CAD", "ORDINARY")])], Today, _audit);
        Assert.Equal(0m, noRate.DividendsReceived);
        Assert.Contains("not credited: no CAD/SEK rate today", Assert.Single(said), StringComparison.Ordinal);
    }

    [Fact]
    public void AShareCountThatDoubled_SplitsThePosition_BeforeItsDividend()
    {
        PaperBook book = Holding(100, price: 100m);
        book.RecordCloseMarks();
        decimal cost = book.Positions[0].CostBasis;
        CorporateSnapshot eric = Snapshot(Eric, [new DividendEvent(Today, null, 0.5m, "SEK", "ORDINARY")], shares: 6_668_303_470m, previous: 3_334_151_735m);

        IReadOnlyList<string> said = CorporateActions.Apply(book, [eric], Today, _audit);

        PaperPosition p = Assert.Single(book.Positions);
        Assert.Equal((200L, cost, 50m, 50m), (p.Quantity, p.CostBasis, p.LastFillPrice, p.LastMark!.Value));
        Assert.Equal(100m, book.DividendsReceived); // 200 × 0.50, per current share
        Assert.Contains("split 2:1 (Avanza's share count went from 3,334,151,735 to 6,668,303,470); the position goes from 100 to 200", said[0], StringComparison.Ordinal);
        JsonElement split = Assert.Single(Records("split"));
        Assert.Equal(("share count", 100L, 200L), (split.GetProperty("source").GetString(), split.GetProperty("before").GetInt64(), split.GetProperty("after").GetInt64()));

        // The day started at yesterday's 100 × 100; the split is neither a gain nor a loss once the price is 50.
        _quotes.Set(Eric, _time.GetUtcNow(), 49.9m, 100, 50.1m, 100, 50m, 1000);
        AccountSnapshot a = book.Snapshot();
        Assert.Equal(a.StartOfDayValue + 100m, a.AccountValue);
        Assert.Empty(book.HeldBack);

        // A second session the same day reads the same count change: the position is not split again.
        Assert.Empty(CorporateActions.Apply(book, [eric], Today, _audit));
        Assert.Equal(200, book.Position(Eric));
    }

    [Fact]
    public void AReverseSplit_PaysTheFractionInCash()
    {
        PaperBook book = Holding(100, price: 10m);
        book.RecordCloseMarks();
        decimal cash = book.Cash;

        CorporateActions.Apply(book, [Snapshot(Eric, [], shares: 1_000m, previous: 3_000m)], Today, _audit);

        PaperPosition p = Assert.Single(book.Positions);
        Assert.Equal((33L, 30m), (p.Quantity, p.LastMark!.Value));
        Assert.Equal(cash + 10m, book.Cash); // ⅓ share at 30
    }

    [Fact]
    public void ASplitAppliedByHand_IsNotAppliedAgain_WhenTheShareCountShowsIt()
    {
        PaperBook book = Holding(100, price: 100m);
        string said = CorporateActions.SplitByOwner(book, Eric, 2m, _audit);
        Assert.Equal("ERIC B: split 2:1 applied; the position goes from 100 to 200.", said);
        Assert.Equal(2m, Assert.Single(book.SplitsByHand).Ratio);

        IReadOnlyList<string> next = CorporateActions.Apply(book, [Snapshot(Eric, [], shares: 200m, previous: 100m)], Today.AddDays(1), _audit);

        Assert.Equal(200, book.Position(Eric));
        Assert.Empty(book.SplitsByHand);
        Assert.Contains("now shows the 2:1 split applied by hand on 2026-09-28; not applied again", Assert.Single(next), StringComparison.Ordinal);
        Assert.Equal("owner", Assert.Single(Records("split")).GetProperty("source").GetString());
    }

    [Fact]
    public void TheSplitGuard_HoldsBackAShareAtASplitLikeRatio_ValuedAtItsLastCloseMark()
    {
        PaperBook book = Holding(100, price: 100m);
        book.RecordCloseMarks();
        _time.Advance(TimeSpan.FromDays(1));
        decimal cash = book.Cash;

        // Tuesday: the price halved and no split is known. The book values it at Monday's close and holds it back.
        _quotes.Set(Eric, _time.GetUtcNow(), 49.9m, 100, 50.1m, 100, 50m, 1000);
        AccountSnapshot a = book.Snapshot();
        Assert.Equal(cash + 10_000m, a.AccountValue);
        Assert.Equal(a.StartOfDayValue, a.AccountValue); // the daily loss stop sees no loss
        Assert.Equal([Eric], book.HeldBack);

        // At the close it keeps Monday's mark, so it is held back again tomorrow until the owner resolves it.
        book.RecordCloseMarks();
        Assert.Equal(100m, book.Positions[0].LastMark);

        // A price back in line releases it.
        _quotes.Set(Eric, _time.GetUtcNow(), 98.9m, 100, 99.1m, 100, 99m, 1000);
        Assert.Equal(cash + 9_900m, book.Snapshot().AccountValue);
        Assert.Empty(book.HeldBack);
    }

    [Fact]
    public void TheOwnerResolvesAHeldBackShare_ByASplit_OrByAcceptingThePrice()
    {
        PaperBook split = Holding(100, price: 100m);
        split.RecordCloseMarks();
        _quotes.Set(Eric, _time.GetUtcNow(), 49.9m, 100, 50.1m, 100, 50m, 1000);
        split.Snapshot();
        Assert.NotEmpty(split.HeldBack);
        CorporateActions.SplitByOwner(split, Eric, CorporateActions.ParseRatio("2:1"), _audit);
        Assert.Equal(split.Cash + 10_000m, split.Snapshot().AccountValue);
        Assert.Empty(split.HeldBack);

        var quotes = new SettableQuotes();
        PaperBook accepted = PaperBook.InMemory(50_000m, PaperOrderChannelTests.Mini.Name, quotes, _time);
        accepted.ApplyFill(Guid.NewGuid(), Eric, "ERIC B", OrderSide.Buy, 100, 100m, 1m, 0m, _time.GetUtcNow());
        quotes.Set(Eric, _time.GetUtcNow(), 99.9m, 100, 100.1m, 100, 100m, 1000);
        accepted.RecordCloseMarks();
        quotes.Set(Eric, _time.GetUtcNow(), 49.9m, 100, 50.1m, 100, 50m, 1000);
        accepted.Snapshot();
        Assert.Equal("ERIC B: its move is taken as real; the next session values and trades it at its live price.", CorporateActions.AcceptPriceByOwner(accepted, Eric, _audit));
        Assert.Equal(accepted.Cash + 5_000m, accepted.Snapshot().AccountValue);
        Assert.Empty(accepted.HeldBack);
        Assert.Single(Records("price-accepted"));
        Assert.Throws<ArgumentException>(() => CorporateActions.AcceptPriceByOwner(accepted, new OrderbookId("5239"), _audit));
    }

    [Fact]
    public void TheBook_KeepsMarksDividendsAndHandSplits_AcrossSessions()
    {
        string dir = _dir.File("paper");
        var config = new PaperConfig(PaperOrderChannelTests.Mini.Name, 50_000m, new TimeOnly(9, 10));
        PaperBook book = PaperBook.OpenOrCreate(dir, config, _quotes, _time, out _);
        book.ApplyFill(Guid.NewGuid(), Eric, "ERIC B", OrderSide.Buy, 100, 100m, 1m, 0m, _time.GetUtcNow());
        _quotes.Set(Eric, _time.GetUtcNow(), 99.9m, 100, 100.1m, 100, 100m, 1000);
        book.RecordCloseMarks();
        CorporateActions.Apply(book, [Snapshot(Eric, [new DividendEvent(Today, null, 1m, "SEK", "ORDINARY")])], Today, _audit);
        CorporateActions.SplitByOwner(book, Eric, 2m, _audit);

        PaperBook again = PaperBook.OpenOrCreate(dir, config, _quotes, _time, out _);
        Assert.Equal((Today, 100m), (again.CorporateThrough!.Value, again.DividendsReceived));
        Assert.Equal((200L, 50m), (again.Positions[0].Quantity, again.Positions[0].LastMark!.Value));
        Assert.Equal(new SplitByHand(Eric, 2m, Today), Assert.Single(again.SplitsByHand));
    }

    [Fact]
    public void TheEndOfDayReport_ListsTheDividendsAndSplits_AndTheSummaryShowsTheTotal()
    {
        PaperBook book = Holding(100, price: 100m);
        book.RecordCloseMarks();
        CorporateActions.Apply(book, [Snapshot(Eric, [new DividendEvent(Today, null, 0.5m, "SEK", "ORDINARY")], shares: 200m, previous: 100m)], Today, _audit);
        _audit.Append("session-start", new { mode = "Paper" });

        EodReport r = EodReport.Build(_audit.Directory, Today, _time);

        Assert.Equal(["split", "dividend"], r.CorporateActions.Select(a => a.Kind));
        Assert.Equal("ERIC B: split 2:1 (from Avanza's share count), 100 → 200 shares", r.CorporateActions[0].Text);
        Assert.Equal("ERIC B: dividend 0.5 SEK × 200 (ex-date 2026-09-28) = 100.00 SEK, gross", r.CorporateActions[1].Text);
        Assert.Equal(100m, r.DividendsSek);
        Assert.Contains("Corporate actions: 1 dividend(s), 100.00 SEK, 1 split(s).", r.Summary(), StringComparison.Ordinal);
        Assert.Equal(r.CorporateActions, EodReport.Load(r.Save(_dir.File("reports"))).CorporateActions);
    }

    private static decimal? Rounded(double? x) => x is { } v ? decimal.Round((decimal)v, 6) : null;

    /// <summary>A book holding <paramref name="quantity"/> ERIC B bought at <paramref name="price"/>, quoted at that price.</summary>
    private PaperBook Holding(long quantity, decimal price)
    {
        PaperBook book = PaperBook.InMemory(50_000m, PaperOrderChannelTests.Mini.Name, _quotes, _time);
        book.ApplyFill(Guid.NewGuid(), Eric, "ERIC B", OrderSide.Buy, quantity, price, 1m, 0m, _time.GetUtcNow());
        _quotes.Set(Eric, _time.GetUtcNow(), price - 0.1m, 100, price + 0.1m, 100, price, 1000);
        return book;
    }

    private CorporateSnapshot Snapshot(OrderbookId id, IReadOnlyList<DividendEvent> dividends, decimal? shares = null, decimal? previous = null) =>
        new(new CorporateData(id, dividends, shares, _time.GetUtcNow()), previous);

    private IEnumerable<JsonElement> Records(string kind) =>
        Directory.EnumerateFiles(_audit.Directory, "*.jsonl").Order(StringComparer.Ordinal)
            .SelectMany(AuditLog.Read)
            .Where(r => r.GetProperty("kind").GetString() == kind)
            .Select(r => r.GetProperty("data"));
}
