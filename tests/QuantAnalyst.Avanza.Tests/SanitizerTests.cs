using System.Net;
using System.Text.Json;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Avanza.Recording;

namespace QuantAnalyst.Avanza.Tests;

public sealed class SanitizerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TestRig Rig, string Live)> RecordProbe(FakeAvanza? server = null)
    {
        var rig = new TestRig(server, record: true);
        await rig.Connection.Probe.RunAsync("ERIC-B", Ct);
        return (rig, rig.Connection.RecordingDirectory!);
    }

    [Fact]
    public async Task Sanitize_ReplacesIdsNamesAndAmounts_KeepsPublicMarketData_AndIsDeterministic()
    {
        (TestRig rig, string live) = await RecordProbe();
        using (rig)
        {
            string out1 = Path.Combine(rig.Root, "fixtures-1");
            string out2 = Path.Combine(rig.Root, "fixtures-2");
            SanitizeReport report = RecordingSanitizer.Sanitize(live, out1, new SanitizeOptions { ForbiddenValues = FakeSecrets.All });
            Assert.True(report.Succeeded, string.Join("; ", report.Problems));
            Assert.True(RecordingSanitizer.Sanitize(live, out2).Succeeded);

            string[] files = [.. Directory.GetFiles(out1).Order(StringComparer.Ordinal)];
            Assert.Equal(Directory.GetFiles(live).Length, files.Length);
            string all = string.Join('\n', files.Select(File.ReadAllText));

            foreach (string raw in new[] { "9990001", "9990002", "Algo ISK", "url-1", "url-2", "10629.0", "12345.67" })
            {
                Assert.DoesNotContain(raw, all, StringComparison.Ordinal);
            }

            Assert.Contains("900001001", all, StringComparison.Ordinal); // fake id keeps the last 3 digits
            Assert.Contains("Account ", all, StringComparison.Ordinal);
            Assert.Contains("SE0000108656", all, StringComparison.Ordinal); // instruments are not personal

            string market = File.ReadAllText(files.Single(f => f.EndsWith("-marketdata.json", StringComparison.Ordinal)));
            Assert.Contains("70.84", market, StringComparison.Ordinal);

            // Same input => byte-identical output.
            foreach (string f in files)
            {
                Assert.Equal(File.ReadAllText(f), File.ReadAllText(Path.Combine(out2, Path.GetFileName(f))));
            }

            // Scrambled positions still parse strictly (same shape).
            using JsonDocument positions = JsonDocument.Parse(File.ReadAllText(files.Single(f => f.EndsWith("-positions.json", StringComparison.Ordinal))));
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(positions.RootElement.GetProperty("response").GetProperty("body"));
            var dto = new Json.AvanzaJson(new CapturingLogger()).Deserialize(body, Dto.AvanzaTierAContext.Default.PositionsDto, "positions", "t", Core.Broker.DtoTier.A);
            Assert.Equal(dto.WithOrderbook[0].Volume.Value, decimal.Truncate(dto.WithOrderbook[0].Volume.Value));
        }
    }

    [Fact]
    public async Task Sanitize_KeepAmounts_LeavesNumbersButStillMapsIds()
    {
        (TestRig rig, string live) = await RecordProbe();
        using (rig)
        {
            string output = Path.Combine(rig.Root, "fixtures");
            Assert.True(RecordingSanitizer.Sanitize(live, output, new SanitizeOptions { KeepAmounts = true }).Succeeded);
            string all = string.Join('\n', Directory.GetFiles(output).Select(File.ReadAllText));
            Assert.Contains("12345.67", all, StringComparison.Ordinal);
            Assert.DoesNotContain("9990001", all, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Sanitize_FailsClosedOnLeaks_AndWritesNothing()
    {
        var server = new FakeAvanza();
        server.On(AvanzaRoutes.Positions, _ => FakeAvanza.Json(Fixtures.Mutate("positions.json", n =>
        {
            n["withOrderbook"]![0]!["id"] = FakeSecrets.Password; // pretend a secret ended up in a body
            n["cashPositions"]![0]!["id"] = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.c2lnbmF0dXJl";
            n["cashPositions"]![1]!["id"] = "Qm9vbXRva2VuVGhhdExvb2tzUmFuZG9tMTIzNDU2Nzg5MEFCQ0RFRg==";
        })));
        (TestRig rig, string live) = await RecordProbe(server);
        using (rig)
        {
            string output = Path.Combine(rig.Root, "fixtures");
            SanitizeReport report = RecordingSanitizer.Sanitize(live, output, new SanitizeOptions { ForbiddenValues = FakeSecrets.All });
            Assert.False(report.Succeeded);
            Assert.Contains(report.Problems, p => p.Contains("value from the secret store", StringComparison.Ordinal));
            Assert.Contains(report.Problems, p => p.Contains("JWT-like", StringComparison.Ordinal));
            Assert.Contains(report.Problems, p => p.Contains("long token-like string", StringComparison.Ordinal));
            Assert.All(report.Problems, p => Assert.DoesNotContain(FakeSecrets.Password, p, StringComparison.Ordinal));
            Assert.False(Directory.Exists(output));
        }
    }

    [Fact]
    public void Sanitize_RefusesNonEmptyOutputAndForeignFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "qa-sanitize-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "in"));
            Directory.CreateDirectory(Path.Combine(root, "out"));
            File.WriteAllText(Path.Combine(root, "out", "x.json"), "{}");
            File.WriteAllText(Path.Combine(root, "in", "a.json"), """{"format":"something-else"}""");
            Assert.Throws<IOException>(() => RecordingSanitizer.Sanitize(Path.Combine(root, "in"), Path.Combine(root, "out")));
            Assert.Throws<InvalidDataException>(() => RecordingSanitizer.Sanitize(Path.Combine(root, "in"), Path.Combine(root, "out2")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("12345.67")]
    [InlineData("-9768.51")]
    [InlineData("3456789.0")]
    [InlineData("150")]
    [InlineData("0.84")]
    [InlineData("1.5e3")]
    public void ScrambleNumber_KeepsShape(string number)
    {
        string s = RecordingSanitizer.ScrambleNumber(number, "salt");
        Assert.Equal(number.Length, s.Length);
        Assert.Equal(number.StartsWith('-'), s.StartsWith('-'));
        Assert.Equal(number.IndexOf('.', StringComparison.Ordinal), s.IndexOf('.', StringComparison.Ordinal));
        Assert.True(double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));
        if (number.EndsWith(".0", StringComparison.Ordinal))
        {
            Assert.EndsWith(".0", s, StringComparison.Ordinal); // integral values stay integral
        }

        Assert.Equal(s, RecordingSanitizer.ScrambleNumber(number, "salt"));
    }

    [Fact]
    public async Task Recording_OfFailedCallIsStillWritten()
    {
        var server = new FakeAvanza();
        server.On(AvanzaRoutes.Orders, _ => FakeAvanza.Status(HttpStatusCode.BadRequest, """{"error":"bad"}"""));
        (TestRig rig, string live) = await RecordProbe(server);
        using (rig)
        {
            string orders = File.ReadAllText(Directory.GetFiles(live).Single(f => f.EndsWith("-orders.json", StringComparison.Ordinal)));
            Assert.Contains("\"status\": 400", orders, StringComparison.Ordinal);
        }
    }
}
