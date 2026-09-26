using System.Net;
using QuantAnalyst.Avanza.Auth;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Logging;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza.Tests;

public sealed class BankIdLoginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TestRig Rig(FakeAvanza? server = null, AvanzaOptions? options = null, bool record = false, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        new(server, options, record: record, logger: logger, login: AvanzaLoginMethod.BankId);

    private static int Count(FakeAvanza server, string path, string method = "POST") =>
        server.Requests.Count(r => r.Method == method && r.PathAndQuery == path);

    [Fact]
    public async Task BankId_ShowsARefreshingQrCode_ThenCompletesWithTheCsrfCookieAsToken()
    {
        using TestRig rig = Rig();
        LoginResult result = await rig.Run(() => rig.Connection.Authenticator.LoginAsync(Ct));

        Assert.Equal(AvanzaLoginMethod.BankId, result.Method);
        Assert.Equal("cookie", result.TokenSource);
        Assert.Equal(["bankid.qr.0", "bankid.qr.1", "bankid.qr.2"], rig.Prompt.QrCodes);
        Assert.Contains("Open the BankID app and scan the QR code.", rig.Prompt.Statuses);
        Assert.Contains("Confirm the login in the BankID app.", rig.Prompt.Statuses);
        Assert.Equal(1, rig.Prompt.Completions);

        // Exactly one transaction; start page + its same-origin redirect, login path and trading page were visited.
        Assert.Equal(1, Count(rig.Server, AvanzaRoutes.BankIdStart.Path()));
        Assert.Equal(3, Count(rig.Server, AvanzaRoutes.BankIdCollect.Path()));
        Assert.Equal(1, Count(rig.Server, rig.Server.StartPageRedirect, "GET"));
        Assert.Equal(1, Count(rig.Server, rig.Server.BankIdLoginPath, "GET"));
        Assert.Equal(1, Count(rig.Server, AvanzaRoutes.TradingPage.Path(), "GET"));
        string cookies = rig.Server.Requests.Last(r => r.PathAndQuery == AvanzaRoutes.TradingPage.Path()).Headers["Cookie"];
        Assert.Contains(FakeSecrets.SessionCookie, cookies, StringComparison.Ordinal); // set on the 302 hop
        Assert.Contains("hop-cookie", cookies, StringComparison.Ordinal); // set by the login path

        // Reads now carry the token.
        Assert.True((await rig.Connection.Gateway.GetSessionHealthAsync(Ct)).LoggedIn);
        Assert.Equal(FakeSecrets.CsrfCookie, rig.Server.Requests[^1].Headers["X-SecurityToken"]);
    }

    [Fact]
    public async Task BankId_Cancelled_StopsWithoutASecondTransaction_AndDoesNotTouchTheTotpLock()
    {
        var server = new FakeAvanza { BankIdFinalState = "FAILED", BankIdFailureHint = "userCancel" };
        using TestRig rig = Rig(server);
        var ex = await Assert.ThrowsAsync<LoginFailedException>(() => rig.Run(() => rig.Connection.Authenticator.LoginAsync(Ct)));

        Assert.Contains("cancelled", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, Count(server, AvanzaRoutes.BankIdStart.Path()));
        Assert.Equal(0, Count(server, server.BankIdLoginPath, "GET"));
        Assert.False(File.Exists(rig.Connection.Authenticator.StateFile));
    }

    [Fact]
    public async Task BankId_NotApprovedInTime_Expires()
    {
        var server = new FakeAvanza { BankIdPendingPolls = int.MaxValue };
        using TestRig rig = Rig(server, new AvanzaOptions { BankIdTimeout = TimeSpan.FromSeconds(10) });
        var ex = await Assert.ThrowsAsync<LoginFailedException>(() => rig.Run(() => rig.Connection.Authenticator.LoginAsync(Ct)));

        Assert.Contains("not approved within 10 s", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, Count(server, AvanzaRoutes.BankIdStart.Path()));
        Assert.InRange(Count(server, AvanzaRoutes.BankIdCollect.Path()), 9, 11);
    }

    [Theory]
    [InlineData("https://evil.example/_api/authentication/v2/sessions/bankid/x")]
    [InlineData("/_api/authentication/v2/sessions/bankid/../../../transfer")]
    [InlineData("/_api/trading/rest/orders")]
    [InlineData("/_api/authentication/v2/sessions/bankid/collect")]
    [InlineData("")]
    public async Task BankId_ServerSuppliedLoginPathIsValidated_NeverFollowedBlindly(string loginPath)
    {
        var server = new FakeAvanza { BankIdLoginPath = loginPath };
        using TestRig rig = Rig(server);
        var ex = await Assert.ThrowsAsync<SchemaDriftException>(() => rig.Run(() => rig.Connection.Authenticator.LoginAsync(Ct)));
        Assert.Equal(["$.logins[0].loginPath"], ex.Paths);
        Assert.DoesNotContain(server.Requests, r => r.Method == "GET" && r.PathAndQuery.StartsWith("/_api/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BankId_CrossOriginRedirectIsNotFollowed()
    {
        var server = new FakeAvanza { StartPageRedirect = "https://evil.example/steal" };
        using TestRig rig = Rig(server);
        await Assert.ThrowsAsync<LoginFailedException>(() => rig.Run(() => rig.Connection.Authenticator.LoginAsync(Ct)));
        Assert.DoesNotContain(server.Requests, r => r.PathAndQuery.Contains("steal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BankId_UnknownState_IsDrift()
    {
        var server = new FakeAvanza { BankIdPendingPolls = 0, BankIdFinalState = "SOMETHING_NEW" };
        using TestRig rig = Rig(server);
        var ex = await Assert.ThrowsAsync<SchemaDriftException>(() => rig.Run(() => rig.Connection.Authenticator.LoginAsync(Ct)));
        Assert.Equal(["$.state"], ex.Paths);
    }

    [Fact]
    public async Task BankId_WorksWhileTotpIsLocked_AndLeavesTheLockInPlace()
    {
        using TestRig rig = Rig();
        new AuthStateStore(rig.Options.StateDirectory, rig.Time).RecordFailure("usercredentials: HTTP 423", lockNow: true);
        Assert.True(rig.Connection.Authenticator.IsLocked);

        await rig.Run(() => rig.Connection.Authenticator.LoginAsync(Ct));
        Assert.True(rig.Connection.Authenticator.IsLocked);
    }

    [Fact]
    public async Task BankId_PersonalDataAndTokensNeverReachLogsOrRecordings()
    {
        var sink = new StringWriter();
        var redactor = new Redactor();
        var logger = new RedactingLogger(sink, redactor, Microsoft.Extensions.Logging.LogLevel.Trace);
        using TestRig rig = Rig(record: true, logger: logger);
        using var connection = AvanzaConnection.CreateForTest(rig.Options, rig.Secrets, logger, redactor, rig.Time, rig.Server, rig.Prompt);

        IReadOnlyList<ProbeResult> results = await rig.Run(() => connection.Probe.RunAsync("ERIC-B", Ct));
        Assert.Equal("BankID; security token from cookie", results[0].Detail);
        Assert.All(results, r => Assert.NotEqual(ProbeStatus.Stopped, r.Status));

        string logs = sink.ToString();
        string recordings = string.Join('\n', Directory.GetFiles(connection.RecordingDirectory!).Select(File.ReadAllText));
        Assert.Contains(AvanzaRoutes.BankIdLogin.PathTemplate, recordings, StringComparison.Ordinal); // template, not the real path
        foreach (string secret in FakeSecrets.All.Append("9990001"))
        {
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
            if (secret != "9990001")
            {
                Assert.DoesNotContain(secret, recordings, StringComparison.Ordinal);
            }
        }

        Assert.All(rig.Prompt.Statuses, s => Assert.DoesNotContain("PII", s, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BankId_WithoutAPrompt_IsRefusedBeforeAnyHttpCall()
    {
        using TestRig rig = Rig();
        using var connection = AvanzaConnection.CreateForTest(rig.Options, rig.Secrets, new CapturingLogger(), new Redactor(), rig.Time, rig.Server);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Authenticator.LoginAsync(Ct));
        Assert.Empty(rig.Server.Requests);
    }

    [Theory]
    [InlineData("userSign", "Confirm the login in the BankID app.")]
    [InlineData("USERSIGN", "Confirm the login in the BankID app.")] // live spelling, 2026-09-25
    [InlineData("OUTSTANDING_TRANSACTION", "Open the BankID app and scan the QR code.")]
    [InlineData("outstandingTransaction", "Open the BankID app and scan the QR code.")]
    [InlineData(null, "Open the BankID app and scan the QR code.")]
    [InlineData("USER_CANCEL", "The login was cancelled in the BankID app.")]
    [InlineData("SOMETHING_NEW 42 <x>", "BankID status: SOMETHINGNEWx")]
    public void HintCodes_AreMatchedWithoutCaseOrUnderscores(string? hint, string expected) =>
        Assert.Equal(expected, AvanzaAuthenticator.DescribeHint(hint));

    [Theory]
    [InlineData("/_api/authentication/v2/sessions/bankid/tx-1/cust-1", true)]
    [InlineData("/_api/authentication/v2/sessions/bankid/abc", true)]
    [InlineData("/_api/authentication/v2/sessions/bankid/", false)]
    [InlineData("/_api/authentication/v2/sessions/bankid/a/b/c/d", false)]
    [InlineData("/_api/authentication/v2/sessions/bankid/restart", false)]
    [InlineData("/_api/authentication/v2/sessions/bankid/a?x=1", false)]
    [InlineData("//evil.example/_api/authentication/v2/sessions/bankid/a", false)]
    [InlineData(null, false)]
    public void LoginPathValidation(string? path, bool valid) =>
        Assert.Equal(valid, AvanzaRoutes.IsValidBankIdLoginPath(path));
}
