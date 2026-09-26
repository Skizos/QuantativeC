namespace QuantAnalyst.Core.Instruments;

/// <summary>One row of a tick-size table: prices from <see cref="Min"/> up to <see cref="Max"/> trade in steps of <see cref="Tick"/>.</summary>
public sealed record TickSizeBand(decimal Min, decimal Max, decimal Tick);

public enum TickRounding
{
    /// <summary>Largest valid price ≤ the input.</summary>
    Down,

    /// <summary>Smallest valid price ≥ the input.</summary>
    Up,

    /// <summary>Closest valid price; ties go down.</summary>
    Nearest,
}

/// <summary>
/// A per-instrument tick-size table (Avanza <c>orderbook/{id}</c> → <c>tickSizeList</c>). Valid prices are
/// positive multiples of the tick of the band that contains them. A price belongs to the last band whose
/// <see cref="TickSizeBand.Min"/> is ≤ the price, so both "shared edge" (max = next min) and "gap"
/// (max = next min − tick) table styles work. Prices above the last band's max are rejected.
/// </summary>
public sealed class TickSizeTable
{
    private readonly TickSizeBand[] _bands;

    public TickSizeTable(IEnumerable<TickSizeBand> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        _bands = [.. bands];
        if (_bands.Length == 0)
        {
            throw new ArgumentException("A tick-size table needs at least one band.", nameof(bands));
        }

        for (int i = 0; i < _bands.Length; i++)
        {
            TickSizeBand b = _bands[i];
            if (b.Tick <= 0m || b.Min < 0m || b.Max < b.Min)
            {
                throw new ArgumentException($"Invalid tick band {i}: min={b.Min}, max={b.Max}, tick={b.Tick}.", nameof(bands));
            }

            if (i > 0 && b.Min < _bands[i - 1].Max)
            {
                throw new ArgumentException(
                    $"Tick bands overlap or are unsorted at band {i}: min={b.Min} < previous max={_bands[i - 1].Max}.", nameof(bands));
            }
        }
    }

    public IReadOnlyList<TickSizeBand> Bands => _bands;

    /// <summary>The tick size that applies at <paramref name="price"/>.</summary>
    public decimal TickAt(decimal price) => BandAt(price).Tick;

    public bool IsOnTick(decimal price) => price > 0m && WithinTable(price) && price % TickAt(price) == 0m;

    /// <summary>
    /// Rounds a limit price the passive way: a buy rounds <b>down</b> and a sell rounds <b>up</b>, so the order
    /// is never more aggressive than requested (CLAUDE.md: round to tick before risk checks).
    /// </summary>
    public decimal RoundForOrder(decimal price, OrderSide side) =>
        Round(price, side == OrderSide.Buy ? TickRounding.Down : TickRounding.Up);

    public decimal Round(decimal price, TickRounding mode)
    {
        if (price <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), price, "Price must be positive.");
        }

        decimal result = mode switch
        {
            TickRounding.Down => RoundDown(price),
            TickRounding.Up => RoundUp(price),
            TickRounding.Nearest => Nearest(price),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

        return result > 0m
            ? result
            : throw new ArgumentOutOfRangeException(nameof(price), price, "Price is below the smallest valid tick.");
    }

    private decimal Nearest(decimal price)
    {
        decimal down = RoundDown(price);
        decimal up = RoundUp(price);
        return up - price < price - down ? up : down;
    }

    // Each step lands on the grid of the band that contains the new price; crossing into a band with a
    // different tick re-checks against that band. The loop ends within one pass per band.
    private decimal RoundDown(decimal price)
    {
        decimal r = price;
        for (int i = 0; i <= _bands.Length; i++)
        {
            if (r <= 0m)
            {
                return 0m;
            }

            decimal tick = TickAt(r);
            decimal down = decimal.Floor(r / tick) * tick;
            if (down == r)
            {
                return r;
            }

            r = down;
        }

        throw new InvalidOperationException($"Tick rounding did not converge for {price}.");
    }

    private decimal RoundUp(decimal price)
    {
        decimal r = price;
        for (int i = 0; i <= _bands.Length; i++)
        {
            decimal tick = TickAt(r);
            decimal up = decimal.Ceiling(r / tick) * tick;
            if (up == r)
            {
                return r;
            }

            r = up;
        }

        throw new InvalidOperationException($"Tick rounding did not converge for {price}.");
    }

    private bool WithinTable(decimal price) => price >= _bands[0].Min && price <= _bands[^1].Max;

    private TickSizeBand BandAt(decimal price)
    {
        if (!WithinTable(price))
        {
            throw new ArgumentOutOfRangeException(
                nameof(price), price, $"Price is outside the tick table [{_bands[0].Min}, {_bands[^1].Max}].");
        }

        TickSizeBand band = _bands[0];
        foreach (TickSizeBand b in _bands)
        {
            if (b.Min > price)
            {
                break;
            }

            band = b;
        }

        return band;
    }
}
