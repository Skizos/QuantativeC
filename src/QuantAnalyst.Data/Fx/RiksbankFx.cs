using System.Globalization;
using System.Net;
using System.Text.Json;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Data.Fx;

/// <summary>
/// The Riksbank's daily FX fixing (ADR 0005): SWEA API v1, <c>GET /Observations/{seriesId}/{from}/{to}</c> answering
/// <c>[{"date":"yyyy-MM-dd","value":9.4123}, …]</c> in SEK per unit, series <c>SEK&lt;CCY&gt;PMI</c>. Keyless (the Riksbank
/// limits keyless calls; the program asks once per import). An unknown series answers 204 No Content. Research: the
/// thpe/riksbank and pipeworx-io/mcp-riksbank-se clients cited in ADR 0005. The answer is read strictly: a shape other
/// than an array of <c>{date, value}</c>, a rate outside a sane range, or a date twice is refused, never guessed.
/// </summary>
public sealed class RiksbankFxSource : IFxRateSource
{
    public static readonly Uri BaseAddress = new("https://api.riksbank.se/swea/v1/");

    /// <summary>SEK per USD or CAD outside this range is not a per-unit fixing (the Riksbank quotes some currencies per 100).</summary>
    public const decimal MinSane = 1m;

    public const decimal MaxSane = 100m;

    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QuantAnalyst/1.0 (personal research tool)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    });

    private readonly HttpClient _http;

    /// <param name="http">A client whose <see cref="HttpClient.BaseAddress"/> is <see cref="BaseAddress"/> (tests pass a fake handler).</param>
    public RiksbankFxSource(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Gets the source the program uses: one shared HTTP client for the process.</summary>
    public static RiksbankFxSource Shared { get; } = new(SharedClient.Value);

    public static DataSourceInfo Riksbank { get; } = new(
        "riksbank-fixing",
        PointInTime: true,
        SurvivorshipFree: true,
        "Sveriges Riksbank daily FX fixing (SWEA API v1, series SEK<CCY>PMI), SEK per unit, published around 16:15 Stockholm (ADR 0005).");

    public DataSourceInfo Source => Riksbank;

    public string SourceVersion => "swea-v1";

    /// <summary>The fixing series of a currency: USD → SEKUSDPMI.</summary>
    public static string SeriesId(string currency) =>
        currency is { Length: 3 } && currency.All(char.IsAsciiLetterUpper)
            ? $"SEK{currency}PMI"
            : throw new ArgumentException($"'{currency}' is not a three-letter currency code.", nameof(currency));

    public async Task<IReadOnlyList<FxRate>> GetDailyAsync(string currency, DateOnly first, DateOnly last, CancellationToken ct)
    {
        string series = SeriesId(currency);
        if (last < first)
        {
            throw new ArgumentException("The last date is before the first.", nameof(last));
        }

        string path = string.Create(CultureInfo.InvariantCulture, $"Observations/{series}/{first:yyyy-MM-dd}/{last:yyyy-MM-dd}");
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(new Uri(path, UriKind.Relative), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new FxUnavailableException($"The Riksbank's FX rates could not be read ({ex.Message}).", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new FxUnavailableException("The Riksbank did not answer within 30 seconds.", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.NoContent:
                    return []; // no fixing in the range, or no such series
                case HttpStatusCode.TooManyRequests:
                    throw new FxUnavailableException("The Riksbank limits calls without a key; try again in a minute.");
                case var code when (int)code is < 200 or >= 300:
                    throw new FxUnavailableException(string.Create(CultureInfo.InvariantCulture, $"The Riksbank answered HTTP {(int)code} for {series}."));
            }

            byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return Parse(body, currency, series);
        }
    }

    /// <summary>Reads an Observations answer strictly (see the class remarks).</summary>
    public static IReadOnlyList<FxRate> Parse(ReadOnlySpan<byte> body, string currency, string series)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw Unexpected(series, "the answer is not a list");
            }

            var rates = new List<FxRate>();
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object
                    || !e.TryGetProperty("date", out JsonElement date) || date.ValueKind != JsonValueKind.String
                    || !e.TryGetProperty("value", out JsonElement value))
                {
                    throw Unexpected(series, "an observation has no date or value");
                }

                if (!DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
                {
                    throw Unexpected(series, $"'{date.GetString()}' is not a yyyy-MM-dd date");
                }

                if (value.ValueKind == JsonValueKind.Null)
                {
                    continue; // a day without a fixing
                }

                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out decimal v))
                {
                    throw Unexpected(series, $"the value on {d:yyyy-MM-dd} is not a number");
                }

                if (v is < MinSane or > MaxSane)
                {
                    throw Unexpected(series, string.Create(CultureInfo.InvariantCulture, $"{v} SEK per {currency} on {d:yyyy-MM-dd} is outside {MinSane}–{MaxSane}; not a per-unit fixing"));
                }

                rates.Add(new FxRate(d, v));
            }

            if (rates.Select(r => r.Date).Distinct().Count() != rates.Count)
            {
                throw Unexpected(series, "a date appears twice");
            }

            return [.. rates.OrderBy(r => r.Date)];
        }
        catch (JsonException ex)
        {
            throw new FxUnavailableException($"The Riksbank's answer for {series} is not JSON ({ex.Message}).", ex);
        }
    }

    private static FxUnavailableException Unexpected(string series, string what) =>
        new($"The Riksbank's answer for {series} is not what the program expects: {what}. Nothing was stored.");
}

/// <summary>What one FX import stored.</summary>
public sealed record FxImportReport(string Currency, WriteCounts Rates, DateOnly? First, DateOnly? Last, decimal? LatestSekPerUnit, DataSourceInfo Source);

/// <summary>Imports one currency's daily fixings into the history store (ADR 0005), known at now.</summary>
public static class FxImporter
{
    /// <summary>A range at least this long without a single fixing means the series is wrong, not a quiet week.</summary>
    public static readonly int SilentRangeDays = 10;

    public static async Task<FxImportReport> ImportAsync(HistoryStore store, IFxRateSource source, string currency, DateOnly from, DateOnly to, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(time);
        if (Markets.ForCurrency(currency) is null || !Markets.IsForeign(currency))
        {
            throw new ArgumentException($"FX rates are for the account's foreign currencies ({string.Join(", ", Markets.All.Where(m => Markets.IsForeign(m.Currency)).Select(m => m.Currency))}), not '{currency}'.");
        }

        IReadOnlyList<FxRate> rates = await source.GetDailyAsync(currency, from, to, ct).ConfigureAwait(false);
        if (rates.Count == 0 && to.DayNumber - from.DayNumber >= SilentRangeDays)
        {
            throw new FxUnavailableException($"{source.Source.Name} has no {currency} fixing from {from:yyyy-MM-dd} to {to:yyyy-MM-dd}; is the series right? Nothing was stored.");
        }

        store.RegisterSource(source.Source);
        WriteCounts written = store.UpsertFxRates(currency, [.. rates.Where(r => r.Date >= from && r.Date <= to)], source.Source, source.SourceVersion, time.GetUtcNow());
        return new FxImportReport(currency, written, rates.Count > 0 ? rates[0].Date : null, rates.Count > 0 ? rates[^1].Date : null, rates.Count > 0 ? rates[^1].SekPerUnit : null, source.Source);
    }
}
