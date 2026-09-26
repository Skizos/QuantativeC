using System.Net;
using System.Text.Json;
using QuantAnalyst.Avanza.Credentials;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>The read-only <c>qa</c> verbs against <see cref="FakeAvanza"/> (no network).</summary>
public sealed class CliAvanzaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qa-cli-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAvanza _server = new();

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
                new AvanzaOptions
                {
                    StateDirectory = options.StateDirectory,
                    RecordingDirectory = options.RecordingDirectory,
                    LoginMethod = options.LoginMethod,
                    BankIdPollInterval = TimeSpan.FromMilliseconds(1),
                    RequestsPerSecond = 10,
                    Burst = 20,
                },
                secrets, logger, redactor, TimeProvider.System, _server, prompt),
            _ => FakeSecrets.Store());
        var output = new StringWriter();
        var error = new StringWriter();
        string[] full = args[0] == "recordings" || args[0] == "--help"
            ? args
            : [.. args, "--state-dir", Path.Combine(_root, "state"), .. args.Contains("--login") ? Array.Empty<string>() : ["--login", "totp"]];
        int code = QaCli.Run(full, output, error, services);
        string all = output + "\n" + error;
        foreach (string secret in FakeSecrets.All.Append("9990001").Append("9990002"))
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }

        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Login_PrintsTokenSourceOnly()
    {
        (int code, string output, string error) = Qa("login");
        Assert.True(code == 0, error);
        Assert.Contains("Logged in with TOTP; security token from the header. Session health: loggedIn=True.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Login_WithBankId_DrawsTheQrCodeAndNeverPrintsPersonalData()
    {
        (int code, string output, string error) = Qa("login", "--login", "bankid");
        Assert.True(code == 0, output + error);
        Assert.Contains("Logged in with BankID; security token from the cookie. Session health: loggedIn=True.", output, StringComparison.Ordinal);
        Assert.Contains("open the BankID app, choose 'Scan QR code'", error, StringComparison.Ordinal);
        Assert.Contains("█", error, StringComparison.Ordinal);
        Assert.Contains("BankID approved", error, StringComparison.Ordinal);
        Assert.DoesNotContain("bankid.qr.", error, StringComparison.Ordinal); // the payload itself is only drawn, never printed
        Assert.Equal(0, _server.Requests.Count(r => r.PathAndQuery == Http.AvanzaRoutes.UserCredentials.Path()));
    }

    [Fact]
    public void BankIdQrRendering_ReproducesTheQrModulesExactly()
    {
        const string payload = "bankid.67df3917-fa0d-44e5-b327-edcc928297f8.0.dc69358e712458a66a7525beef148ae8526b1c71610eff2c16cdffb4cdac9bf8";
        using var generator = new QRCoder.QRCodeGenerator();
        using QRCoder.QRCodeData data = generator.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.L);
        IReadOnlyList<string> lines = QuantAnalyst.Cli.Output.ConsoleBankIdPrompt.Render(payload);

        int n = data.ModuleMatrix.Count;
        Assert.Equal((n + 1) / 2, lines.Count);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                char c = lines[y / 2][x];
                bool dark = y % 2 == 0 ? c is '█' or '▀' : c is '█' or '▄';
                Assert.Equal(data.ModuleMatrix[y][x], dark);
            }
        }
    }

    [Fact]
    public void Accounts_TableAndJson_MaskAccountIds()
    {
        (int code, string output, string error) = Qa("accounts");
        Assert.True(code == 0, error);
        Assert.Contains("***001", output, StringComparison.Ordinal);
        Assert.Contains("Algo ISK", output, StringComparison.Ordinal);
        Assert.Contains("12,345.67 SEK", output, StringComparison.Ordinal);

        (code, output, _) = Qa("accounts", "--json");
        Assert.Equal(0, code);
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.Equal("***002", doc.RootElement[1].GetProperty("account").GetString());
        Assert.Equal(12345.67m, doc.RootElement[0].GetProperty("availableForPurchase").GetDecimal());
    }

    [Fact]
    public void Positions_FilterByAccountSuffix()
    {
        (int code, string output, string error) = Qa("positions", "--account", "001");
        Assert.True(code == 0, error);
        Assert.Contains("Ericsson B", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Global Index Fund", output, StringComparison.Ordinal);

        (code, _, error) = Qa("positions", "--account", "777");
        Assert.Equal(1, code);
        Assert.Contains("No account ends with '777'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Orders_And_Quote()
    {
        (int code, string output, string error) = Qa("orders");
        Assert.True(code == 0, error);
        Assert.Contains("700000001", output, StringComparison.Ordinal);

        (code, output, error) = Qa("quote", "ERIC-B");
        Assert.True(code == 0, error);
        Assert.Contains("ERIC B  Ericsson B  (orderbook 5240, SE0000108656, XSTO, SEK)", output, StringComparison.Ordinal); // no status field live
        Assert.Contains("bid 70.84  ask 70.86", output, StringComparison.Ordinal);
        Assert.Contains("tick size at last 0.02", output, StringComparison.Ordinal);

        (code, _, error) = Qa("quote");
        Assert.Equal(1, code);
        Assert.Contains("Give either a ticker or --id", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_RecordsAndPointsToSanitize_ThenSanitizeProducesFixtures()
    {
        string live = Path.Combine(_root, "live");
        (int code, string output, string error) = Qa("probe", "--record-dir", live);
        Assert.True(code == 0, output + error);
        Assert.Contains("0 deal(s)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("DRIFT", output, StringComparison.Ordinal);
        Assert.Contains("Next: qa recordings sanitize", output, StringComparison.Ordinal);

        string recorded = Directory.GetDirectories(live).Single();
        string fixtures = Path.Combine(_root, "fixtures");
        (code, output, error) = Qa("recordings", "sanitize", "--in", recorded, "--out", fixtures);
        Assert.True(code == 0, output + error);
        Assert.Contains("Sanitized", output, StringComparison.Ordinal);
        Assert.NotEmpty(Directory.GetFiles(fixtures));
    }

    [Fact]
    public void ProbePreflight_RecordsBothChecks_AndSanitizingMasksTheAccountInTheirBodies()
    {
        Assert.Contains("--account is only used with --preflight", Qa("probe", "--account", "001", "--no-record").Error, StringComparison.Ordinal);

        string live = Path.Combine(_root, "live");
        (int code, string output, string error) = Qa("probe", "--preflight", "--record-dir", live);
        Assert.True(code == 0, output + error);
        Assert.Contains("validate: all 6 valid", output, StringComparison.Ordinal);
        Assert.Contains("nothing placed", output, StringComparison.Ordinal);

        string recorded = Directory.GetDirectories(live).Single();
        Assert.Single(Directory.GetFiles(recorded, "*-preflight.validate.json"));
        Assert.Single(Directory.GetFiles(recorded, "*-preflight.fee.json"));

        string fixtures = Path.Combine(_root, "fixtures");
        (code, output, error) = Qa("recordings", "sanitize", "--in", recorded, "--out", fixtures);
        Assert.True(code == 0, output + error);
        string sanitized = string.Join('\n', Directory.GetFiles(fixtures, "*-preflight.*.json").Select(File.ReadAllText));
        Assert.Contains("preflight.validate", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("9990001", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_WithTierADrift_ExitsWithHaltCode()
    {
        _server.On(AvanzaRoutes.Orders, _ => FakeAvanza.Json(Fixtures.Mutate("orders.json", n => n["orders"]![0]!["newThing"] = 1)));
        (int code, string output, _) = Qa("probe", "--no-record");
        Assert.Equal(AvanzaCommands.ExitHalt, code);
        Assert.Contains("DRIFT", output, StringComparison.Ordinal);
        Assert.Contains("$.orders[0].newThing", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionExpiry_IsAHalt_AndLockIsExit4_AndClearLockWorks()
    {
        _server.On(AvanzaRoutes.Positions, _ => FakeAvanza.Status(HttpStatusCode.Unauthorized));
        (int code, _, string error) = Qa("positions");
        Assert.Equal(AvanzaCommands.ExitHalt, code);
        Assert.StartsWith("HALT: Session expired", error, StringComparison.Ordinal);

        _server.On(AvanzaRoutes.UserCredentials, _ => FakeAvanza.Status(HttpStatusCode.Locked));
        (code, _, error) = Qa("accounts");
        Assert.Equal(AvanzaCommands.ExitLocked, code);
        Assert.Contains("LOCKED", error, StringComparison.Ordinal);

        int before = _server.Requests.Count;
        (code, _, _) = Qa("accounts");
        Assert.Equal(AvanzaCommands.ExitLocked, code);
        Assert.Equal(before, _server.Requests.Count); // no HTTP while locked

        (code, string output, _) = Qa("login", "--clear-lock");
        Assert.Equal(0, code);
        Assert.Contains("No login was attempted", output, StringComparison.Ordinal);
        Assert.Equal(before, _server.Requests.Count);
        Assert.Equal(0, Qa("login").Code);
    }

    [Fact]
    public void MissingCredentials_FailBeforeAnyHttpCall()
    {
        var services = new AvanzaCliServices(
            (options, secrets, prompt, logger, redactor) => AvanzaConnection.CreateForTest(options, secrets, logger, redactor, TimeProvider.System, _server, prompt),
            _ => new EnvironmentSecretStore(_ => null));
        var output = new StringWriter();
        var error = new StringWriter();
        int code = QaCli.Run(["accounts", "--login", "totp", "--state-dir", Path.Combine(_root, "state")], output, error, services);
        Assert.Equal(1, code);
        Assert.Contains("QA_AVANZA_USERNAME", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public void CommandTree_HasNoOrderOrTransferVerbs()
    {
        static IEnumerable<string> Names(System.CommandLine.Command c) =>
            c.Subcommands.SelectMany(s => Names(s).Prepend(s.Name));

        string[] verbs = [.. Names(QaCli.Build(AvanzaCliServices.Default))];
        Assert.Contains("probe", verbs);
        Assert.Contains("sanitize", verbs);
        string[] forbidden = ["buy", "sell", "place", "order", "cancel", "modify", "transfer", "withdraw", "deposit", "payment", "rebalance"];
        Assert.DoesNotContain(verbs, v => forbidden.Contains(v, StringComparer.OrdinalIgnoreCase));
    }
}
