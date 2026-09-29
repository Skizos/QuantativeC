using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Cli;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Intraday;
using QuantAnalyst.Data.Live;
using QuantAnalyst.Data.Store;

namespace QuantAnalyst.Analytics.Tests;

/// <summary>
/// Plan 17 step A5: <c>qa intraday backtest</c> end to end, on a temporary store with eight collected days of 5-minute
/// bars for two research-list shares and a fixture intraday holdout of the last three days.
/// </summary>
public sealed class IntradayBacktestCliTests : IDisposable
{
    private const string DailyHoldout =
        "{\"format\":\"qa-holdout/1\",\"locked\":true,\"start\":\"2025-10-01\",\"unlocked_by\":null,\"unlocked_on\":null,\"reason\":null}";

    private static readonly OrderbookId Eric = new("5240");

    private static readonly OrderbookId Volvo = new("5269");

    // Eight trading days (no Stockholm holiday among them); the last three are the fixture's holdout.
    private static readonly DateOnly[] Days =
        [.. new[] { 1, 2, 5, 6, 7, 8, 9, 12 }.Select(d => new DateOnly(2026, 10, d))];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qa-intraday-cli", Guid.NewGuid().ToString("N"));

    public IntradayBacktestCliTests()
    {
        Directory.CreateDirectory(Config);
        File.WriteAllText(Path.Combine(Config, HoldoutPolicy.FileName), DailyHoldout);
        WriteIntradayHoldout(days: 3);
        foreach (string f in new[] { "costs.avanza-start.json", "costs.avanza-mini.json", "backtest-defaults.json", "market-calendar.XSTO.2026.json" })
        {
            File.Copy(Path.Combine(RepoConfig, f), Path.Combine(Config, f));
        }

        File.WriteAllText(Path.Combine(Config, "universe.json"), """{ "format": "qa-universe/1", "instruments": [] }""");
        new ResearchList([new ResearchEntry(Eric, "ERIC B", "Ericsson B"), new ResearchEntry(Volvo, "VOLV B", "Volvo B")]).Save(Path.Combine(Config, ResearchList.FileName));

        using HistoryStore store = HistoryStore.Open(Store);
        store.RegisterSource(AvanzaChartImporter.AvanzaPriceChart);
        store.RegisterSource(SpreadSampler.Source);
        DateTimeOffset knownAt = new(2026, 10, 12, 16, 0, 0, TimeSpan.Zero);
        store.UpsertIntradayBars(Eric, ChartResolution.FiveMinutes, Bars(breakout: true), AvanzaChartImporter.AvanzaPriceChart, "test", knownAt);
        store.UpsertIntradayBars(Volvo, ChartResolution.FiveMinutes, Bars(breakout: false), AvanzaChartImporter.AvanzaPriceChart, "test", knownAt);
    }

    private string Config => Path.Combine(_dir, "config");

    private string Store => Path.Combine(_dir, "q.duckdb");

    private string Ledger => Path.Combine(_dir, "ledger.jsonl");

