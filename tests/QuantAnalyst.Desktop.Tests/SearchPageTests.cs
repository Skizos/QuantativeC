using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.ViewModels;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>
/// The Instruments page's <b>Find a share</b> (docs/plans/15-share-search.md): search as you type, each hit with Add or
/// why not, Add, Done, and the one login it keeps. The search itself is faked here; the session behind it is tested
/// over the recorded Avanza answers in QuantAnalyst.Avanza.Tests (MarketSearchTests).
/// </summary>
public sealed class SearchPageTests : IDisposable
{
    private static readonly InstrumentSearchHit EricB = new(new OrderbookId("5240"), "Ericsson B", "STOCK", "Stockholmsbörsen", true, 94.96m, "SEK")
    {
        Ticker = "ERIC B",
        FlagCode = "SE",
        TodayChangePercent = 0.66m,
        Sector = "Technology",
    };

    private static readonly InstrumentSearchHit EricHelsinki = new(new OrderbookId("61540"), "Ericsson B", "STOCK", "Helsingforsbörsen", true, 8.424m, "EUR")
    {
        Ticker = "ERIBR",
        FlagCode = "FI",
        TodayChangePercent = 10.96m,
        Sector = "Technology",
    };

    private static readonly InstrumentSearchHit Evolution = new(new OrderbookId("549768"), "Evolution", "STOCK", "Stockholmsbörsen", true, 812.4m, "SEK")
    {
        Ticker = "EVO",
        FlagCode = "SE",
        TodayChangePercent = -1.2m,
    };

    private readonly TempWorkspace _ws = new();
    private readonly FakeSearch _search;

    public SearchPageTests() => _search = new FakeSearch(_ws);

    public void Dispose() => _ws.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ShellViewModel Shell(ScriptedRunner? runner = null)
    {
        QaEngine engine = runner is null ? new QaEngine(new ImmediateDispatcher()) : new QaEngine(new ImmediateDispatcher(), runner.Run, null);
        return new ShellViewModel(_ws.Workspace, engine, _ws.Time, new EngineAccountSource(engine, _ws.Workspace), new FakeEnvironment(), _search);
    }

    /// <summary>Waits for what a timer or a background task sets off (at most 10 s).</summary>
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task Typing_SearchesAfterAPause_AndOnlyForTheLastText()
    {
        InstrumentsViewModel page = Shell().Instruments;
        _search.Answer("eric", [EricB, EricHelsinki]);

        foreach (string text in new[] { "e", "er", "eri", "eric" })
        {
            page.SearchText = text;
            _ws.Time.Advance(TimeSpan.FromMilliseconds(100)); // quicker than the pause
        }

        Assert.Empty(_search.Queries);
        _ws.Time.Advance(InstrumentsViewModel.TypingPause);
        await Until(() => page.Results.Count == 2);

        Assert.Equal(["eric"], _search.Queries);
        Assert.Equal("2 shares.", page.SearchStatus);
        Assert.False(page.SearchStatusIsError);
        Assert.False(page.IsSearchOpen); // searching needs no login

        // One character is not searched; an empty box clears the hits.
        page.SearchText = "e";
        await page.SearchCommand.ExecuteAsync(); // Enter is not offered for one character
        Assert.False(page.SearchCommand.CanExecute(null));
        _ws.Time.Advance(InstrumentsViewModel.TypingPause);
        await Until(() => page.Results.Count == 0);
        Assert.Equal("Type at least 2 characters.", page.SearchStatus);
        Assert.Equal(["eric"], _search.Queries);
    }

    [Fact]
    public async Task Enter_SearchesAtOnce_WithoutASecondSearchAfterThePause()
    {
        InstrumentsViewModel page = Shell().Instruments;
        _search.Answer("volvo", []);
        page.SearchText = "volvo";
        await page.SearchCommand.ExecuteAsync();
        _ws.Time.Advance(InstrumentsViewModel.TypingPause * 2);

        Assert.Equal(["volvo"], _search.Queries);
        Assert.Equal("No shares match “volvo”.", page.SearchStatus);
        Assert.Empty(page.Results);
    }

