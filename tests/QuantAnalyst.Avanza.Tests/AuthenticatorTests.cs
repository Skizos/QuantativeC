using System.Net;
using System.Text.Json;
using QuantAnalyst.Avanza.Auth;
using QuantAnalyst.Avanza.Credentials;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Core.Broker;

namespace QuantAnalyst.Avanza.Tests;

public sealed class AuthenticatorTests
{
    [Fact]
    public async Task Login_WithHeaderToken_SendsExactlyOneRequestPerStep()
    {
        using var rig = new TestRig();
        LoginResult result = await rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken);

        Assert.Equal("header", result.TokenSource);
        Assert.Equal(2, rig.Server.Requests.Count);

        using JsonDocument step1 = JsonDocument.Parse(rig.Server.Requests[0].Body!);
        Assert.Equal(60, step1.RootElement.GetProperty("maxInactiveMinutes").GetInt32());
        Assert.Equal(FakeSecrets.Username, step1.RootElement.GetProperty("username").GetString());

        using JsonDocument step2 = JsonDocument.Parse(rig.Server.Requests[1].Body!);
        Assert.Equal("TOTP", step2.RootElement.GetProperty("method").GetString());
        string expected = Totp.Compute(Base32.Decode(FakeSecrets.TotpSecret), rig.Time.GetUtcNow());
        Assert.Equal(expected, step2.RootElement.GetProperty("totpCode").GetString());
        Assert.Contains(FakeSecrets.SessionCookie, rig.Server.Requests[1].Headers["Cookie"], StringComparison.Ordinal);

