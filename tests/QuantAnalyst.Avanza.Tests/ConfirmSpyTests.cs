using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Orders;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Modes;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>Lines typed by the test, one at a time; <see cref="ReadLine"/> blocks until one is typed.</summary>
internal sealed class TypedLines : TextReader
{
    private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();

    public void Type(string? line) => _lines.Writer.TryWrite(line);

    public void EndInput() => _lines.Writer.TryComplete();

    public override string? ReadLine() => _lines.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
}

/// <summary>Output a test can read while the command still writes it.</summary>
internal sealed class SharedWriter : TextWriter
{
    private readonly StringBuilder _text = new();

    public override Encoding Encoding => Encoding.UTF8;

    public string Text
    {
        get
        {
            lock (_text)
            {
                return _text.ToString();
            }
        }
    }

    public override void Write(char value)
    {
        lock (_text)
        {
            _text.Append(value);
        }
    }

    public override void Write(string? value)
    {
        lock (_text)
        {
            _text.Append(value);
        }
    }
}

/// <summary>
/// The Avanza channel as it will be once the owner's capture finalises its format (plan 07 step 7): the same requests to
/// the same routes, but it no longer says it is not ready. Only these tests use it, so the Confirm spy can show what a
/// confirmed card sends.
/// </summary>
internal sealed class FinalFormatChannel(AvanzaOrderChannel inner) : IBrokerOrderChannel
{
    public string Name => inner.Name;

    public string? NotReadyReason => null;

    public Task<OrderSubmitResult> PlaceAsync(ApprovedOrder order, CancellationToken ct) => inner.PlaceAsync(order, ct);

    public Task<OrderSubmitResult> ModifyAsync(ApprovedModify modify, CancellationToken ct) => inner.ModifyAsync(modify, ct);

    public Task<OrderSubmitResult> CancelAsync(ApprovedCancel cancel, CancellationToken ct) => inner.CancelAsync(cancel, ct);
}

