using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuantAnalyst.Avanza.Dto;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Json;
using QuantAnalyst.Avanza.Mapping;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>
/// The app's share search (docs/plans/15-share-search.md) over the recorded 2026-09-25 answers: what a hit shows, and
/// the search session: one login for every search and add, the allowlist rules checked before any import, and an end
/// on Done, when idle, or when the login fails.
/// </summary>
public sealed class MarketSearchTests : IDisposable
{
    private static readonly string Recording = Path.Combine(AppContext.BaseDirectory, "fixtures", "avanza", "2026-09-25");
    private static readonly DateOnly Today = new(2026, 9, 25);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-market-search", Guid.NewGuid().ToString("N"));
    private readonly FakeAvanza _server = new();
    private readonly StubFx _fx = new();

    public MarketSearchTests()
    {
        Directory.CreateDirectory(Config);
        foreach (string file in Directory.GetFiles(Path.Combine(PaperSpyTests.RepoRoot(), "config"), "market-calendar.*.json"))
        {
            File.Copy(file, Path.Combine(Config, Path.GetFileName(file)));
        }

        File.WriteAllText(Path.Combine(Config, Universe.FileName), """{ "format": "qa-universe/1", "instruments": [] }""");
        _server.Always(AvanzaRoutes.Search, _ => FakeAvanza.Json(Body("032-search")));
        _server.Always(AvanzaRoutes.Orderbook, r => FakeAvanza.Json(Orderbook(r.RequestUri!.AbsolutePath.Split('/')[^1])));
        _server.Always(AvanzaRoutes.PriceChart, _ => FakeAvanza.Json(Body("035-price-chart")));
    }

    private string Config => Path.Combine(_root, "config");

    private string Store => Path.Combine(_root, "q.duckdb");

    private string State => Path.Combine(_root, "state");