        Assert.False(rig.Connection.Authenticator.IsLocked);
    }

    [Fact]
    public async Task Login_FallsBackToCsrfCookie_AndReadsSendItAsToken()
    {
        var server = new FakeAvanza { SendTokenHeader = false };
        using var rig = new TestRig(server);
        LoginResult result = await rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken);
        Assert.Equal("cookie", result.TokenSource);

        Assert.True((await rig.Connection.Gateway.GetSessionHealthAsync(TestContext.Current.CancellationToken)).LoggedIn);
        Assert.Equal(FakeSecrets.CsrfCookie, server.Requests[^1].Headers["X-SecurityToken"]);
    }

    [Fact]
    public async Task Login_WithoutAnyToken_IsSchemaDrift()
    {
        using var rig = new TestRig(new FakeAvanza { SendTokenHeader = false, SendCsrfCookie = false });
        var ex = await Assert.ThrowsAsync<SchemaDriftException>(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
        Assert.Contains("X-SecurityToken|AZACSRF", ex.Paths);
    }

    [Fact]
    public async Task Login_Rejected_IsNotRetried_AndSecondFailureLocks_AndLockBlocksAllHttp()
    {
        var server = new FakeAvanza();
        server.On(AvanzaRoutes.UserCredentials, _ => FakeAvanza.Status(HttpStatusCode.Unauthorized), _ => FakeAvanza.Status(HttpStatusCode.Unauthorized));
        using var rig = new TestRig(server);

        // Trigger 1: one request, failure recorded, not locked yet.
        await Assert.ThrowsAsync<LoginFailedException>(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
        Assert.Single(server.Requests);
        Assert.False(rig.Connection.Authenticator.IsLocked);

        // Trigger 2 (new process = new connection), 1 h later: second failure within 24 h => locked and persisted.
        rig.Time.Advance(TimeSpan.FromHours(1));
        rig.NewConnection();
        await Assert.ThrowsAsync<LoginLockedException>(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains("\"locked\": true", File.ReadAllText(rig.Connection.Authenticator.StateFile), StringComparison.Ordinal);

        // Trigger 3: refused before any HTTP call or secret read.
        rig.NewConnection();
        int secretReads = ((InMemorySecretStore)rig.Secrets).Reads;
        await Assert.ThrowsAsync<LoginLockedException>(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(secretReads, ((InMemorySecretStore)rig.Secrets).Reads);

        // A human clears the lock; the next trigger logs in normally.
        rig.Connection.Authenticator.ClearLock();
        rig.NewConnection();
        await rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Login_FailuresMoreThan24hApart_DoNotLock()
    {
        var server = new FakeAvanza();
        server.On(AvanzaRoutes.UserCredentials, _ => FakeAvanza.Status(HttpStatusCode.Unauthorized), _ => FakeAvanza.Status(HttpStatusCode.Unauthorized));
        using var rig = new TestRig(server);
        await Assert.ThrowsAsync<LoginFailedException>(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
        rig.Time.Advance(TimeSpan.FromHours(25));
        rig.NewConnection();
        await Assert.ThrowsAsync<LoginFailedException>(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
        Assert.False(rig.Connection.Authenticator.IsLocked);
    }

    [Fact]
    public async Task Login_RejectedTotp_IsNotRetriedWithTheNextCode()
    {
        var server = new FakeAvanza();
        server.On(AvanzaRoutes.Totp, _ => FakeAvanza.Status(HttpStatusCode.Unauthorized));
        using var rig = new TestRig(server);
        var ex = await Assert.ThrowsAsync<LoginFailedException>(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
        Assert.Contains("totp", ex.Reason, StringComparison.Ordinal);
        Assert.Equal(2, server.Requests.Count); // usercredentials + one totp, nothing more
    }

    [Theory]
    [InlineData(HttpStatusCode.Locked)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Login_LockoutSignal_LocksImmediately(HttpStatusCode status)
    {
        var server = new FakeAvanza();
        server.On(AvanzaRoutes.UserCredentials, _ => FakeAvanza.Status(status));
        using var rig = new TestRig(server);
        await Assert.ThrowsAsync<LoginLockedException>(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
        Assert.True(rig.Connection.Authenticator.IsLocked);
    }

    [Fact]
    public async Task Login_ServerError_IsNotRetriedAndNotCounted()
    {
        var server = new FakeAvanza();
        server.On(AvanzaRoutes.UserCredentials, _ => FakeAvanza.Status(HttpStatusCode.ServiceUnavailable));
        using var rig = new TestRig(server);
        await Assert.ThrowsAsync<BrokerUnavailableException>(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
        Assert.Single(server.Requests);
        Assert.False(File.Exists(rig.Connection.Authenticator.StateFile)); // nothing recorded towards the lock
    }

    [Fact]
    public async Task Login_UnsupportedTwoFactorMethod_StopsWithoutCounting()
    {
        using var rig = new TestRig(new FakeAvanza { TwoFactorMethod = "BANKID" });
        var ex = await Assert.ThrowsAsync<LoginFailedException>(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
        Assert.Contains("unsupported two-factor method", ex.Message, StringComparison.Ordinal);
        Assert.Single(rig.Server.Requests);
        Assert.False(File.Exists(rig.Connection.Authenticator.StateFile));
    }

    [Fact]
    public async Task Login_WaitsForAFreshTotpWindowWhenTheCodeIsAboutToExpire()
    {
        using var rig = new TestRig();
        rig.Time.Advance(TimeSpan.FromSeconds(18.5)); // 28.5 s into the window: 1.5 s left
        DateTimeOffset before = rig.Time.GetUtcNow();

        await rig.Run(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));

        using JsonDocument step2 = JsonDocument.Parse(rig.Server.Requests[1].Body!);
        byte[] key = Base32.Decode(FakeSecrets.TotpSecret);
        Assert.Equal(Totp.Compute(key, before.AddSeconds(2)), step2.RootElement.GetProperty("totpCode").GetString());
        Assert.NotEqual(Totp.Compute(key, before), step2.RootElement.GetProperty("totpCode").GetString());
        Assert.Equal(2, rig.Server.Requests.Count);
    }

    [Fact]
    public async Task Login_TwiceOnOneConnection_IsRefused()
    {
        using var rig = new TestRig();
        await rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, rig.Server.Requests.Count);
    }

    [Fact]
    public async Task Login_InvalidTotpSecretOrMissingSecrets_FailBeforeAnyHttpCall()
    {
        using (var rig = new TestRig(secrets: FakeSecrets.Store(totpSecret: "not base32!")))
        {
            await Assert.ThrowsAsync<LoginFailedException>(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
            Assert.Empty(rig.Server.Requests);
        }

        using (var rig = new TestRig(secrets: new InMemorySecretStore(null)))
        {
            await Assert.ThrowsAsync<SecretStoreException>(() => rig.Connection.Authenticator.LoginAsync(TestContext.Current.CancellationToken));
            Assert.Empty(rig.Server.Requests);
        }
    }

    [Fact]
    public void EnvironmentStore_NamesMissingVariablesButNeverValues()
    {
        var env = new Dictionary<string, string?> { [EnvironmentSecretStore.UsernameVariable] = FakeSecrets.Username };
        var store = new EnvironmentSecretStore(k => env.GetValueOrDefault(k));
        var ex = Assert.Throws<SecretStoreException>(store.GetAvanzaCredentials);
        Assert.Contains(EnvironmentSecretStore.PasswordVariable, ex.Message, StringComparison.Ordinal);
        Assert.Contains(EnvironmentSecretStore.TotpSecretVariable, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecrets.Username, ex.Message, StringComparison.Ordinal);

        env[EnvironmentSecretStore.PasswordVariable] = FakeSecrets.Password;
        env[EnvironmentSecretStore.TotpSecretVariable] = FakeSecrets.TotpSecret;
        AvanzaCredentials c = store.GetAvanzaCredentials();
        Assert.Equal(FakeSecrets.Password, c.Password.Reveal());
        Assert.DoesNotContain(FakeSecrets.Password, c.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsCredentialStore_RoundTripsOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Credential Manager exists only on Windows.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string prefix = "QuantAnalyst:Test:" + Guid.NewGuid().ToString("N");
        var store = new WindowsCredentialStore(prefix);
        try
        {
            Assert.Throws<SecretStoreException>(store.GetAvanzaCredentials);
            WindowsCredentialStore.Write(store.LoginTarget, FakeSecrets.Username, new Core.Secret(FakeSecrets.Password));
            WindowsCredentialStore.Write(store.TotpTarget, "totp", new Core.Secret(FakeSecrets.TotpSecret));
            AvanzaCredentials c = store.GetAvanzaCredentials();
            Assert.Equal(FakeSecrets.Username, c.Username.Reveal());
            Assert.Equal(FakeSecrets.Password, c.Password.Reveal());
            Assert.Equal(FakeSecrets.TotpSecret, c.TotpSecret.Reveal());
            Assert.True(WindowsCredentialStore.Exists(store.TotpTarget));
        }
        finally
        {
            WindowsCredentialStore.Delete(store.LoginTarget);
            WindowsCredentialStore.Delete(store.TotpTarget);
        }

        Assert.False(WindowsCredentialStore.Exists(store.LoginTarget));
    }
}