/// <summary>
/// The Confirm spy (plan 07 gate): whole <c>qa trade run --mode confirm</c> and <c>qa rebalance</c> runs, in-process,
/// against the fake Avanza server on a fake clock through Monday 2026-09-28's decision time, with a scripted keyboard.
/// The server records every request:
/// <list type="bullet">
/// <item>a session in which nothing is confirmed sends <b>zero</b> requests to an order route;</item>
/// <item>each typed <c>ERIC-B JA</c> sends exactly <b>one</b> place request, after a second Avanza check;</item>
/// <item>a wrong, late or re-check-failed answer sends none;</item>
/// <item>a start the checks refuse sends nothing, and the checks that need no login refuse before any login.</item>
/// </list>
/// </summary>
public sealed class ConfirmSpyTests : IDisposable
{
    private const string Card = "Type  ERIC-B JA  within 30 s to send.";
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 7, 9, 40, TimeSpan.Zero); // 09:09:40 Stockholm

    private static readonly string EmptyPositions = """
        {"withOrderbook":[],"withoutOrderbook":[],"cashPositions":[{"account":{"id":"9990001","type":"INVESTERINGSSPARKONTO","name":"Algo ISK","urlParameterId":"url-1","hasCredit":false},"totalBalance":{"value":12345.67,"unit":"SEK","unitType":"MONETARY","decimalPrecision":2},"id":"cash-1"}],"withCreditAccount":false}
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-confirm-spy", Guid.NewGuid().ToString("N"));
    private readonly FakeAvanza _server = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly TypedLines _input = new();
    private readonly MemoryPromotionKeys _keys = new() { Key = Promotion.NewKey() };
    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal) { ["AVANZA__ALLOWEDACCOUNTIDS"] = "9990001" };
    private bool _finalFormat = true;

    public ConfirmSpyTests()
    {
        Directory.CreateDirectory(Config);
        string repo = Path.Combine(PaperSpyTests.RepoRoot(), "config");
        foreach (string f in new[] { "risk-limits.json", "costs.avanza-start.json", "market-calendar.XSTO.2026.json", "market-calendar.XSTO.2027.json" })
        {
            File.Copy(Path.Combine(repo, f), Path.Combine(Config, f));
        }

        // R20: this test's calendar is "verified" (the committed one waits for the owner, O2).
        string calendar = Path.Combine(Config, "market-calendar.XSTO.2026.json");
        File.WriteAllText(calendar, File.ReadAllText(calendar).Replace("\"verified_on\": null", "\"verified_on\": \"2026-09-01\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(Config, "paper.json"), """{ "format": "qa-paper/1", "costs": "avanza-start", "cash": 45000, "decision_time": "09:10" }""");
        File.WriteAllText(Path.Combine(Config, "universe.json"), """{ "format": "qa-universe/1", "instruments": [] }""");

        // The owner's promotion to Confirm, signed with the test's key.
        Promotion.Append(PromotionDir, Promotion.Sign(
            new PromotionRecord("Confirm", "Paper", Start.AddDays(-1), "owner", [], Promotion.EvidenceHash([]), ["[ok] spy gate"], string.Empty), _keys.Key!));

        // The ISK holds no shares and has no open orders, so the strategy buys; an order sent is answered SUCCESS.
        _server.Always(AvanzaRoutes.Positions, _ => FakeAvanza.Json(EmptyPositions));
        _server.Always(AvanzaRoutes.Orders, _ => FakeAvanza.Json("""{"orders":[],"fundOrders":[],"cancelledOrders":[]}"""));
        _server.Always(AvanzaOrderRoutes.Place, _ => FakeAvanza.Json(Fixtures.Bytes("order-request-success.json")));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Config => Path.Combine(_root, "config");

    private string Store => Path.Combine(_root, "q.duckdb");

    private string State => Path.Combine(_root, "state");

    private string Audit => Path.Combine(_root, "audit");

    private string KillFile => Path.Combine(_root, "KILL");

    private string PromotionDir => Path.Combine(_root, "promotion");

    public void Dispose()
    {
        _input.EndInput();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private AvanzaCliServices Services(TimeProvider time) => new(
        (options, secrets, prompt, logger, redactor) => AvanzaConnection.CreateForTest(
            new AvanzaOptions { StateDirectory = options.StateDirectory, LoginMethod = options.LoginMethod, RequestsPerSecond = 10, Burst = 20 },
            secrets, logger, redactor, time, _server, prompt),
        _ => FakeSecrets.Store())
    {
        Time = time,
        Input = _input,
        GetVariable = name => _variables.GetValueOrDefault(name),
        PromotionKeys = () => _keys,
        OrderChannel = connection => _finalFormat ? new FinalFormatChannel(connection.CreateOrderChannel()) : connection.CreateOrderChannel(),
    };

    private (int Code, string Output, string Error) Qa(TimeProvider time, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = QaCli.Run(args, output, error, Services(time));
        return (code, output.ToString(), error.ToString());
    }

    private void PrepareHistoryAndUniverse()
    {
        Assert.Equal(0, Qa(TimeProvider.System, "history", "import", "ERIC-B", "--from", "2026-09-24", "--to", "2026-09-25", "--store", Store,
            "--state-dir", State, "--login", "totp").Code);
        Assert.Equal(0, Qa(TimeProvider.System, "universe", "add", "ERIC-B", "--config-dir", Config, "--store", Store).Code);
    }

    private string[] Args(string[] verb, string? mode = "confirm") =>
        [.. verb, .. mode is null ? Array.Empty<string>() : ["--mode", mode], "--strategy", "buy-and-hold", "--duration", "120",
         "--config-dir", Config, "--store", Store, "--state-dir", State, "--audit-dir", Audit, "--kill-file", KillFile,
         "--promotion-dir", PromotionDir, "--reports-dir", Path.Combine(_root, "reports"), "--login", "totp"];

    private static readonly string[] TradeRun = ["trade", "run"];
    private static readonly string[] RebalanceExecute = ["rebalance", "--execute"];

    /// <summary>
    /// Runs a live command on a worker while this thread moves the fake clock, keeps the depth stream alive, and types
    /// <paramref name="answer"/>(card number) two seconds after each card appears (null types nothing).
    /// </summary>
    private async Task<(int Code, string Output, string Error)> RunLive(string[] args, Func<int, string?> answer)
    {
        var conn = new SseConnection();
        _server.Serve(conn);
        await conn.Event("info", "connected", "e0", 1000);
        var output = new SharedWriter();
        var error = new StringWriter();
        Task<int> run = Task.Run(() => QaCli.Run(args, output, error, Services(_time)), Ct);

        // Login and setup need no clock; hold it until the session streams (or stops).
        await Task.WhenAny(conn.Connected.Task, run).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        int seen = 0;
        int typeAt = -1;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        for (int step = 0; !run.IsCompleted && DateTime.UtcNow < deadline; step++)
        {
            if (step % 8 == 0)
            {
                await conn.Depth("5240", 70.84m, 70.86m, $"e{step}");
            }

            int cards = Count(output.Text, Card);
            if (cards > seen)
            {
                seen = cards;
                typeAt = step + 4;
            }

            if (step == typeAt && answer(seen) is { } typed)
            {
                _input.Type(typed);
            }

            await Task.Delay(15, Ct);
            _time.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.True(run.IsCompleted, "the live command did not finish: " + output.Text);
        return (await run, output.Text, error.ToString());
    }

    private static int Count(string text, string what)
    {
        int n = 0;
        for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    private RecordedRequest[] OrderRouteRequests() =>
        [.. _server.Requests.Where(r => AvanzaOrderRoutes.All.Any(route => r.PathAndQuery.StartsWith(route.PathTemplate, StringComparison.Ordinal)))];

    private int Preflights(AvanzaRoute route) => _server.Requests.Count(r => r.PathAndQuery == route.Path());

    [Fact]
    public async Task ATypedJA_SendsExactlyOnePlaceRequest_AfterTheReCheck()
    {
        PrepareHistoryAndUniverse();

        (int code, string output, string error) = await RunLive(Args(TradeRun), _ => "ERIC-B JA");

        Assert.True(code == 0, output + error);
        Assert.Contains("Confirm startup checks: all passed.", output, StringComparison.Ordinal);
        Assert.Contains("decision: 1 order(s), one card each.", output, StringComparison.Ordinal);
        Assert.Equal(1, Count(output, Card));
        Assert.Contains("CONFIRM MODE · a real order on your Avanza account", output, StringComparison.Ordinal);
        Assert.Contains(" Confirmed. Re-checking before sending", output, StringComparison.Ordinal);
        Assert.Contains(" Re-checked: 21 of 21 pass", output, StringComparison.Ordinal);
        Assert.Contains(" Sent: order 700000002, Working.", output, StringComparison.Ordinal);
        Assert.Contains("nothing more to trade today.", output, StringComparison.Ordinal);

        RecordedRequest place = Assert.Single(OrderRouteRequests());
        Assert.Equal(("POST", AvanzaOrderRoutes.Place.Path()), (place.Method, place.PathAndQuery));
        using JsonDocument body = JsonDocument.Parse(place.Body!);
        Assert.Equal("9990001", body.RootElement.GetProperty("accountId").GetString());
        Assert.Equal("5240", body.RootElement.GetProperty("orderbookId").GetString());
        Assert.Equal("BUY", body.RootElement.GetProperty("side").GetString());
        Assert.Equal((2, 2), (Preflights(AvanzaPreflightRoutes.Validate), Preflights(AvanzaPreflightRoutes.PreliminaryFee))); // the card, then after JA

        Assert.DoesNotContain("9990001", output, StringComparison.Ordinal); // the account is masked everywhere
        Assert.True(AuditLog.Verify(Audit).Valid);
        Assert.Single(Directory.GetFiles(Audit).SelectMany(AuditLog.Read), e => e.GetProperty("kind").GetString() == "submit");
        Assert.False(File.Exists(Path.Combine(State, "session.lock")));
    }

    [Theory]
    [InlineData("VOLV-B JA", "Skipped: another ticker. Nothing was sent.")]
    [InlineData("JA", "Skipped: JA without the ticker. Nothing was sent.")]
    [InlineData(null, "Skipped: no answer within 30 s. Nothing was sent.")]
    public async Task AnyOtherAnswer_SendsNothing_AndTheCardIsNotShownAgain(string? typed, string expected)
    {
        PrepareHistoryAndUniverse();

        (int code, string output, string error) = await RunLive(Args(TradeRun), _ => typed);

        Assert.True(code == 0, output + error);
        Assert.Contains(expected, output, StringComparison.Ordinal);
        Assert.Equal(1, Count(output, Card)); // one card per instrument per day: a late JA can't confirm another
        Assert.Contains("nothing more to trade today.", output, StringComparison.Ordinal);
        Assert.Empty(OrderRouteRequests());
        Assert.Equal(1, Preflights(AvanzaPreflightRoutes.Validate));
    }

    [Fact]
    public async Task AvanzaRefusingAtTheReCheck_SendsNothing()
    {
        PrepareHistoryAndUniverse();
        _server.On(AvanzaPreflightRoutes.Validate,
            _ => FakeAvanza.Json(Fixtures.Bytes("preflight-validate.json")),
            _ => FakeAvanza.Json(Fixtures.Mutate("preflight-validate.json", n => n["priceRampingWarning"]!["valid"] = false)));

        (int code, string output, string error) = await RunLive(Args(TradeRun), _ => "ERIC-B JA");

        Assert.True(code == 0, output + error);
        Assert.Contains(" Re-check failed: R21 Avanza's own order validation failed. Skipped; nothing was sent.", output, StringComparison.Ordinal);
        Assert.Empty(OrderRouteRequests());
        Assert.Equal(2, Preflights(AvanzaPreflightRoutes.Validate));
    }

    [Fact]
    public async Task RebalanceExecute_ShowsTheCardsNow_AndAJASendsOne()
    {
        PrepareHistoryAndUniverse();

        (int code, string output, string error) = await RunLive(Args(RebalanceExecute), _ => "eric-b ja");

        Assert.True(code == 0, output + error);
        Assert.Contains("The cards start now", output, StringComparison.Ordinal);
        Assert.Single(OrderRouteRequests());
    }

    public static TheoryData<string, string> RefusedBeforeLogin() => new()
    {
        { "claude code", "[--] not started from Claude Code: CLAUDECODE is set" },
        { "provisional format", "[--] order channel: 'avanza' is not ready: the Avanza order format is provisional" },
        { "kill switch", "[--] kill switch: on since" },
        { "not promoted", "[--] promotion: Mode Confirm is above the promotion state (Paper" },
        { "calendar not verified", "[--] verified constants (R20): the 2026 trading calendar has no verified_on date" },
    };

    [Theory]
    [MemberData(nameof(RefusedBeforeLogin))]
    public void ACheckThatNeedsNoLogin_RefusesBeforeAnyRequest(string label, string expected)
    {
        File.WriteAllText(Path.Combine(Config, "universe.json"), """{ "format": "qa-universe/1", "instruments": [ { "orderbook_id": "5240", "ticker": "ERIC B", "name": "Ericsson B" } ] }""");
        switch (label)
        {
            case "claude code":
                _variables["CLAUDECODE"] = "1";
                break;
            case "provisional format":
                _finalFormat = false;
                break;
            case "kill switch":
                File.WriteAllText(KillFile, "2026-09-28T07:00:00Z owner test\n");
                break;
            case "not promoted":
                Directory.Delete(PromotionDir, recursive: true);
                break;
            case "calendar not verified":
                string calendar = Path.Combine(Config, "market-calendar.XSTO.2026.json");
                File.WriteAllText(calendar, File.ReadAllText(calendar).Replace("\"verified_on\": \"2026-09-01\"", "\"verified_on\": null", StringComparison.Ordinal));
                break;
        }

        (int code, string output, _) = Qa(_time, Args(TradeRun));

        Assert.Equal(AvanzaCommands.ExitHalt, code);
        Assert.Contains("Confirm can't start; nothing was logged in to or sent:", output, StringComparison.Ordinal);
        Assert.Contains(expected, output, StringComparison.Ordinal);
        Assert.Empty(_server.Requests); // not even a login
        Assert.False(File.Exists(Path.Combine(State, "session.lock")));
    }

    [Fact]
    public void AnAccountR1Refuses_RefusesAfterTheLogin_AndSendsNothing()
    {
        File.WriteAllText(Path.Combine(Config, "universe.json"), """{ "format": "qa-universe/1", "instruments": [ { "orderbook_id": "5240", "ticker": "ERIC B", "name": "Ericsson B" } ] }""");
        _variables.Remove("AVANZA__ALLOWEDACCOUNTIDS");

        (int code, string output, _) = Qa(_time, Args(TradeRun));

        Assert.Equal(AvanzaCommands.ExitHalt, code);
        Assert.Contains("Confirm can't start; nothing was sent:", output, StringComparison.Ordinal);
        Assert.Contains("[--] account (R1): AVANZA__ALLOWEDACCOUNTIDS is not set", output, StringComparison.Ordinal);
        Assert.NotEmpty(_server.Requests); // the login and the accounts read
        Assert.Empty(OrderRouteRequests());
        Assert.Equal(0, Preflights(AvanzaPreflightRoutes.Validate));
    }

    [Theory]
    [InlineData(null, "qa trade run needs --mode confirm")]
    [InlineData("paper", "Paper runs with: qa paper run")]
    [InlineData("auto", "Auto is not available before Phase 8")]
    [InlineData("confirmed", "Unknown mode 'confirmed'")]
    public void TheModeMustBeNamed_AndBeConfirm(string? mode, string expected)
    {
        (int code, _, string error) = Qa(_time, Args(TradeRun, mode));

        Assert.Equal(1, code);
        Assert.Contains(expected, error, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public void RebalanceExecute_NeedsTheModeToo()
    {
        (int code, _, string error) = Qa(_time, Args(RebalanceExecute, mode: null));

        Assert.Equal(1, code);
        Assert.Contains("qa rebalance --execute needs --mode confirm", error, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public void RebalanceWithoutExecute_ShowsThePlan_AndSendsNothing()
    {
        PrepareHistoryAndUniverse();
        int before = _server.Requests.Count;

        (int code, string output, string error) = Qa(_time, Args(["rebalance"], mode: null));

        Assert.True(code == 0, output + error);
        Assert.Contains("Rebalance plan for ***001 (buy-and-hold", output, StringComparison.Ordinal);
        Assert.Contains("1 order(s) would be proposed, one card each. Nothing was sent.", output, StringComparison.Ordinal);
        RecordedRequest[] session = [.. _server.Requests.Skip(before)];
        Assert.Empty(OrderRouteRequests());
        Assert.All(session.Where(r => r.Method != "GET"), r => Assert.Contains(r.PathAndQuery, new[] { AvanzaRoutes.UserCredentials.Path(), AvanzaRoutes.Totp.Path() }));
    }

    [Fact]
    public void Status_ListsTheConfirmChecks_ThatAreNotReady()
    {
        (int code, string output, _) = Qa(_time, "status", "--config-dir", Config, "--store", Store, "--state-dir", State, "--audit-dir", Audit,
            "--kill-file", KillFile, "--promotion-dir", PromotionDir);

        Assert.Equal(0, code);
        Assert.Contains("Confirm checks", output, StringComparison.Ordinal);
        Assert.Contains("1 not ready: order channel", output, StringComparison.Ordinal);
        Assert.Contains("Before Confirm, order channel: the Avanza order format is provisional", output, StringComparison.Ordinal);

        _variables["CLAUDECODE"] = "1";
        (_, output, _) = Qa(_time, "status", "--config-dir", Config, "--store", Store, "--state-dir", State, "--audit-dir", Audit,
            "--kill-file", KillFile, "--promotion-dir", PromotionDir);
        Assert.Contains("2 not ready: not started from Claude Code, order channel", output, StringComparison.Ordinal);
        Assert.Empty(_server.Requests); // status is offline
    }
}
