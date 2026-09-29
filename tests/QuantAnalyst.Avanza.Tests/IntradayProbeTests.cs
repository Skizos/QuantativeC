using System.Net;
using System.Text.Json.Nodes;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// Plan 17 step A1: <c>qa intraday probe</c> asks the public price chart, without a login, which bar sizes it gives
/// for the short periods and how many days of 1- and 5-minute bars come back. The fake answers are shaped like the
/// recorded chart (2026-09-25), with intraday resolutions.
/// </summary>
public sealed class IntradayProbeTests : IDisposable
{
    private static readonly string[] TodayOffers = ["minute", "two_minutes", "five_minutes", "ten_minutes", "thirty_minutes", "hour"];
    private static readonly string[] WeekOffers = ["five_minutes", "ten_minutes", "thirty_minutes", "hour"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-intraday-probe", Guid.NewGuid().ToString("N"));
    private readonly FakeAvanza _server = new();

    public IntradayProbeTests() => _server.Always(AvanzaRoutes.PriceChart, Answer);

    /// <summary>Gets or sets a status the chart answers instead, for one period and resolution ("one_week&amp;five_minutes").</summary>
    private Dictionary<string, HttpStatusCode> Fails { get; } = new(StringComparer.Ordinal);

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
                new AvanzaOptions { StateDirectory = options.StateDirectory, LoginMethod = options.LoginMethod, RecordingDirectory = options.RecordingDirectory, RequestsPerSecond = 10, Burst = 20 },
                secrets, logger, redactor, TimeProvider.System, _server, prompt),
            _ => FakeSecrets.Store());
        var output = new StringWriter();
        var error = new StringWriter();
        int code = QaCli.Run(args, output, error, services);
        return (code, output.ToString(), error.ToString());
    }

    private string[] Probe(params string[] extra) =>
        ["intraday", "probe", .. extra, "--state-dir", Path.Combine(_root, "state"), "--record-dir", Path.Combine(_root, "rec")];

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        string period = query["timePeriod"]!;
        string? resolution = query["resolution"];
        if (Fails.TryGetValue($"{period}&{resolution}", out HttpStatusCode status))
        {
            return FakeAvanza.Status(status);
        }

        return (period, resolution) switch
        {
            ("today", null) => Chart("minute", TodayOffers, Bars(new DateOnly(2026, 9, 29), 1, 5, TimeSpan.FromMinutes(1))),
            ("today", "five_minutes") => Chart("five_minutes", TodayOffers, Bars(new DateOnly(2026, 9, 29), 1, 2, TimeSpan.FromMinutes(5))),
            ("one_week", null) => Chart("ten_minutes", WeekOffers, Bars(new DateOnly(2026, 9, 23), 5, 3, TimeSpan.FromMinutes(10))),
            ("one_week", "five_minutes") => Chart("five_minutes", WeekOffers, Bars(new DateOnly(2026, 9, 23), 5, 4, TimeSpan.FromMinutes(5))),
            ("one_month", null) => FakeAvanza.Json(FxCliTests.Body("035-price-chart")), // recorded: day; offers hour, day, week
            ("three_months", null) => Chart("day", ["day", "week"], Bars(new DateOnly(2026, 7, 1), 60, 1, TimeSpan.FromDays(1))),
            _ => FakeAvanza.Status(HttpStatusCode.BadRequest),
        };
    }

    /// <summary><paramref name="perDay"/> bars from 09:00 Stockholm (07:00 UTC) on each of <paramref name="days"/> weekdays.</summary>
    private static IEnumerable<DateTimeOffset> Bars(DateOnly first, int days, int perDay, TimeSpan step)
    {
        int found = 0;
        for (DateOnly d = first; found < days; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            found++;
            var open = new DateTimeOffset(d.ToDateTime(new TimeOnly(7, 0)), TimeSpan.Zero);
            for (int i = 0; i < perDay; i++)
            {
                yield return step >= TimeSpan.FromDays(1) ? open : open + (i * step);
            }
        }
    }

    private static HttpResponseMessage Chart(string resolution, string[] offers, IEnumerable<DateTimeOffset> starts)
    {
        var ohlc = new JsonArray();
        foreach (DateTimeOffset t in starts)
        {
            ohlc.Add(new JsonObject
            {
                ["timestamp"] = t.ToUnixTimeMilliseconds(),
                ["open"] = 94.9,
                ["close"] = 95.0,
                ["low"] = 94.8,
                ["high"] = 95.1,
                ["totalVolumeTraded"] = 1200,
            });
        }

        var body = new JsonObject
        {
            ["ohlc"] = ohlc,
            ["metadata"] = new JsonObject
            {
                ["resolution"] = new JsonObject { ["chartResolution"] = resolution, ["availableResolutions"] = new JsonArray([.. offers.Select(o => (JsonNode)o)]) },
            },
            ["from"] = "2026-09-23",
            ["to"] = "2026-09-29",
            ["previousClosingPrice"] = 94.96,
        };
        return FakeAvanza.Json(body.ToJsonString());
    }

    [Fact]
    public void TheProbe_AsksEachShortPeriod_ThenTheOfferedMinuteBars_WithoutALogin_AndRecordsTheAnswers()
    {
        (int code, string output, string error) = Qa(Probe("--id", "5240"));

        Assert.True(code == 0, error + output);
        Assert.Contains("Avanza's price chart for orderbook 5240, without a login:", output, StringComparison.Ordinal);
        Assert.Matches(@"today\s+\(its own\)\s+minute\s+5\s+1\s+2026-09-29 09:00\s+2026-09-29 09:04\s+minute, two_minutes, five_minutes", output);
        Assert.Matches(@"today\s+five_minutes\s+five_minutes\s+2\s+1\s", output);
        Assert.Matches(@"one_week\s+\(its own\)\s+ten_minutes\s+15\s+5\s", output);
        Assert.Matches(@"one_week\s+five_minutes\s+five_minutes\s+20\s+5\s+2026-09-23 09:00\s+2026-09-29 09:15", output);
        Assert.Matches(@"one_month\s+\(its own\)\s+day\s+24\s+24\s.*hour, day, week", output);
        Assert.Matches(@"three_months\s+\(its own\)\s+day\s+60\s+60\s", output);
        Assert.Contains("1-minute bars: today 1 day(s) from 2026-09-29.", output, StringComparison.Ordinal);
        Assert.Contains("5-minute bars: today 1 day(s) from 2026-09-29; one_week 5 day(s) from 2026-09-23.", output, StringComparison.Ordinal);

        // Six chart questions, nothing else: no login, no other route, nothing but GET.
        RecordedRequest[] sent = [.. _server.Requests];
        Assert.Equal(6, sent.Length);
        Assert.All(sent, r => Assert.Equal("GET", r.Method));
        Assert.All(sent, r => Assert.StartsWith("/_api/price-chart/stock/5240?timePeriod=", r.PathAndQuery, StringComparison.Ordinal));
        Assert.DoesNotContain(sent, r => r.Headers.ContainsKey("X-SecurityToken"));

        Assert.Contains("Raw answers: ", output, StringComparison.Ordinal);
        Assert.Equal(6, Directory.GetFiles(Path.Combine(_root, "rec"), "*.json", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void OneQuestionFailing_IsAnErrorRow_AndTheProbeGoesOn()
    {
        Fails["one_week&five_minutes"] = HttpStatusCode.BadRequest;

        (int code, string output, string error) = Qa(Probe("--id", "5240", "--no-record"));

        Assert.True(code == 0, error + output);
        Assert.Matches(@"one_week\s+five_minutes\s+ERROR\s+.*HTTP 400", output);
        Assert.Contains("5-minute bars: today 1 day(s) from 2026-09-29.", output, StringComparison.Ordinal);
        Assert.Matches(@"three_months\s+\(its own\)\s+day", output); // it went on
    }

    [Fact]
    public void AChartThatWantsALogin_StopsTheProbe_AndNoLoginIsTried()
    {
        Fails["today&"] = HttpStatusCode.Unauthorized;

        (int code, _, string error) = Qa(Probe("--id", "5240", "--no-record"));

        Assert.NotEqual(0, code);
        Assert.Contains("HALT:", error, StringComparison.Ordinal);
        Assert.Single(_server.Requests); // the one question; no login, no retry
    }

    [Fact]
    public void ATickerNeedsTheInstrumentMaster()
    {
        (int code, _, string error) = Qa(Probe("ERIC-B", "--store", Path.Combine(_root, "missing.duckdb"), "--no-record"));
        Assert.Equal(1, code);
        Assert.Contains("No history store at", error, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);

        (code, _, error) = Qa(Probe("ERIC-B", "--id", "5240", "--no-record"));
        Assert.Equal(1, code);
        Assert.Contains("Give a ticker or --id, not both.", error, StringComparison.Ordinal);
    }
}
