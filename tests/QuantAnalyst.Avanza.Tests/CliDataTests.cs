using System.Text.Json;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>Phase 4 verbs: <c>qa stream</c>, <c>qa history</c>, <c>qa instruments</c>, <c>qa calendar</c> (fake server, temp store).</summary>
public sealed class CliDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-cli-data-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAvanza _server = new();

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

    /// <summary>Online verbs get a state folder and TOTP login against the fake; offline verbs run as given.</summary>
    private (int Code, string Output, string Error) Qa(bool online, params string[] args)
    {
        var services = new AvanzaCliServices(
            (options, secrets, prompt, logger, redactor) => AvanzaConnection.CreateForTest(
                new AvanzaOptions
                {
                    StateDirectory = options.StateDirectory,
                    RecordingDirectory = options.RecordingDirectory,
                    LoginMethod = options.LoginMethod,
                    RequestsPerSecond = 10,
                    Burst = 20,
                },
                secrets, logger, redactor, TimeProvider.System, _server, prompt),
            _ => FakeSecrets.Store());
        var output = new StringWriter();
        var error = new StringWriter();
        string[] full = online ? [.. args, "--state-dir", Path.Combine(_root, "state"), "--login", "totp"] : args;
        int code = QaCli.Run(full, output, error, services);
        string all = output + "\n" + error;
        foreach (string secret in FakeSecrets.All.Append("9990001"))
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }

        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Stream_PrintsLiveQuotesFromDepthAndPoll_ThenASummary()
    {
        var conn = new SseConnection();
        _server.Serve(conn);
        await conn.Event("info", "connected", "e1", 1000);

        // Push the depth snapshot only after the first poll, so the stream is the newer source (deterministic output).
        Task pushDepth = Task.Run(async () =>
        {
            while (_server.CountFor(Http.AvanzaRoutes.MarketData) == 0)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            await Task.Delay(150, TestContext.Current.CancellationToken);
            await conn.Depth("5240", 70.84m, 70.86m, "e2");
        }, TestContext.Current.CancellationToken);

        (int code, string output, string error) = Qa(true, "stream", "ERIC-B", "--duration", "1.5", "--no-record");
        await pushDepth;
        Assert.True(code == 0, output + error);
        Assert.Contains("Streaming ERIC B (5240) for 1.5 s", output, StringComparison.Ordinal);
        Assert.Contains("ERIC B   bid 70.84 x 100  ask 70.86 x 200  last 70.86  stream", output, StringComparison.Ordinal);
        Assert.Contains("ERIC B: ", output, StringComparison.Ordinal);
        Assert.Contains("with bid/ask from the depth stream", output, StringComparison.Ordinal);
        Assert.Equal(1, _server.CountFor(Http.AvanzaRoutes.OrderDepthStream));
    }

    [Fact]
    public async Task Stream_WithDrift_IsAHalt()
    {
        var conn = new SseConnection();
        _server.Serve(conn);
        await conn.Event("ORDER_DEPTH", """{"orderbookId":"5240","levels":[],"brandNew":1}""");
        (int code, _, string error) = Qa(true, "stream", "ERIC-B", "--duration", "10", "--no-record");
        Assert.Equal(AvanzaCommands.ExitHalt, code);
        Assert.Contains("HALT: Schema drift on 'order-depth-stream'", error, StringComparison.Ordinal);
        Assert.Contains("$.brandNew", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--duration", "0")]
    [InlineData("--poll", "0.5")]
    public void Stream_RejectsBadLimits(string option, string value)
    {
        (int code, _, string error) = Qa(true, "stream", "ERIC-B", option, value, "--no-record");
        Assert.Equal(1, code);
        Assert.Contains("--duration must be", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Stream_AtMostFiveInstruments()
    {
        (int code, _, string error) = Qa(true, "stream", "A", "B", "C", "D", "E", "F", "--no-record");
        Assert.Equal(1, code);
        Assert.Contains("Give 1–5 tickers", error, StringComparison.Ordinal);
        Assert.Empty(_server.Requests); // refused before login
    }

    [Fact]
    public void HistoryImport_ThenShowAndInstruments_OfflineWithLabels()
    {
        (int code, string output, string error) = Qa(true, "history", "import", "ERIC-B", "--from", "2026-09-24", "--to", "2026-09-25", "--store", Store);
        Assert.True(code == 0, output + error);
        Assert.Contains("ERIC B Ericsson B (orderbook 5240, SE0000108656, XSTO, SEK)", output, StringComparison.Ordinal);
        Assert.Contains("Instrument master: added.", output, StringComparison.Ordinal);
        Assert.Contains("Daily bars 2026-09-24..2026-09-25: 2 new, 0 restated, 0 unchanged.", output, StringComparison.Ordinal);
        Assert.Contains("Source: avanza-price-chart — NOT survivorship-free, NOT point-in-time.", output, StringComparison.Ordinal);
        Assert.Contains("resolution=day", _server.Requests.Last(r => r.PathAndQuery.Contains("price-chart", StringComparison.Ordinal)).PathAndQuery, StringComparison.Ordinal);

        (code, output, _) = Qa(true, "history", "import", "ERIC-B", "--from", "2026-09-24", "--to", "2026-09-25", "--store", Store);
        Assert.Equal(0, code);
        Assert.Contains("0 new, 0 restated, 2 unchanged", output, StringComparison.Ordinal);
        Assert.Contains("Instrument master: unchanged.", output, StringComparison.Ordinal);

        int requests = _server.Requests.Count;
        (code, output, error) = Qa(false, "history", "show", "ERIC-B", "--store", Store);
        Assert.True(code == 0, error);
        string[] rows = output.Split('\n');
        string day1 = rows.Single(l => l.StartsWith("2026-09-24", StringComparison.Ordinal));
        string day2 = rows.Single(l => l.StartsWith("2026-09-25", StringComparison.Ordinal));
        Assert.Equal(["2026-09-24", "70.1", "70.6", "69.9", "70.44", "3100000"], day1.Split(' ', StringSplitOptions.RemoveEmptyEntries)[..6]);
        Assert.Equal(["2026-09-25", "70.44", "71.2", "70.1", "70.86", "3456789"], day2.Split(' ', StringSplitOptions.RemoveEmptyEntries)[..6]);
        Assert.Contains("2 bar(s); prices in SEK", output, StringComparison.Ordinal);
        Assert.Contains("NOT survivorship-free", output, StringComparison.Ordinal);

        (code, output, _) = Qa(false, "instruments", "--store", Store);
        Assert.Equal(0, code);
        Assert.Contains("ERIC B", output, StringComparison.Ordinal);
        Assert.Contains("Continuous", output, StringComparison.Ordinal);
        Assert.Equal(requests, _server.Requests.Count); // offline verbs make no HTTP calls

        (code, _, error) = Qa(false, "history", "show", "ERIC-B", "--store", Store, "--as-of", "2000-01-01T00:00:00Z");
        Assert.Equal(1, code);
        Assert.Contains("not in the instrument master at that time", error, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryImport_StoresTheDividends_AndHistoryDividendsChecksTheHistoryOnTheExDates()
    {
        // Plan 21: a 1.45 SEK dividend goes ex on 2026-09-25, and that day opens at 69.00 after a 70.44 close: price-only.
        _server.Always(AvanzaRoutes.StockDetails, _ => FakeAvanza.Json(Fixtures.Mutate("stock-details-5240.json", n =>
        {
            n["dividends"]!["pastEvents"]![0]!["exDate"] = "2026-09-25";
            n["dividends"]!["pastEvents"]![0]!["paymentDate"] = "2026-09-30";
        })));
        _server.Always(AvanzaRoutes.PriceChart, _ => FakeAvanza.Json(Fixtures.Mutate("price-chart-5240.json", n =>
        {
            n["ohlc"]![1]!["open"] = 69.0;
            n["ohlc"]![1]!["low"] = 68.9;
        })));

        (int code, string output, string error) = Qa(true, "history", "import", "ERIC-B", "--from", "2026-09-24", "--to", "2026-09-25", "--store", Store);
        Assert.True(code == 0, output + error);
        Assert.Contains("stored; share count 3,334,151,735 (check the history against them with 'qa history dividends').", output, StringComparison.Ordinal);

        int requests = _server.Requests.Count;
        (code, output, error) = Qa(false, "history", "dividends", "ERIC-B", "--store", Store);
        Assert.True(code == 0, error);
        string[] rows = output.ReplaceLineEndings("\n").Split('\n'); // Windows writes \r\n
        Assert.Equal(["2026-09-25", "1.45", "SEK", "2026-09-30", "70.44", "69", "2.06%", "-2.04%", "price-only"],
            rows.Single(l => l.StartsWith("2026-09-25", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.EndsWith("no bars around it", rows.Single(l => l.StartsWith("2025-03-27", StringComparison.Ordinal)).TrimEnd(), StringComparison.Ordinal);
        Assert.EndsWith("not yet in the history", rows.Single(l => l.StartsWith("2026-10-22", StringComparison.Ordinal)).TrimEnd(), StringComparison.Ordinal);
        Assert.Contains("Share count 3,334,151,735", output, StringComparison.Ordinal);
        Assert.Contains("The stored history is price-only", output, StringComparison.Ordinal);

        (code, output, _) = Qa(false, "history", "dividends", "ERIC-B", "--store", Store, "--json");
        Assert.Equal(0, code);
        using JsonDocument json = JsonDocument.Parse(output);
        Assert.Equal("PriceOnly", json.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(4, json.RootElement.GetProperty("dividends").GetArrayLength());
        Assert.Equal(requests, _server.Requests.Count); // offline
    }

    [Fact]
    public void HistoryImport_WithoutTheStockDetails_StillImportsTheBars()
    {
        _server.Always(AvanzaRoutes.StockDetails, _ => FakeAvanza.Status(System.Net.HttpStatusCode.OK, """{ "stock": {} }"""));

        (int code, string output, string error) = Qa(true, "history", "import", "ERIC-B", "--from", "2026-09-24", "--to", "2026-09-25", "--store", Store);

        Assert.True(code == 0, output + error);
        Assert.Contains("Daily bars 2026-09-24..2026-09-25: 2 new", output, StringComparison.Ordinal);
        Assert.Contains("warning: dividends and the share count were not stored (", output, StringComparison.Ordinal);
        Assert.Contains("No dividends stored. 'qa history import ERIC-B' stores them", Qa(false, "history", "dividends", "ERIC-B", "--store", Store).Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "Trading model (plan 22): Continuous — it traded outside the auction times on 3 of 3 days last week.", null)]
    [InlineData(false, "Trading model (plan 22): PeriodicAuction — it traded only at the auction times", "trades only in auctions")]
    public void HistoryImport_OfAFirstNorthShare_MeasuresHowItTrades_AndTheListFollows(bool continuous, string measured, string? refused)
    {
        // Plan 22: the same share as if listed on First North; last week's 10-minute bars tell how it trades.
        _server.Always(AvanzaRoutes.Orderbook, _ => FakeAvanza.Json(Fixtures.Mutate("orderbook-5240.json", n => n["marketPlace"] = "FNSE")));
        _server.Always(AvanzaRoutes.PriceChart, r => r.RequestUri!.Query.Contains("ten_minutes", StringComparison.Ordinal)
            ? ChartAnswers.TenMinuteWeek(continuous)
            : FakeAvanza.Json(Fixtures.Bytes("price-chart-5240.json")));

        (int code, string output, string error) = Qa(true, "history", "import", "ERIC-B", "--from", "2026-09-24", "--to", "2026-09-25", "--store", Store);
        Assert.True(code == 0, output + error);
        Assert.Contains(measured, output, StringComparison.Ordinal);
        Assert.Contains(_server.Requests, r => r.PathAndQuery.EndsWith("?timePeriod=one_week&resolution=ten_minutes", StringComparison.Ordinal));

        string config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        (code, output, error) = Qa(false, "universe", "add", "ERIC-B", "--config-dir", config, "--store", Store);
        if (refused is null)
        {
            Assert.True(code == 0, error);
        }
        else
        {
            Assert.Equal(1, code);
            Assert.Contains(refused, error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StoreThatCannotBeOpened_IsAReadableError_NotACrash()
    {
        string directory = Path.Combine(_root, "a-folder-not-a-file");
        Directory.CreateDirectory(directory);
        (int code, string output, string error) = Qa(true, "history", "import", "ERIC-B", "--from", "2026-09-24", "--to", "2026-09-25", "--store", directory);
        Assert.True(code == 1, output + error);
        Assert.StartsWith("error: the history store could not be used:", error, StringComparison.Ordinal);

        File.WriteAllText(Store, "this is not a DuckDB file");
        (code, _, error) = Qa(false, "history", "show", "ERIC-B", "--store", Store);
        Assert.Equal(1, code);
        Assert.StartsWith("error: the history store could not be used:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryShow_WithoutAStore_SaysWhatToDo()
    {
        (int code, _, string error) = Qa(false, "history", "show", "ERIC-B", "--store", Store);
        Assert.Equal(1, code);
        Assert.Contains("Run 'qa history import <TICKER>' first", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Store));
    }

    [Fact]
    public void Calendar_ClassifiesAndReportsVerification()
    {
        string config = Path.Combine(AppContext.BaseDirectory, "config");
        (int code, string output, string error) = Qa(false, "calendar", "--date", "2026-06-19", "--config-dir", config);
        Assert.True(code == 0, error);
        Assert.Contains("2026-06-19 (Friday): Closed (Midsummer Eve).", output, StringComparison.Ordinal);

        (code, output, _) = Qa(false, "calendar", "--year", "2027", "--config-dir", config);
        Assert.Equal(0, code);
        Assert.Contains("XSTO 2027: ", output, StringComparison.Ordinal);
        Assert.Contains("Midsummer Eve", output, StringComparison.Ordinal);
        Assert.Contains("2027 ", output, StringComparison.Ordinal);

        (code, _, error) = Qa(false, "calendar", "--date", "2031-01-02", "--config-dir", config);
        Assert.Equal(1, code);
        Assert.Contains("No XSTO calendar is loaded for 2031", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Calendar_ShowsTheUsAndCanadianMarkets_InTheirOwnTime_AndInStockholmTime()
    {
        string config = Path.Combine(AppContext.BaseDirectory, "config");
        (int code, string output, string error) = Qa(false, "calendar", "--market", "XNYS", "--date", "2026-11-27", "--config-dir", config);
        Assert.True(code == 0, error);
        Assert.Contains("2026-11-27 (Friday): Half trading day, 09:30–13:00 New York (Day after Thanksgiving) (15:30–19:00 Stockholm).", output, StringComparison.Ordinal);

        (code, output, _) = Qa(false, "calendar", "--market", "xtse", "--year", "2026", "--config-dir", config);
        Assert.Equal(0, code);
        Assert.Contains("XTSE 2026: ", output, StringComparison.Ordinal);
        Assert.Contains("regular session 09:30–16:00 Toronto.", output, StringComparison.Ordinal);
        Assert.Contains("Boxing Day (observed)", output, StringComparison.Ordinal);
        Assert.Contains("NOT VERIFIED", output, StringComparison.Ordinal);

        (code, _, error) = Qa(false, "calendar", "--market", "XOSL", "--config-dir", config);
        Assert.Equal(1, code);
        Assert.Contains("XOSL is not a market the program trades on; use XSTO, XNYS, XTSE.", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-09-25T18:00:00Z", "2026-09-25T18:00:00+00:00")]
    [InlineData("2026-09-25T20:00:00+02:00", "2026-09-25T18:00:00+00:00")]
    [InlineData("2026-09-25T20:00:00", "2026-09-25T18:00:00+00:00")] // Stockholm wall clock (CEST)
    [InlineData("2026-01-15 10:00", "2026-01-15T09:00:00+00:00")] // CET
    public void AsOf_AcceptsOffsetsOrStockholmTime(string text, string expectedUtc) =>
        Assert.Equal(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), DataCommands.ParseAsOf(text));

    [Fact]
    public void AsOf_RefusesAmbiguousStockholmTimes() =>
        Assert.Throws<ArgumentException>(() => DataCommands.ParseAsOf("2026-10-25T02:30:00")); // DST ends: 02:30 happens twice
}