    private static string RepoConfig
    {
        get
        {
            for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "QuantAnalyst.sln")))
                {
                    return Path.Combine(d.FullName, "config");
                }
            }

            throw new DirectoryNotFoundException("repository root not found");
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteIntradayHoldout(int days, bool locked = true) =>
        File.WriteAllText(Path.Combine(Config, IntradayHoldout.FileName),
            locked
                ? $"{{\"format\":\"qa-intraday-holdout/1\",\"locked\":true,\"days\":{days},\"unlocked_by\":null,\"unlocked_on\":null,\"reason\":null}}"
                : $"{{\"format\":\"qa-intraday-holdout/1\",\"locked\":false,\"days\":{days},\"unlocked_by\":\"owner\",\"unlocked_on\":\"2026-10-13\",\"reason\":\"test\"}}");

    /// <summary>09:00-17:25 each day; a breakout share closes at 101 from 10:00 (range high 100.1), the other stays at 50.</summary>
    private static List<Bar> Bars(bool breakout, DateOnly? stopsEarlyOn = null)
    {
        var bars = new List<Bar>();
        foreach (DateOnly day in Days)
        {
            for (int k = 0; k < 102; k++)
            {
                Assert.True(MarketTime.TryStockholmToUtc(day.ToDateTime(new TimeOnly(9, 0)).AddMinutes(5 * k), out DateTimeOffset start));
                if (day == stopsEarlyOn && k >= 36)
                {
                    break; // the day's bars end at 12:00
                }

                decimal close = breakout ? (k >= 12 ? 101m : 100m) : 50m;
                decimal open = breakout && k == 12 ? 100m : close;
                bars.Add(new Bar(start, open, Math.Max(open, close) + 0.1m, Math.Min(open, close) - 0.1m, close, 100_000));
            }
        }

        return bars;
    }

    private (int Code, string Output, string Error) Qa(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = QaCli.Run([.. args, "--config-dir", Config, "--store", Store, "--ledger", Ledger], output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void ARun_ReadsUpToTheIntradayHoldout_AndLogsTheTrial()
    {
        (int code, string output, string error) = Qa("intraday", "backtest", "--strategy", "orb-long");

        Assert.True(code == 0, error + output);
        Assert.Contains("Data read up to 2026-10-07: the intraday holdout, the last 3 collected days from 2026-10-08, is locked", output, StringComparison.Ordinal);
        Assert.Contains("Spreads: none measured yet", output, StringComparison.Ordinal);
        Assert.Contains("Orders: market orders at the next bar's open", output, StringComparison.Ordinal);
        Assert.Contains("Trades: 10 fills on 5 trading day(s) (2.0 a day).", output, StringComparison.Ordinal); // ERIC B in and out each day
        Assert.Contains("Study: orb-long|ERIC B,VOLV B|2026-10-01..2026-10-07|avanza-price-chart:5m", output, StringComparison.Ordinal);

        TrialRecord logged = Assert.Single(new TrialLedger(Ledger).ReadAll());
        Assert.Equal((TrialStatus.Ok, "avanza-price-chart:5m", false, "avanza-start"), (logged.Status, logged.DataSource, logged.HoldoutTouched, logged.CostModel));
        Assert.Equal("15", logged.Parameters["range"]);
        Assert.Equal(5, logged.Metrics!.Observations); // one return per day
    }

    [Fact]
    public void ASweep_RunsAndLogsEveryConfiguration()
    {
        (int code, string output, string error) = Qa("intraday", "backtest", "--strategy", "orb-long", "--grid", "range=5,15,30");

        Assert.True(code == 0, error + output);
        Assert.Contains("3 configurations run and logged (3 ok).", output, StringComparison.Ordinal);
        Assert.Contains("PBO: n/a", output, StringComparison.Ordinal); // five days are too few
        Assert.Equal(["15", "30", "5"], new TrialLedger(Ledger).ReadAll().Select(r => r.Parameters["range"]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RunningIntoTheLockedHoldout_IsRefused_AndLogged()
    {
        (int code, string output, _) = Qa("intraday", "backtest", "--strategy", "open-close", "--to", "2026-10-09");

        Assert.Equal(2, code);
        Assert.Contains("REJECTED (holdout)", output, StringComparison.Ordinal);
        Assert.Equal(TrialStatus.RejectedHoldout, Assert.Single(new TrialLedger(Ledger).ReadAll()).Status);
    }

    [Fact]
    public void OnceTheOwnerUnlocksIt_TheHoldoutIsRead_AndTheTrialSaysSo()
    {
        WriteIntradayHoldout(days: 3, locked: false);
        (int code, string output, string error) = Qa("intraday", "backtest", "--strategy", "open-close");

        Assert.True(code == 0, error + output);
        Assert.Contains("2026-10-01..2026-10-12", output, StringComparison.Ordinal);
        Assert.True(Assert.Single(new TrialLedger(Ledger).ReadAll()).HoldoutTouched);
    }

    [Fact]
    public void WithFewerDaysThanTheHoldout_NothingRuns_AndItSaysWhy()
    {
        WriteIntradayHoldout(days: 20);
        (int code, _, string error) = Qa("intraday", "backtest", "--strategy", "orb-long");

        Assert.Equal(1, code);
        Assert.Contains("8 trading day(s) of 5-minute bars are collected; the last 20 are the locked intraday holdout", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Ledger));
    }

    [Fact]
    public void AnIncompleteDay_IsLeftOut_AndAMeasuredSpreadIsUsed()
    {
        using (HistoryStore store = HistoryStore.Open(Store))
        {
            // A third share whose 2 October stops at 12:00.
            var third = new OrderbookId("5247");
            store.UpsertIntradayBars(third, ChartResolution.FiveMinutes, Bars(breakout: false, stopsEarlyOn: new DateOnly(2026, 10, 2)), AvanzaChartImporter.AvanzaPriceChart, "test", DateTimeOffset.UtcNow);
            ResearchList list = ResearchList.Load(Path.Combine(Config, ResearchList.FileName));
            list.With(new ResearchEntry(third, "SHB A", "Handelsbanken A")).Save(Path.Combine(Config, ResearchList.FileName));

            DateTimeOffset at = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
            store.AddSpreadSamples([.. Enumerable.Range(0, 40).Select(m => new SpreadSample(Eric, at.AddMinutes(m), 100.9m, 101.1m, 500m, 500m))], SpreadSampler.Source);
        }

        (int code, string output, string error) = Qa("intraday", "backtest", "--strategy", "orb-long");

        Assert.True(code == 0, error + output);
        Assert.Contains("Data checks: 1 share-day(s) left out whose bars stop more than 30 minutes before the close", output, StringComparison.Ordinal);
        Assert.Contains("Spreads measured by Paper sessions (median half-spread): ERIC B 9.9 bps (40 samples); the others pay the cost model's 5 bps.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ADailyStrategy_IsNotAnIntradayOne()
    {
        (int code, _, string error) = Qa("intraday", "backtest", "--strategy", "ma-cross");

        Assert.Equal(1, code);
        Assert.Contains("Unknown intraday strategy 'ma-cross'. Known: orb-long, late-momentum, open-close.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReport_RunsItsFiveRuns_BeforeTheHoldout_AndSaysNotYet()
    {
        (int code, string output, string error) = Qa("intraday", "report");

        Assert.True(code == 0, error + output);
        Assert.Contains("Intraday go/no-go report (plan 17 step A6, ADR 0006)", output, StringComparison.Ordinal);
        Assert.Contains("Data read up to 2026-10-07", output, StringComparison.Ordinal);
        Assert.Contains("Candidate: orb-long range=", output, StringComparison.Ordinal);
        Assert.Contains("Per trade at Mini: 5 round trips on 5 days", output, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", output, StringComparison.Ordinal);
        Assert.Contains("Free trades: 2.0 trades a day use 500 in about 250 trading days", output, StringComparison.Ordinal);
        Assert.Contains("WAIT  at least 120 trading days", output, StringComparison.Ordinal);
        Assert.Contains("WAIT  holds on the holdout", output, StringComparison.Ordinal);
        Assert.Contains("Verdict: NOT YET", output, StringComparison.Ordinal);

        TrialRecord[] logged = [.. new TrialLedger(Ledger).ReadAll()];
        Assert.Equal(["orb-long", "orb-long", "orb-long", "late-momentum", "open-close"], logged.Select(t => t.Strategy));
        Assert.DoesNotContain(logged, t => t.HoldoutTouched || t.To >= new DateOnly(2026, 10, 8));

        (code, output, error) = Qa("intraday", "report", "--json");
        Assert.True(code == 0, error);
        using var json = System.Text.Json.JsonDocument.Parse(output);
        Assert.StartsWith("NOT YET", json.RootElement.GetProperty("verdict").GetString(), StringComparison.Ordinal);
        Assert.Equal(6, json.RootElement.GetProperty("criteria").GetArrayLength());
    }

    [Fact]
    public void TheReport_WithTheHoldoutUnlocked_ChoosesWithoutIt_ThenChecksTheChoiceOnIt()
    {
        WriteIntradayHoldout(days: 3, locked: false);
        (int code, string output, string error) = Qa("intraday", "report");

        Assert.True(code == 0, error + output);
        Assert.Contains("The intraday holdout from 2026-10-08 is unlocked", output, StringComparison.Ordinal);
        Assert.Contains("Holdout (3 days)", output, StringComparison.Ordinal);
        TrialRecord[] logged = [.. new TrialLedger(Ledger).ReadAll()];
        Assert.Equal([false, false, false, false, false, true, true], logged.Select(t => t.HoldoutTouched));
        Assert.All(logged.Take(5), t => Assert.Equal(new DateOnly(2026, 10, 7), t.To)); // chosen without the holdout
    }

    [Fact]
    public void TheReport_NeedsMoreDaysThanTheHoldout()
    {
        WriteIntradayHoldout(days: 8, locked: false);
        (int code, _, string error) = Qa("intraday", "report");

        Assert.Equal(1, code);
        Assert.Contains("8 trading day(s) of 5-minute bars are collected; the report chooses on the days before the last 8", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHoldoutRule_CountsBackFromTheNewestCollectedDay()
    {
        var rule = new IntradayHoldout(true, 3, "h.json");
        Assert.Null(rule.For(Days.Take(2)));
        Assert.Equal(new DateOnly(2026, 10, 8), rule.For(Days.Reverse())!.Start);

        IntradayHoldout repo = IntradayHoldout.Load(Path.Combine(RepoConfig, IntradayHoldout.FileName)); // whatever the owner has set, it loads
        Assert.InRange(repo.Days, 1, 250);
    }
}