    [Fact]
    public async Task EachHit_ShowsWhoWhereAndToday_AndWhetherItCanBeAdded_OrWhyNot()
    {
        _ws.AllowEricB();
        InstrumentsViewModel page = Shell().Instruments;
        await page.RefreshAsync();
        var closed = new InstrumentSearchHit(new OrderbookId("1"), "Delisted", "STOCK", "Stockholmsbörsen", false, 5m, "SEK") { Ticker = "DEL" };
        var anon = new InstrumentSearchHit(new OrderbookId("2"), "No Ticker AB", "STOCK", "First North Stockholm", true, 5m, "SEK");
        var cheap = new InstrumentSearchHit(new OrderbookId("3"), "Volvo B", "STOCK", "Stockholmsbörsen", true, 250.1m, "SEK") { Ticker = "VOLV B", FlagCode = "SE" };
        _search.Answer("any", [EricB, EricHelsinki, Evolution, closed, anon, cheap]);
        page.SearchText = "any";
        await page.SearchCommand.ExecuteAsync();

        SearchResultRow eric = page.Results[0];
        Assert.Equal(("ERIC B", "Ericsson B", "SE · Stockholmsbörsen", "94,96 kr", "Technology"), (eric.Ticker, eric.Name, eric.Market, eric.Price, eric.Sector));
        Assert.Equal(("▲ +0,66 %", "up"), (eric.Change, eric.Direction));
        Assert.Equal((false, true, "On your list"), (eric.CanAdd, eric.IsOnList, eric.Why));

        SearchResultRow helsinki = page.Results[1];
        Assert.Equal(("FI · Helsingforsbörsen", "8,424 EUR"), (helsinki.Market, helsinki.Price));
        Assert.False(helsinki.CanAdd);
        Assert.StartsWith("Trades in EUR", helsinki.Why, StringComparison.Ordinal);

        SearchResultRow evo = page.Results[2];
        Assert.Equal("down", evo.Direction);
        Assert.False(evo.CanAdd);
        Assert.Equal("One share costs more than an order may (500,00 kr)", evo.Why); // R6 at the 5 000 kr cap

        Assert.Equal("Not tradable at Avanza", page.Results[3].Why);
        Assert.Equal("Avanza shows no ticker for it", page.Results[4].Why);
        Assert.True(page.Results[5].CanAdd);
        Assert.Equal(string.Empty, page.Results[5].Why);

        Assert.False(page.AddResultCommand.CanExecute(eric));
        Assert.True(page.AddResultCommand.CanExecute(page.Results[5]));
        Assert.Contains("One order may be at most 500,00 kr", page.SearchHint, StringComparison.Ordinal);
        Assert.Contains("Add imports 3 years of daily prices", page.SearchHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsAndCanadianShares_CanBeAdded_AndShowTheirPriceInKronorAtTheLatestFixing()
    {
        var apple = new InstrumentSearchHit(new OrderbookId("4478"), "Apple Inc", "STOCK", "NASDAQ", true, 250m, "USD") { Ticker = "AAPL", FlagCode = "US" };
        var ford = new InstrumentSearchHit(new OrderbookId("4400"), "Ford Motor Co", "STOCK", "NYSE", true, 12.5m, "USD") { Ticker = "F", FlagCode = "US" };
        var shopify = new InstrumentSearchHit(new OrderbookId("9001"), "Shopify Inc", "STOCK", "Toronto Stock Exchange", true, 30m, "CAD") { Ticker = "SHOP", FlagCode = "CA" };
        InstrumentsViewModel page = Shell().Instruments;
        _search.Answer("us", [apple, ford, shopify]);
        page.SearchText = "us";
        await page.SearchCommand.ExecuteAsync();

        // No fixing stored yet: no SEK value, and the price can't be judged, so Add decides (it imports the rates first).
        Assert.All(page.Results, r => Assert.True(r.CanAdd, r.Why));
        Assert.All(page.Results, r => Assert.Equal(string.Empty, r.PriceSek));
        Assert.Equal("250,00 USD", page.Results[0].Price);

        using (var store = Data.Store.HistoryStore.Open(_ws.Workspace.Store))
        {
            store.RegisterSource(Data.Fx.RiksbankFxSource.Riksbank);
            store.UpsertFxRates("USD", [new QuantAnalyst.Core.Market.FxRate(new DateOnly(2026, 9, 25), 9.4m)], Data.Fx.RiksbankFxSource.Riksbank, "test", _ws.Time.GetUtcNow());
        }

        await page.SearchNowAsync();
        SearchResultRow aapl = page.Results[0];
        Assert.Equal("≈ 2\u00A0350 kr", aapl.PriceSek);
        Assert.False(aapl.CanAdd);
        Assert.Equal("One share costs more than an order may (500,00 kr)", aapl.Why); // 250 USD = 2 350 kr > R6
        Assert.Equal(("≈ 118 kr", true), (page.Results[1].PriceSek, page.Results[1].CanAdd));
        Assert.Equal((string.Empty, true), (page.Results[2].PriceSek, page.Results[2].CanAdd)); // no CAD fixing stored
        Assert.Contains("US and Canadian ones trade on paper", page.SearchHint, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherCurrencies_StayOut_WithTheReason()
    {
        var oslo = new InstrumentSearchHit(new OrderbookId("1"), "Equinor", "STOCK", "Oslo Børs", true, 250m, "NOK") { Ticker = "EQNR" };
        Assert.Equal((false, false, "Trades in NOK: the program trades shares in SEK, USD, CAD"), InstrumentsViewModel.Verdict(oslo, Universe.Empty, 500m));
        Assert.Equal(94.96m, InstrumentsViewModel.SekPrice(EricB, null)); // a SEK share needs no rate
        Assert.Null(InstrumentsViewModel.SekPrice(EricHelsinki, null));
        Assert.Equal(84.24m, InstrumentsViewModel.SekPrice(EricHelsinki, 10m));
    }

    [Fact]
    public void AFullList_TakesNoMoreNames_ButANameOnItStillShowsAsOnIt()
    {
        var full = new Universe(Enumerable.Range(1, Allowlist.MaxNames).Select(i => new UniverseEntry(new OrderbookId($"{i}"), $"T{i}", $"Name {i}")));
        Assert.Equal((false, false, "Your list is full (5 names): remove one first"), InstrumentsViewModel.Verdict(EricB, full, 500m));
        var onIt = new InstrumentSearchHit(new OrderbookId("3"), "Name 3", "STOCK", "Stockholmsbörsen", true, 10m, "SEK") { Ticker = "T3" };
        Assert.Equal((false, true, "On your list"), InstrumentsViewModel.Verdict(onIt, full, 500m));
        Assert.Equal((true, false, string.Empty), InstrumentsViewModel.Verdict(Evolution, Universe.Empty, null)); // no limit known: Add decides
    }

    [Fact]
    public async Task Add_ImportsAndAllowsTheShare_ThenItsHitSaysOnYourList_AndTheListShowsIt()
    {
        ShellViewModel shell = Shell();
        InstrumentsViewModel page = shell.Instruments;
        shell.SelectedPage = page;
        _search.Answer("eric", [EricB, EricHelsinki]);
        page.SearchText = "eric";
        await page.SearchCommand.ExecuteAsync();
        Assert.True(page.Results[0].CanAdd);

        await page.AddResultCommand.ExecuteAsync(page.Results[0]);

        Assert.Equal([new OrderbookId("5240")], _search.Adds);
        Assert.Equal("ERIC B added (752 new daily prices to 2026-09-25). The next session trades it.", page.SearchStatus);
        Assert.False(page.SearchStatusIsError);
        InstrumentRow row = Assert.Single(page.Rows);
        Assert.Equal("ERIC B", row.Ticker);
        Assert.Same(row, page.SelectedRow);
        Assert.Equal((false, true, "On your list"), (page.Results[0].CanAdd, page.Results[0].IsOnList, page.Results[0].Why));
        Assert.True(page.IsSearchOpen); // the add's login stays for the next add
    }

    [Fact]
    public async Task AFailedAdd_SaysWhy_AndKeepsTheHits()
    {
        InstrumentsViewModel page = Shell().Instruments;
        _search.Answer("eric", [EricB]);
        page.SearchText = "eric";
        await page.SearchCommand.ExecuteAsync();
        _search.AddFails = new ArgumentException("ERIC B trades in EUR; v1 trades SEK instruments only.");

        await page.AddResultCommand.ExecuteAsync(page.Results[0]);

        Assert.True(page.SearchStatusIsError);
        Assert.Equal("Could not add ERIC B: ERIC B trades in EUR; v1 trades SEK instruments only.", page.SearchStatus);
        Assert.Single(page.Results);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task AFailedSearch_SaysWhy_AndAnOlderAnswerArrivingLateIsDropped()
    {
        InstrumentsViewModel page = Shell().Instruments;
        _search.Fail("bad", new LoginFailedException("BankID was cancelled"));
        page.SearchText = "bad";
        await page.SearchCommand.ExecuteAsync();
        Assert.True(page.SearchStatusIsError);
        Assert.StartsWith("The search failed: Login failed: BankID was cancelled", page.SearchStatus, StringComparison.Ordinal);

        var slow = new TaskCompletionSource<IReadOnlyList<InstrumentSearchHit>>();
        _search.Answer("er", slow.Task);
        _search.Answer("eric", [EricB]);
        page.SearchText = "er";
        Task first = page.SearchCommand.ExecuteAsync();
        page.SearchText = "eric";
        await page.SearchNowAsync();
        slow.SetResult([EricHelsinki, EricB]);
        await first;

        Assert.Equal("ERIC B", Assert.Single(page.Results).Ticker);
        Assert.Equal("1 share.", page.SearchStatus);
    }

    [Fact]
    public async Task Done_LetsTheLoginGo_AndClearsTheSearch()
    {
        InstrumentsViewModel page = Shell().Instruments;
        Assert.False(page.DoneCommand.CanExecute(null));
        _search.Answer("eric", [EricB]);
        page.SearchText = "eric";
        await page.SearchCommand.ExecuteAsync();
        Assert.True(page.DoneCommand.CanExecute(null));

        page.DoneCommand.Execute(null);

        Assert.Equal(1, _search.Closes);
        Assert.False(page.IsSearchOpen);
        Assert.Empty(page.Results);
        Assert.Equal(string.Empty, page.SearchText);
        Assert.Equal(string.Empty, page.SearchStatus);
        _ws.Time.Advance(InstrumentsViewModel.TypingPause); // clearing the box searches nothing
        Assert.Equal(["eric"], _search.Queries);
    }

    [Fact]
    public async Task LeavingThePage_LetsTheLoginGo_ButShowingItBesideOrInAWindowKeepsIt()
    {
        ShellViewModel shell = Shell();
        InstrumentsViewModel page = shell.Instruments;
        _search.Answer("eric", [EricB]);

        // Beside another page: shown, so the search stays while the left page changes.
        shell.SelectedPage = shell.Charts;
        shell.SelectedBeside = shell.BesideChoices.Single(c => c.Kind == PageKind.Instruments);
        page.SearchText = "eric";
        await page.SearchCommand.ExecuteAsync();
        shell.SelectedPage = shell.Accounts;
        Assert.Equal(0, _search.Closes);
        Assert.Single(page.Results); // the search is kept

        // In a window of its own while the beside pane closes: still shown.
        shell.OpenInWindow(page);
        shell.SelectedBeside = shell.BesideChoices[0];
        Assert.Equal(0, _search.Closes);

        // The window closes and nothing shows the page any more: the login is let go.
        shell.WindowClosed(page);
        Assert.Equal(1, _search.Closes);
        Assert.Empty(page.Results);

        // Navigating away from it, too.
        shell.SelectedPage = page;
        page.SearchText = "eric";
        await page.SearchCommand.ExecuteAsync();
        shell.SelectedPage = shell.Status;
        Assert.Equal(2, _search.Closes);
    }

    [Fact]
    public async Task WhileTheAddLoginIsOpen_RemoveGoesThroughIt_NotAsASecondCommand()
    {
        var runner = new ScriptedRunner();
        InstrumentsViewModel page = Shell(runner).Instruments;
        _search.Answer("eric", [EricB]);
        page.SearchText = "eric";
        await page.SearchCommand.ExecuteAsync();
        await page.AddResultCommand.ExecuteAsync(page.Results[0]); // opens the login
        Assert.False(page.Results[0].CanAdd);

        await page.RemoveCommand.ExecuteAsync(page.Rows[0]);

        Assert.Equal(["ERIC B"], _search.Removes);
        Assert.Empty(runner.Calls);
        Assert.Empty(page.Rows);
        Assert.Equal("ERIC B removed from the allowlist.", page.Message);
        Assert.True(page.Results[0].CanAdd); // room again: the hit offers Add
    }

    [Fact]
    public async Task WithoutAnOpenLogin_RemoveRunsUniverseRemove()
    {
        _ws.AllowEricB();
        var runner = new ScriptedRunner();
        InstrumentsViewModel page = Shell(runner).Instruments;
        await page.RefreshAsync();
        _search.Answer("eric", [EricB]);
        page.SearchText = "eric";
        await page.SearchCommand.ExecuteAsync();

        await page.RemoveCommand.ExecuteAsync(page.Rows[0]);

        Assert.Empty(_search.Removes);
        Assert.Equal(["universe", "remove", "ERIC B"], runner.Calls.Single().Take(3));
    }

    [Fact]
    public async Task WhileAnotherCommandRuns_SearchingStillWorks_ButAddWaits()
    {
        var started = new TaskCompletionSource();
        int Blocking(string[] args, TextWriter output, TextWriter error, AvanzaCliServices services)
        {
            started.SetResult();
            services.Cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            return 0;
        }

        var engine = new QaEngine(new ImmediateDispatcher(), Blocking, null);
        var shell = new ShellViewModel(_ws.Workspace, engine, _ws.Time, new EngineAccountSource(engine, _ws.Workspace), new FakeEnvironment(), _search);
        Task<CommandResult> running = engine.RunAsync("Paper session", ["paper", "run"]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        InstrumentsViewModel page = shell.Instruments;
        _search.Answer("eric", [EricB]);
        page.SearchText = "eric";
        Assert.True(page.SearchCommand.CanExecute(null));
        await page.SearchCommand.ExecuteAsync();
        Assert.Equal(["eric"], _search.Queries);
        Assert.True(page.Results[0].CanAdd);
        Assert.False(page.AddResultCommand.CanExecute(page.Results[0])); // adding logs in: after the session

        // Had Avanza wanted a login to search, the search would wait too.
        _search.NeedsLogin = true;
        await page.SearchNowAsync();
        Assert.Equal(["eric"], _search.Queries);
        Assert.True(page.SearchStatusIsError);
        Assert.Equal("'Paper session' is running; search when it has finished (Avanza wants a login to search).", page.SearchStatus);

        engine.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task TheRealSearch_TriesWithoutALogin_AndLogsInOnceOnlyWhenAvanzaRefuses()
    {
        int publicTries = 0;
        int logins = 0;
        var services = new AvanzaCliServices(
            (options, _, _, _, _) =>
            {
                if (options.LoginMethod == Avanza.Auth.AvanzaLoginMethod.Totp)
                {
                    Interlocked.Increment(ref logins);
                    throw new LoginFailedException("rejected");
                }

                Interlocked.Increment(ref publicTries);
                throw new SessionExpiredException("search", 401); // Avanza wants a login to search
            },
            _ => null!); // no secret is read
        var engine = new QaEngine(new ImmediateDispatcher(), null, services);
        var search = new EngineMarketSearch(engine, _ws.Workspace, _ws.Time, () => "totp");
        Assert.False(search.SearchNeedsLogin);

        var ex = await Assert.ThrowsAsync<LoginFailedException>(() => search.SearchAsync("eric").WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Contains("rejected", ex.Message, StringComparison.Ordinal);
        Assert.Equal((1, 1), (publicTries, logins));
        await Until(() => !engine.IsBusy);
        Assert.False(search.IsOpen);
        Assert.True(search.SearchNeedsLogin);

        // For the rest of the run a search goes straight to one login attempt: no second anonymous try, no retry loop.
        await Assert.ThrowsAsync<LoginFailedException>(() => search.SearchAsync("eric").WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal((1, 2), (publicTries, logins));
        await Assert.ThrowsAsync<InvalidOperationException>(() => search.RemoveAsync("ERIC B"));
    }

    [Fact]
    public async Task TheRealSearch_AnyOtherProblemWithoutALogin_IsShown_AndNothingLogsIn()
    {
        int logins = 0;
        var services = new AvanzaCliServices(
            (options, _, _, _, _) =>
            {
                if (options.LoginMethod == Avanza.Auth.AvanzaLoginMethod.Totp)
                {
                    Interlocked.Increment(ref logins);
                }

                throw new BrokerUnavailableException("search", "Avanza returned HTTP 503 for 'search'.", 503);
            },
            _ => null!);
        var engine = new QaEngine(new ImmediateDispatcher(), null, services);
        var search = new EngineMarketSearch(engine, _ws.Workspace, _ws.Time, () => "totp");

        await Assert.ThrowsAsync<BrokerUnavailableException>(() => search.SearchAsync("eric").WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(0, logins);
        Assert.False(search.SearchNeedsLogin);
        Assert.False(engine.IsBusy); // a search without a login never occupies the app
    }

    /// <summary>Answers searches from a script; an add writes the allowlist the way the real one does.</summary>
    private sealed class FakeSearch(TempWorkspace ws) : IMarketSearch
    {
        private readonly Dictionary<string, Task<IReadOnlyList<InstrumentSearchHit>>> _answers = new(StringComparer.Ordinal);

        public List<string> Queries { get; } = [];

        public List<OrderbookId> Adds { get; } = [];

        public List<string> Removes { get; } = [];

        public int Closes { get; private set; }

        public Exception? AddFails { get; set; }

        public bool IsOpen { get; private set; }

        /// <summary>Gets or sets whether Avanza wanted a login to search (then a search opens the login too).</summary>
        public bool NeedsLogin { get; set; }

        public bool SearchNeedsLogin => NeedsLogin && !IsOpen;

        public void Answer(string query, IReadOnlyList<InstrumentSearchHit> hits) => _answers[query] = Task.FromResult(hits);

        public void Answer(string query, Task<IReadOnlyList<InstrumentSearchHit>> hits) => _answers[query] = hits;

        public void Fail(string query, Exception ex) => _answers[query] = Task.FromException<IReadOnlyList<InstrumentSearchHit>>(ex);

        public Task<IReadOnlyList<InstrumentSearchHit>> SearchAsync(string query)
        {
            Queries.Add(query);
            IsOpen |= NeedsLogin;
            return _answers[query];
        }

        public Task<ShareAdded> AddAsync(OrderbookId id)
        {
            Adds.Add(id);
            IsOpen = true;
            if (AddFails is { } fail)
            {
                return Task.FromException<ShareAdded>(fail);
            }

            string path = Path.Combine(ws.Workspace.ConfigDir, Universe.FileName);
            Universe.Load(path).With(new UniverseEntry(id, "ERIC B", "Ericsson B")).Save(path);
            return Task.FromResult(new ShareAdded("ERIC B", "Ericsson B", 752, new DateOnly(2026, 9, 25), null));
        }

        public Task RemoveAsync(string ticker)
        {
            Removes.Add(ticker);
            IsOpen = true;
            string path = Path.Combine(ws.Workspace.ConfigDir, Universe.FileName);
            Universe u = Universe.Load(path);
            u.Without(u.Entries.Single(e => e.Ticker == ticker).OrderbookId).Save(path);
            return Task.CompletedTask;
        }

        public void Close()
        {
            Closes++;
            IsOpen = false;
        }
    }
}
