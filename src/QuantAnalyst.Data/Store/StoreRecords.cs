using System.Globalization;
using System.Text;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Data.Store;

/// <summary>How an instrument trades. The Phase 5 backtest refuses <see cref="Unknown"/> (market-rules.md: First North auctions).</summary>
public enum TradingModel
{
    Unknown,
    Continuous,
    PeriodicAuction,
}

/// <summary>One version of an instrument master row (the attributes; <see cref="StoredInstrument"/> adds when it was known).</summary>
public sealed record InstrumentRecord(
    OrderbookId OrderbookId,
    string? Isin,
    string Ticker,
    string Name,
    string Currency,
    string MarketPlace,
    string InstrumentType,
    TradingModel TradingModel,
    decimal VolumeFactor,
    string TickTableJson,
    DateOnly ValidFrom)
{
    /// <summary>
    /// Builds the master row from the broker's orderbook parameters. Nasdaq Stockholm main market (XSTO) trades
    /// continuously, and so do the US and Canadian shares Avanza offers (ADR 0005: USD and CAD, listed on exchanges with
    /// continuous trading); anything else stays <see cref="TradingModel.Unknown"/> until classified.
    /// </summary>
    public static InstrumentRecord FromTradingParams(InstrumentTradingParams p) => new(
        p.OrderbookId,
        string.IsNullOrWhiteSpace(p.Isin) ? null : p.Isin,
        p.TickerSymbol ?? p.Name,
        p.Name,
        p.Currency,
        p.MarketPlace,
        p.InstrumentType,
        string.Equals(p.MarketPlace, "XSTO", StringComparison.Ordinal) || p.Currency is "USD" or "CAD" ? TradingModel.Continuous : TradingModel.Unknown,
        p.VolumeFactor,
        CanonicalTickTable(p.TickSizes),
        DateOnly.FromDateTime(MarketTime.ToStockholm(p.KnownAtUtc).DateTime));

    /// <summary>Canonical JSON of a tick table (invariant decimals, fixed key order) so equal tables compare equal.</summary>
    public static string CanonicalTickTable(TickSizeTable table)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < table.Bands.Count; i++)
        {
            TickSizeBand b = table.Bands[i];
            sb.Append(i == 0 ? string.Empty : ",")
              .Append(CultureInfo.InvariantCulture, $"{{\"min\":{b.Min.ToString(CultureInfo.InvariantCulture)},\"max\":{b.Max.ToString(CultureInfo.InvariantCulture)},\"tick\":{b.Tick.ToString(CultureInfo.InvariantCulture)}}}");
        }

        return sb.Append(']').ToString();
    }

    /// <summary>True when every attribute except <see cref="ValidFrom"/> is equal (a new version is only stored on a real change).</summary>
    public bool SameAttributes(InstrumentRecord other) => this with { ValidFrom = other.ValidFrom } == other;
}

public sealed record StoredInstrument(InstrumentRecord Instrument, DateTimeOffset KnownAtUtc, string Source, string SourceVersion);

public sealed record StoredBar(DailyBar Bar, DateTimeOffset KnownAtUtc, string Source, string SourceVersion);

/// <summary>One stored FX rate (ADR 0005) and when it became known.</summary>
public sealed record StoredFxRate(string Currency, FxRate Rate, DateTimeOffset KnownAtUtc, string Source, string SourceVersion);

/// <summary>One stored intraday bar (plan 17): its resolution and when it became known.</summary>
public sealed record StoredIntradayBar(Bar Bar, ChartResolution Resolution, DateTimeOffset KnownAtUtc, string Source, string SourceVersion);

/// <summary>
/// The best bid and ask of one instrument at one moment, as a running session saw them (plan 17: the backtest's spread
/// cost comes from these, not from a guess).
/// </summary>
public sealed record SpreadSample(OrderbookId OrderbookId, DateTimeOffset AtUtc, decimal Bid, decimal Ask, decimal BidVolume, decimal AskVolume)
{
    /// <summary>Gets the spread as a share of the mid (0.001 = 10 bps).</summary>
    public decimal RelativeSpread => (Ask - Bid) / ((Ask + Bid) / 2m);
}

/// <summary>What an append-only write did: rows new to the store, rows that changed (restatements) and unchanged rows.</summary>
public sealed record WriteCounts(int New, int Restated, int Unchanged);
