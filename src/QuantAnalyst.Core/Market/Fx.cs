namespace QuantAnalyst.Core.Market;

/// <summary>One day's exchange rate: how many SEK one unit of the currency cost (the Riksbank's fixing, ADR 0005).</summary>
public readonly record struct FxRate(DateOnly Date, decimal SekPerUnit);

/// <summary>FX rates could not be read (the source is unreachable, refused or answered something unexpected).</summary>
public sealed class FxUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A source of daily FX rates (the Riksbank's fixing; tests pass a fake).</summary>
public interface IFxRateSource
{
    /// <summary>Gets the labels stored with every rate from this source.</summary>
    DataSourceInfo Source { get; }

    /// <summary>Gets the version stored with every rate, e.g. the API version.</summary>
    string SourceVersion { get; }

    /// <summary>Daily rates for <paramref name="currency"/> from <paramref name="first"/> to <paramref name="last"/> (inclusive), oldest first; days without a fixing are absent.</summary>
    Task<IReadOnlyList<FxRate>> GetDailyAsync(string currency, DateOnly first, DateOnly last, CancellationToken ct);
}