    private string UniverseFile => Path.Combine(Config, Universe.FileName);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private static string Body(string file)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Recording, file + ".json")));
        return doc.RootElement.GetProperty("response").GetProperty("body").GetRawText();
    }

    /// <summary>The recorded ERIC B orderbook; 61540 is its Helsinki listing (in euro), as the search says.</summary>
    private static string Orderbook(string id)
    {
        JsonNode node = JsonNode.Parse(Body("033-orderbook"))!;
        if (id == "4478")
        {
            node["id"] = "4478";
            node["name"] = "Apple Inc";
            node["isin"] = "US0378331005";
            node["currency"] = "USD";
            node["marketPlace"] = "XNAS";
            node["countryCode"] = "US";
            node["tickerSymbol"] = "AAPL";
        }

        if (id == "61540")
        {
            node["id"] = "61540";
            node["currency"] = "EUR";
            node["marketPlace"] = "XHEL";
            node["countryCode"] = "FI";
            node["tickerSymbol"] = "ERIBR";
        }

        return node.ToJsonString();
    }

    private AvanzaCliServices Services(CancellationToken stop) => new(
        (options, secrets, prompt, logger, redactor) => AvanzaConnection.CreateForTest(
            new AvanzaOptions { StateDirectory = options.StateDirectory, LoginMethod = options.LoginMethod, RequestsPerSecond = 10, Burst = 20 },
            secrets, logger, redactor, TimeProvider.System, _server, prompt),
        _ => FakeSecrets.Store())
    {
        Cancellation = stop,
        FxRates = () => _fx,
    };

    /// <summary>Runs the session the way the app does: on its own task, until Done, idle or a failure.</summary>
    private Task<int> Start(MarketSearchSession session) => StartStoppable(session, CancellationToken.None);

    private Task<int> StartStoppable(MarketSearchSession session, CancellationToken stop) =>
        Task.Run(() => session.RunAsync(Services(stop), State, "totp"), Ct);

    private int Logins => _server.CountFor(AvanzaRoutes.UserCredentials);

    [Fact]
    public void RecordedHits_ShowNameTicker_Country_TodaysChange_AndSector()
    {
        var json = new AvanzaJson(new CapturingLogger());
        using JsonDocument doc = JsonDocument.Parse(Body("032-search"));
        IReadOnlyList<InstrumentSearchHit> hits = AvanzaMapper.ToSearchHits(
            json.Deserialize(doc.RootElement, AvanzaTierBContext.Default.SearchResponseDto, "search", SearchResponseDto.Version, DtoTier.B));

        Assert.Equal(2, hits.Count);
        InstrumentSearchHit sto = hits[0];
        Assert.Equal(new OrderbookId("5240"), sto.OrderbookId);
        Assert.Equal("Ericsson B", sto.Name);
        Assert.Equal("ERIC B", sto.Ticker);
        Assert.Equal("SE", sto.FlagCode);
        Assert.Equal("Stockholmsbörsen", sto.MarketPlaceName);
        Assert.Equal(94.96m, sto.LastPrice);
        Assert.Equal("SEK", sto.Currency);
        Assert.Equal(0.66m, sto.TodayChangePercent);
        Assert.Equal("Technology", sto.Sector);
        Assert.True(sto.Tradeable);

        InstrumentSearchHit hel = hits[1];
        Assert.Equal(new OrderbookId("61540"), hel.OrderbookId);
        Assert.Equal(("Ericsson B", "ERIBR", "FI", "EUR"), (hel.Name, hel.Ticker, hel.FlagCode, hel.Currency));
        Assert.Equal(8.424m, hel.LastPrice);
        Assert.Equal(10.96m, hel.TodayChangePercent);
    }

    [Theory]
    [InlineData("Ericsson B (ERIC B)", "Ericsson B", "ERIC B")]
    [InlineData("  Investor A (INVE A)  ", "Investor A", "INVE A")]
    [InlineData("Svenska Handelsbanken (publ) A (SHB A)", "Svenska Handelsbanken (publ) A", "SHB A")]
    [InlineData("Ericsson B", "Ericsson B", null)]
    [InlineData("Ericsson B ()", "Ericsson B ()", null)]
    [InlineData("(ERIC B)", "(ERIC B)", null)]
    [InlineData("Odd (title) text", "Odd (title) text", null)]
    public void Title_SplitsIntoNameAndTicker(string title, string name, string? ticker) =>
        Assert.Equal((name, ticker), AvanzaMapper.SplitTitle(title));

    [Fact]
    public async Task ASearchWithoutALogin_SendsNoLoginAndNoToken()
    {
        IReadOnlyList<InstrumentSearchHit> hits = await MarketSearchSession.SearchPublicAsync(Services(Ct), State, "eric");

        Assert.Equal(["ERIC B", "ERIBR"], hits.Select(h => h.Ticker));
        Assert.Equal(0, Logins);
        RecordedRequest request = Assert.Single(_server.Requests);
        Assert.StartsWith(AvanzaRoutes.Search.Path(), request.PathAndQuery, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Headers.Keys, k => string.Equals(k, "X-SecurityToken", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ASearchAvanzaRefusesWithoutALogin_SaysSo_AndNothingLogsIn()
    {
        _server.On(AvanzaRoutes.Search, _ => FakeAvanza.Status(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<SessionExpiredException>(() => MarketSearchSession.SearchPublicAsync(Services(Ct), State, "eric"));
        Assert.Equal(0, Logins);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task OneLogin_ServesEverySearchAndTheAdd_ThenDoneEndsIt()
    {
        var session = new MarketSearchSession();
        Task<int> run = Start(session);

        IReadOnlyList<InstrumentSearchHit> first = await session.SearchAsync("eric");
        IReadOnlyList<InstrumentSearchHit> second = await session.SearchAsync("ericsson b");
        AddedShare added = await session.AddAsync(new OrderbookId("5240"), Store, Config, Today);
        session.Close();

        Assert.Equal(0, await run);
        Assert.True(session.HasEnded);
        Assert.Equal("ERIC B", first[0].Ticker);
        Assert.Equal(2, second.Count);
        Assert.Equal(1, Logins);
        Assert.Equal(2, _server.CountFor(AvanzaRoutes.Search));

        // The add imported the daily prices and put the share on the allowlist.
        Assert.Equal(new UniverseEntry(new OrderbookId("5240"), "ERIC B", "Ericsson B"), added.Entry);
        Assert.Equal(24, added.Import.Report.Bars.New);
        Assert.Equal(new DateOnly(2026, 9, 25), added.Import.Report.LastDate);
        Assert.Equal(1, added.Import.Instrument.New);
        Assert.Equal([added.Entry], Universe.Load(UniverseFile).Entries);

        // The chart asked for covers the three years.
        string chart = _server.Requests.Single(r => r.PathAndQuery.StartsWith(AvanzaRoutes.PriceChart.PathTemplate.Split('{')[0], StringComparison.Ordinal)).PathAndQuery;
        Assert.Contains("resolution=day", chart, StringComparison.Ordinal);
        Assert.DoesNotContain("one_month", chart, StringComparison.Ordinal);

        // Plan 21: and stored its dividends and share count.
        Assert.StartsWith("Dividends: ", added.Import.CorporateNote, StringComparison.Ordinal);

        // Nothing but the login and reads was asked of Avanza.
        string[] allowed = [.. new[] { AvanzaRoutes.UserCredentials, AvanzaRoutes.Totp, AvanzaRoutes.Search, AvanzaRoutes.Orderbook, AvanzaRoutes.PriceChart, AvanzaRoutes.StockDetails }
            .Select(r => r.PathTemplate.Split('{')[0])];
        Assert.All(_server.Requests, r => Assert.Contains(allowed, a => r.PathAndQuery.StartsWith(a, StringComparison.Ordinal)));

        // After Done, a search is a new session (and a new login), never this one again.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SearchAsync("volvo"));
        Assert.Contains("search again", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, Logins);
    }

    [Fact]
    public async Task AShareInEuro_IsRefusedBeforeAnyImport_AndTheSessionGoesOn()
    {
        var session = new MarketSearchSession();
        Task<int> run = Start(session);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => session.AddAsync(new OrderbookId("61540"), Store, Config, Today));
        Assert.Contains("ERIBR trades in EUR", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _server.CountFor(AvanzaRoutes.PriceChart));
        Assert.False(File.Exists(Store));
        Assert.Empty(Universe.Load(UniverseFile).Entries);

        // A refusal fails that request alone: the next search still uses the same login.
        Assert.Equal(2, (await session.SearchAsync("eric")).Count);
        Assert.False(session.HasEnded);
        session.Close();
        Assert.Equal(0, await run);
        Assert.Equal(1, Logins);
    }

    [Fact]
    public async Task AUsShare_IsAdded_WithItsFxFixingsFirst()
    {
        var session = new MarketSearchSession();
        Task<int> run = Start(session);

        AddedShare added = await session.AddAsync(new OrderbookId("4478"), Store, Config, Today);
        session.Close();
        Assert.Equal(0, await run);

        Assert.Equal(new UniverseEntry(new OrderbookId("4478"), "AAPL", "Apple Inc"), added.Entry);
        Assert.Equal("USD", added.Import.Fx!.Currency);
        Assert.Equal(("USD", Today.AddYears(-InstrumentImport.AppYears).AddDays(-InstrumentImport.FxLeadDays), Today), Assert.Single(_fx.Calls));
        Assert.Contains(added.Entry, Universe.Load(UniverseFile).Entries);
    }

    [Fact]
    public async Task AFirstNorthShareTradingOnlyInAuctions_IsRefused_BeforeItsImport()
    {
        // Plan 22: the share is measured (one public chart call) before anything is imported.
        _server.Always(AvanzaRoutes.Orderbook, _ => FakeAvanza.Json(Fixtures.Mutate("orderbook-5240.json", n => n["marketPlace"] = "FNSE")));
        _server.Always(AvanzaRoutes.PriceChart, _ => ChartAnswers.TenMinuteWeek(continuous: false));
        var session = new MarketSearchSession();
        Task<int> run = Start(session);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => session.AddAsync(new OrderbookId("5240"), Store, Config, Today));
        session.Close();
        Assert.Equal(0, await run);

        Assert.Contains("ERIC B is listed on 'FNSE' and trades only in auctions (it traded only at the auction times", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, _server.CountFor(AvanzaRoutes.PriceChart)); // last week's 10-minute bars, no daily import
        Assert.Empty(Universe.Load(UniverseFile).Entries);
    }

    [Fact]
    public async Task AFirstNorthShareTradingContinuously_IsAdded_AndTheImportDoesNotMeasureItTwice()
    {
        _server.Always(AvanzaRoutes.Orderbook, _ => FakeAvanza.Json(Fixtures.Mutate("orderbook-5240.json", n => n["marketPlace"] = "FNSE")));
        _server.Always(AvanzaRoutes.PriceChart, r => r.RequestUri!.Query.Contains("ten_minutes", StringComparison.Ordinal)
            ? ChartAnswers.TenMinuteWeek(continuous: true)
            : FakeAvanza.Json(Fixtures.Bytes("price-chart-5240.json")));
        var session = new MarketSearchSession();
        Task<int> run = Start(session);

        AddedShare added = await session.AddAsync(new OrderbookId("5240"), Store, Config, Today);
        session.Close();
        Assert.Equal(0, await run);

        Assert.Equal(Data.Store.TradingModel.Continuous, added.Import.TradingModel!.Model);
        Assert.Single(_server.Requests, r => r.PathAndQuery.Contains("ten_minutes", StringComparison.Ordinal));
        Assert.Contains(added.Entry, Universe.Load(UniverseFile).Entries);
    }

    [Fact]
    public async Task AFullList_IsRefusedBeforeAnyImport()
    {
        new Universe(Enumerable.Range(1, Allowlist.MaxNames).Select(i => new UniverseEntry(new OrderbookId($"{i}"), $"T{i}", $"Name {i}"))).Save(UniverseFile);
        var session = new MarketSearchSession();
        Task<int> run = Start(session);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => session.AddAsync(new OrderbookId("5240"), Store, Config, Today));
        Assert.Contains($"already has {Allowlist.MaxNames} names", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _server.CountFor(AvanzaRoutes.PriceChart));
        Assert.Equal(Allowlist.MaxNames, Universe.Load(UniverseFile).Entries.Count);

        session.Close();
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task AFullList_MakesRoomWithARemove_InTheSameLogin()
    {
        new Universe(Enumerable.Range(1, Allowlist.MaxNames).Select(i => new UniverseEntry(new OrderbookId($"{i}"), $"T{i} B", $"Name {i}"))).Save(UniverseFile);
        var session = new MarketSearchSession();
        Task<int> run = Start(session);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => session.RemoveAsync("NOPE", Config));
        Assert.Equal("NOPE is not in the allowlist.", ex.Message);
        Assert.Equal("removed T1 B (1)", await session.RemoveAsync("t1-b", Config)); // written as on the command line
        AddedShare added = await session.AddAsync(new OrderbookId("5240"), Store, Config, Today);
        session.Close();
        Assert.Equal(0, await run);

        Assert.Equal(Allowlist.MaxNames, Universe.Load(UniverseFile).Entries.Count);
        Assert.Contains(added.Entry, Universe.Load(UniverseFile).Entries);
        Assert.Equal(1, Logins);
    }

    [Fact]
    public async Task ANameAlreadyOnTheList_IsAddedAgain_WithoutCountingTwice()
    {
        var session = new MarketSearchSession();
        Task<int> run = Start(session);
        AddedShare once = await session.AddAsync(new OrderbookId("5240"), Store, Config, Today);
        AddedShare twice = await session.AddAsync(new OrderbookId("5240"), Store, Config, Today);
        session.Close();
        await run;

        Assert.Equal(once.Entry, twice.Entry);
        Assert.Equal(0, twice.Import.Report.Bars.New);
        Assert.Single(Universe.Load(UniverseFile).Entries);
    }

    [Fact]
    public async Task Idle_LetsTheLoginGo_AndLaterSearchesStartANewSession()
    {
        var session = new MarketSearchSession { IdleTimeout = TimeSpan.FromMilliseconds(200) };
        Task<int> run = Start(session);
        await session.SearchAsync("eric");

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.True(session.HasEnded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SearchAsync("eric"));
        Assert.Equal(1, Logins);
    }

    [Fact]
    public async Task AFailedLogin_FailsTheWaitingSearch_AndIsNotTriedAgain()
    {
        _server.On(AvanzaRoutes.UserCredentials, _ => FakeAvanza.Status(HttpStatusCode.Unauthorized));
        var session = new MarketSearchSession();
        Task<IReadOnlyList<InstrumentSearchHit>> waiting = session.SearchAsync("eric");
        Task<int> run = Start(session);

        await Assert.ThrowsAsync<LoginFailedException>(() => run);
        await Assert.ThrowsAsync<LoginFailedException>(() => waiting);
        Assert.True(session.HasEnded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SearchAsync("eric"));
        Assert.Equal(1, Logins);
        Assert.Equal(0, _server.CountFor(AvanzaRoutes.Search));
    }

    [Fact]
    public async Task AnExpiredSession_EndsIt_AndTheWaitingRequestsFailWithIt()
    {
        _server.OnAsync(AvanzaRoutes.Search, async (_, ct) =>
        {
            await Task.Delay(100, ct);
            return FakeAvanza.Status(HttpStatusCode.Unauthorized);
        });
        var session = new MarketSearchSession();
        Task<int> run = Start(session);
        Task<IReadOnlyList<InstrumentSearchHit>> expires = session.SearchAsync("eric");
        Task<IReadOnlyList<InstrumentSearchHit>> waiting = session.SearchAsync("volvo");

        await Assert.ThrowsAsync<SessionExpiredException>(() => expires);
        await Assert.ThrowsAsync<SessionExpiredException>(() => waiting);
        await Assert.ThrowsAsync<SessionExpiredException>(() => run);
        Assert.True(session.HasEnded);
        Assert.Equal(1, Logins);
        Assert.Equal(1, _server.CountFor(AvanzaRoutes.Search));
    }

    [Fact]
    public async Task AMovedTradingCriticalEndpoint_EndsIt()
    {
        // ADR 0002: a 404 on the orderbook (Tier A) means the endpoint moved; nothing more is asked of Avanza.
        _server.On(AvanzaRoutes.Orderbook, _ => FakeAvanza.Status(HttpStatusCode.NotFound));
        var session = new MarketSearchSession();
        Task<int> run = Start(session);

        await Assert.ThrowsAsync<EndpointGoneException>(() => session.AddAsync(new OrderbookId("5240"), Store, Config, Today));
        await Assert.ThrowsAsync<EndpointGoneException>(() => run);
        Assert.True(session.HasEnded);
        Assert.Equal(0, _server.CountFor(AvanzaRoutes.PriceChart));
        Assert.Empty(Universe.Load(UniverseFile).Entries);
    }

    [Fact]
    public async Task Stop_EndsTheSession()
    {
        using var stop = new CancellationTokenSource();
        var session = new MarketSearchSession();
        Task<int> run = StartStoppable(session, stop.Token);
        await session.SearchAsync("eric");

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(session.HasEnded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SearchAsync("eric"));
    }

    /// <summary>A flat 9.40 SEK per unit on every weekday asked for.</summary>
    private sealed class StubFx : IFxRateSource
    {
        public List<(string Currency, DateOnly First, DateOnly Last)> Calls { get; } = [];

        public DataSourceInfo Source => Data.Fx.RiksbankFxSource.Riksbank;

        public string SourceVersion => "test";

        public Task<IReadOnlyList<FxRate>> GetDailyAsync(string currency, DateOnly first, DateOnly last, CancellationToken ct)
        {
            Calls.Add((currency, first, last));
            IReadOnlyList<FxRate> rates = [.. Enumerable.Range(0, last.DayNumber - first.DayNumber + 1).Select(first.AddDays)
                .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).Select(d => new FxRate(d, 9.4m))];
            return Task.FromResult(rates);
        }
    }
}
