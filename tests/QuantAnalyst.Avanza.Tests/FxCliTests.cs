using System.Text.Json;
using System.Text.Json.Nodes;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Fx;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// ADR 0005 / plan 16 step 2: <c>qa fx import|show</c>, and a USD share's history import bringing its FX fixings along
/// (first, so a share whose rates can't be read stores nothing). The Riksbank is faked; Avanza serves the recorded
/// answers, with the orderbook turned into a US share.
/// </summary>
public sealed class FxCliTests : IDisposable
{
    private static readonly string Recording = Path.Combine(AppContext.BaseDirectory, "fixtures", "avanza", "2026-09-25");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-fx-cli", Guid.NewGuid().ToString("N"));
    private readonly FakeAvanza _server = new();
    private readonly FakeFx _fx = new();

    public FxCliTests()
    {
        _server.Always(AvanzaRoutes.Orderbook, r => FakeAvanza.Json(UsOrderbook(r.RequestUri!.AbsolutePath.Split('/')[^1])));
        _server.Always(AvanzaRoutes.PriceChart, _ => FakeAvanza.Json(Body("035-price-chart")));
    }

    private string Store => Path.Combine(_root, "q.duckdb");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    internal static string Body(string file)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Recording, file + ".json")));
        return doc.RootElement.GetProperty("response").GetProperty("body").GetRawText();
    }

    /// <summary>The recorded orderbook as a US share (Avanza's own fields, only the values changed).</summary>
    internal static string UsOrderbook(string id)
    {
        JsonNode node = JsonNode.Parse(Body("033-orderbook"))!;
        node["id"] = id;
        node["name"] = "Apple Inc";
        node["isin"] = "US0378331005";
        node["currency"] = "USD";
        node["marketPlace"] = "XNAS";
        node["countryCode"] = "US";
        node["tickerSymbol"] = "AAPL";
        return node.ToJsonString();
    }

    private (int Code, string Output, string Error) Qa(params string[] args)
    {
        var services = new AvanzaCliServices(
            (options, secrets, prompt, logger, redactor) => AvanzaConnection.CreateForTest(
                new AvanzaOptions { StateDirectory = options.StateDirectory, LoginMethod = options.LoginMethod, RequestsPerSecond = 10, Burst = 20 },
                secrets, logger, redactor, TimeProvider.System, _server, prompt),
            _ => FakeSecrets.Store())
        {
            FxRates = () => _fx,
        };
        var output = new StringWriter();
        var error = new StringWriter();
        int code = QaCli.Run(args, output, error, services);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Import_StoresEachCurrency_AndShowListsTheLastFixings()
    {
        (int code, string output, string error) = Qa("fx", "import", "usd", "CAD", "--from", "2026-09-01", "--to", "2026-09-25", "--store", Store);
        Assert.True(code == 0, error);
        Assert.Contains("USD: 19 new, 0 restated, 0 unchanged fixing(s), 2026-09-01 to 2026-09-25; latest 10.2400 SEK.", output, StringComparison.Ordinal);
        Assert.Contains("CAD: 19 new", output, StringComparison.Ordinal);
        Assert.Contains("riksbank-fixing — survivorship-free, point-in-time", output, StringComparison.Ordinal);
        Assert.Equal([("USD", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 25)), ("CAD", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 25))], _fx.Calls);

        (code, output, error) = Qa("fx", "show", "USD", "--store", Store);
        Assert.True(code == 0, error);
        Assert.Contains("SEK per USD", output, StringComparison.Ordinal);
        Assert.Matches(@"2026-09-25\s+10\.2400", output);
        Assert.DoesNotContain("2026-09-11 ", output, StringComparison.Ordinal); // the last 10 only
        Assert.Contains("19 fixing(s) 2026-09-01 to 2026-09-25", output, StringComparison.Ordinal);

        (code, output, _) = Qa("fx", "show", "CAD", "--from", "2026-09-24", "--store", Store);
        Assert.Equal(0, code);
        Assert.Contains("2 fixing(s)", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("EUR", "FX rates are for the account's foreign currencies (USD, CAD), not 'EUR'.")]
    [InlineData("SEK", "not 'SEK'")]
    public void OtherCurrencies_AreRefused(string currency, string expected)
    {
        (int code, _, string error) = Qa("fx", "import", currency, "--from", "2026-09-01", "--store", Store);
        Assert.Equal(1, code);
        Assert.Contains(expected, error, StringComparison.Ordinal);
        Assert.Empty(_fx.Calls);
    }

    [Fact]
    public void AnUnreachableRiksbank_IsAnErrorLine()
    {
        _fx.Fails = new FxUnavailableException("The Riksbank limits calls without a key; try again in a minute.");
        (int code, _, string error) = Qa("fx", "import", "USD", "--from", "2026-09-01", "--store", Store);
        Assert.Equal(1, code);
        Assert.Contains("error: The Riksbank limits calls without a key", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AUsShare_ImportsItsFxFixingsWithItsHistory_AsAContinuouslyTradedUsdInstrument()
    {
        (int code, string output, string error) = Qa("history", "import", "--id", "4478", "--from", "2026-09-24", "--to", "2026-09-25", "--store", Store,
            "--state-dir", Path.Combine(_root, "state"), "--login", "totp");
        Assert.True(code == 0, error + output);
        Assert.Contains("AAPL Apple Inc (orderbook 4478, US0378331005, XNAS, USD)", output, StringComparison.Ordinal);
        Assert.Contains("FX fixings (ADR 0005): USD: ", output, StringComparison.Ordinal);
        Assert.Equal(("USD", new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 25)), Assert.Single(_fx.Calls)); // 10 days' lead

        using (HistoryStore store = HistoryStore.Open(Store))
        {
            StoredInstrument aapl = store.GetInstrument(new OrderbookId("4478"))!;
            Assert.Equal(("USD", TradingModel.Continuous), (aapl.Instrument.Currency, aapl.Instrument.TradingModel));
            Assert.NotEmpty(store.GetFxRates("USD", RiksbankFxSource.Riksbank.Name));
        }

        // The allowlist takes it (ADR 0005: foreign shares trade on paper only).
        string config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        (code, output, error) = Qa("universe", "add", "AAPL", "--config-dir", config, "--store", Store);
        Assert.True(code == 0, error);
        Assert.Contains("added AAPL (4478", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AUsShareWhoseRatesCantBeRead_StoresNothing()
    {
        _fx.Fails = new FxUnavailableException("The Riksbank answered HTTP 503 for SEKUSDPMI.");
        (int code, string output, string error) = Qa("history", "import", "--id", "4478", "--from", "2026-09-24", "--to", "2026-09-25", "--store", Store,
            "--state-dir", Path.Combine(_root, "state"), "--login", "totp");
        Assert.NotEqual(0, code);
        Assert.Contains("HTTP 503 for SEKUSDPMI", error + output, StringComparison.Ordinal);
        Assert.Equal(0, _server.CountFor(AvanzaRoutes.PriceChart));

        using HistoryStore store = HistoryStore.Open(Store);
        Assert.Null(store.GetInstrument(new OrderbookId("4478")));
    }

    /// <summary>Weekday fixings, 10.00 plus 0.01 per day into September 2026; USD and CAD alike.</summary>
    internal sealed class FakeFx : IFxRateSource
    {
        public List<(string Currency, DateOnly First, DateOnly Last)> Calls { get; } = [];

        public Exception? Fails { get; set; }

        /// <summary>Gets or sets the last day with a fixing (a stale series), or null for every weekday asked.</summary>
        public DateOnly? Until { get; set; }

        public DataSourceInfo Source => RiksbankFxSource.Riksbank;

        public string SourceVersion => "test";

        public Task<IReadOnlyList<FxRate>> GetDailyAsync(string currency, DateOnly first, DateOnly last, CancellationToken ct)
        {
            Calls.Add((currency, first, last));
            if (Fails is { } fail)
            {
                return Task.FromException<IReadOnlyList<FxRate>>(fail);
            }

            var rates = new List<FxRate>();
            for (DateOnly d = first; d <= last; d = d.AddDays(1))
            {
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && (Until is not { } until || d <= until))
                {
                    rates.Add(new FxRate(d, 10m + (0.01m * (d.DayNumber - new DateOnly(2026, 9, 1).DayNumber))));
                }
            }

            return Task.FromResult<IReadOnlyList<FxRate>>(rates);
        }
    }
}
