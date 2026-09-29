using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Intraday;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// Plan 17 step A2: <c>qa intraday research add|remove|list</c> (the public search, no login) and
/// <c>qa intraday import</c> (the public chart, no login): the allowlist's Stockholm shares and the research list,
/// 1- and 5-minute bars, a failing name reported while the others go on.
/// </summary>
public sealed class IntradayImportTests : IDisposable
{
    // Tuesday 2026-09-29 18:00 Stockholm: the day's bars are all closed.
    private static readonly DateTimeOffset Evening = new(2026, 9, 29, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Open = new(2026, 9, 29, 7, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-intraday-import", Guid.NewGuid().ToString("N"));
    private readonly FakeAvanza _server = new();

    public IntradayImportTests()
    {
        Directory.CreateDirectory(Config);
        _server.Always(AvanzaRoutes.Search, Search);
        _server.Always(AvanzaRoutes.PriceChart, Chart);
    }

    private string Config => Path.Combine(_root, "config");

    private string Store => Path.Combine(_root, "q.duckdb");

    /// <summary>Gets the orderbooks whose chart answers daily bars whatever is asked (a failing name).</summary>
    private HashSet<string> DailyOnly { get; } = [];

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

    private (int Code, string Output, string Error) Qa(params string[] args)
    {
        var services = new AvanzaCliServices(
            (options, secrets, prompt, logger, redactor) => AvanzaConnection.CreateForTest(
                new AvanzaOptions { StateDirectory = options.StateDirectory, LoginMethod = options.LoginMethod, RequestsPerSecond = 10, Burst = 20 },
                secrets, logger, redactor, TimeProvider.System, _server, prompt),
            _ => FakeSecrets.Store())
        {
            Time = new FakeTimeProvider(Evening),
        };
        var output = new StringWriter();
        var error = new StringWriter();
        bool online = args is ["intraday", "import", ..] or ["intraday", "research", "add", ..];
        int code = QaCli.Run(online ? [.. args, "--state-dir", Path.Combine(_root, "state")] : args, output, error, services);
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>The recorded "ERIC B" search (Ericsson B in SEK and a foreign listing); "VOLV B" is the same answer as Volvo.</summary>
    private static HttpResponseMessage Search(HttpRequestMessage request)
    {
        string query = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!["query"]!.GetValue<string>();
        JsonNode body = JsonNode.Parse(FxCliTests.Body("032-search"))!;
        if (string.Equals(query, "VOLV B", StringComparison.OrdinalIgnoreCase))
        {
            JsonNode hit = body["hits"]![0]!;
            hit["title"] = "Volvo B (VOLV B)";
            hit["orderBookId"] = "5269";
            hit["urlSlugName"] = "volvo-b";
        }
        else if (!string.Equals(query, "ERIC B", StringComparison.OrdinalIgnoreCase))
        {
            body["hits"] = new JsonArray();
            body["totalNumberOfHits"] = 0;
        }

        return FakeAvanza.Json(body.ToJsonString());
    }

    private HttpResponseMessage Chart(HttpRequestMessage request)
    {
        string id = request.RequestUri!.AbsolutePath.Split('/')[^1];
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        string? resolution = query["resolution"];
        if (DailyOnly.Contains(id) || resolution is null or "day")
        {
            return FakeAvanza.Json(FxCliTests.Body("035-price-chart"));
        }

        (int count, int minutes) = resolution == "five_minutes" ? (2, 5) : (5, 1);
        var ohlc = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            ohlc.Add(new JsonObject
            {
                ["timestamp"] = Open.AddMinutes(i * minutes).ToUnixTimeMilliseconds(),
                ["open"] = 94.9,
                ["close"] = 95.0,
                ["low"] = 94.8,
                ["high"] = 95.1,
                ["totalVolumeTraded"] = 1200,
            });
        }

        return FakeAvanza.Json(new JsonObject
        {
            ["ohlc"] = ohlc,
            ["metadata"] = new JsonObject { ["resolution"] = new JsonObject { ["chartResolution"] = resolution, ["availableResolutions"] = new JsonArray("minute", "five_minutes") } },
        }.ToJsonString());
    }

    /// <summary>ERIC B (SEK) and AAPL (USD) in the instrument master and on the allowlist, as 'qa universe add' leaves them.</summary>
    private void AllowEricAndApple()
    {
        using (HistoryStore store = HistoryStore.Open(Store))
        {
            string ticks = InstrumentRecord.CanonicalTickTable(new TickSizeTable([new TickSizeBand(0m, 99_999m, 0.01m)]));
            foreach ((string id, string ticker, string currency, string market) in new[] { ("5240", "ERIC B", "SEK", "XSTO"), ("4478", "AAPL", "USD", "XNAS") })
            {
                store.UpsertInstrument(new InstrumentRecord(new OrderbookId(id), null, ticker, ticker, currency, market, "STOCK", TradingModel.Continuous, 1m, ticks, new DateOnly(2026, 9, 25)),
                    "test", "test", Evening);
            }
        }

        File.WriteAllText(Path.Combine(Config, "universe.json"),
            """{ "format": "qa-universe/1", "instruments": [ { "orderbook_id": "5240", "ticker": "ERIC B", "name": "Ericsson B" }, { "orderbook_id": "4478", "ticker": "AAPL", "name": "Apple Inc" } ] }""");
    }

    [Fact]
    public void TheResearchList_TakesStockholmSharesByTicker_FromThePublicSearch()
    {
        (int code, string output, string error) = Qa("intraday", "research", "add", "VOLV-B", "ERIC-B", "--config-dir", Config);
        Assert.True(code == 0, error + output);
        Assert.Contains("added VOLV B (Volvo B, orderbook 5269, Stockholmsbörsen).", output, StringComparison.Ordinal);
        Assert.Contains("added ERIC B (Ericsson B, orderbook 5240, Stockholmsbörsen).", output, StringComparison.Ordinal); // not the foreign listing
        Assert.Contains("Research list: 2 of 30", output, StringComparison.Ordinal);

        (code, output, _) = Qa("intraday", "research", "add", "eric b", "NOPE", "--config-dir", Config);
        Assert.Equal(1, code);
        Assert.Contains("ERIC B: already on the research list.", output, StringComparison.Ordinal);
        Assert.Contains("NOPE: no Stockholm (SEK) share with that ticker in Avanza's search.", output, StringComparison.Ordinal);

        (code, output, _) = Qa("intraday", "research", "remove", "VOLV-B", "--config-dir", Config);
        Assert.Equal(0, code);
        (code, output, _) = Qa("intraday", "research", "list", "--config-dir", Config);
        Assert.Equal(0, code);
        Assert.Contains("Research list, 1 of 30: ERIC B", output, StringComparison.Ordinal);
        Assert.Equal(["ERIC B"], ResearchList.Load(Path.Combine(Config, ResearchList.FileName)).Entries.Select(e => e.Ticker));

        // Searches only: no login, no other route.
        Assert.All(_server.Requests, r => Assert.StartsWith(AvanzaRoutes.Search.Path(), r.PathAndQuery, StringComparison.Ordinal));
    }

    [Fact]
    public void Import_CollectsTheAllowlistsStockholmSharesAndTheResearchList_WithoutALogin()
    {
        AllowEricAndApple();
        Assert.Equal(0, Qa("intraday", "research", "add", "VOLV-B", "--config-dir", Config).Code);

        (int code, string output, string error) = Qa("intraday", "import", "--config-dir", Config, "--store", Store);

        Assert.True(code == 0, error + output);
        Assert.Contains("AAPL: skipped, intraday research is Stockholm only (ADR 0006).", output, StringComparison.Ordinal);
        Assert.Contains("ERIC B 1-minute: 5 new, 0 restated, 0 unchanged bar(s) over 1 day(s), 2026-09-29 09:00 to 2026-09-29 09:04.", output, StringComparison.Ordinal);
        Assert.Contains("ERIC B 5-minute: 2 new", output, StringComparison.Ordinal);
        Assert.Contains("VOLV B 1-minute: 5 new", output, StringComparison.Ordinal);
        Assert.Contains("Intraday: 14 bar(s) stored for 2 share(s). Source: avanza-price-chart — NOT survivorship-free, NOT point-in-time.", output, StringComparison.Ordinal);

        using (HistoryStore store = HistoryStore.Open(Store))
        {
            Assert.Equal(5, store.GetIntradayBars(new OrderbookId("5269"), ChartResolution.Minute, AvanzaChartImporter.AvanzaPriceChart.Name).Count);
            Assert.Equal(2, store.GetIntradayBars(new OrderbookId("5240"), ChartResolution.FiveMinutes, AvanzaChartImporter.AvanzaPriceChart.Name).Count);
            Assert.Empty(store.GetIntradayBars(new OrderbookId("4478"), ChartResolution.Minute, AvanzaChartImporter.AvanzaPriceChart.Name));
        }

        // Again: nothing new.
        (code, output, _) = Qa("intraday", "import", "ERIC-B", "--resolution", "minute", "--config-dir", Config, "--store", Store);
        Assert.Equal(0, code);
        Assert.Contains("ERIC B 1-minute: 0 new, 0 restated, 5 unchanged bar(s)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("5-minute", output, StringComparison.Ordinal);

        Assert.DoesNotContain(_server.Requests, r => r.PathAndQuery.StartsWith(AvanzaRoutes.UserCredentials.Path(), StringComparison.Ordinal) || r.Headers.ContainsKey("X-SecurityToken"));
        Assert.All(_server.Requests.Where(r => r.PathAndQuery.StartsWith("/_api/price-chart/", StringComparison.Ordinal)),
            r => Assert.Contains("timePeriod=today&resolution=", r.PathAndQuery, StringComparison.Ordinal));
    }

    [Fact]
    public void AFailingName_IsReported_TheOthersAreStored_AndTheExitCodeSaysSo()
    {
        AllowEricAndApple();
        Assert.Equal(0, Qa("intraday", "research", "add", "VOLV-B", "--config-dir", Config).Code);
        DailyOnly.Add("5269");

        (int code, string output, _) = Qa("intraday", "import", "--config-dir", Config, "--store", Store);

        Assert.Equal(1, code);
        Assert.Contains("VOLV B 1-minute: FAILED (Avanza answered with Day bars for Today, not Minute; nothing was stored.", output, StringComparison.Ordinal);
        Assert.Contains("ERIC B 5-minute: 2 new", output, StringComparison.Ordinal);
        Assert.Contains("2 import(s) failed", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_Refuses_AForeignShare_AnUnknownPeriod_AndAnEmptyList()
    {
        AllowEricAndApple();
        (int code, _, string error) = Qa("intraday", "import", "AAPL", "--config-dir", Config, "--store", Store);
        Assert.Equal(1, code);
        Assert.Contains("AAPL trades in USD; intraday research is Stockholm only (ADR 0006).", error, StringComparison.Ordinal);

        (code, _, error) = Qa("intraday", "import", "--period", "one_year", "--config-dir", Config, "--store", Store);
        Assert.Equal(1, code);
        Assert.Contains("--period: 'one_year' is not today, one_week, one_month or three_months.", error, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(Config, "universe.json"), """{ "format": "qa-universe/1", "instruments": [] }""");
        (code, _, error) = Qa("intraday", "import", "--config-dir", Config, "--store", Store);
        Assert.Equal(1, code);
        Assert.Contains("Nothing to collect", error, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);
    }
}
