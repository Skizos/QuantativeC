using System.Globalization;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Trading.Model;

/// <summary>
/// SEK per unit of each currency the account trades shares in, at one moment (ADR 0005): the money side of the trading
/// core (limits, cash, positions, fees) is SEK; prices stay in the share's currency. SEK is always 1.
/// </summary>
public interface IFxRates
{
    /// <summary>SEK per unit of <paramref name="currency"/>, or null when no rate is known.</summary>
    decimal? SekPerUnit(string currency);
}

/// <summary>A fixed set of rates (a Paper session uses the latest Riksbank fixing at its start, ADR 0005).</summary>
public sealed class FxTable : IFxRates
{
    private readonly Dictionary<string, decimal> _rates;

    public FxTable(IReadOnlyDictionary<string, decimal> rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        foreach ((string currency, decimal rate) in rates)
        {
            if (rate <= 0m)
            {
                throw new ArgumentException($"{currency}: the rate must be positive, not {rate}.", nameof(rates));
            }
        }

        _rates = new Dictionary<string, decimal>(rates, StringComparer.Ordinal);
    }

    /// <summary>Gets a table that knows SEK only (a Swedish-only list).</summary>
    public static FxTable SekOnly { get; } = new(new Dictionary<string, decimal>());

    /// <summary>Gets the rates besides SEK, for the audit and the report.</summary>
    public IReadOnlyDictionary<string, decimal> Rates => _rates;

    public decimal? SekPerUnit(string currency) =>
        !Markets.IsForeign(currency) ? 1m : _rates.TryGetValue(currency, out decimal rate) ? rate : null;

    public override string ToString() =>
        _rates.Count == 0 ? "SEK only" : string.Join(", ", _rates.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Key} {r.Value:0.0000} SEK")));
}
