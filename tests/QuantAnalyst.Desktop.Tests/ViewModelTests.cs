using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Desktop.Core.Charts;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.ViewModels;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Paper;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>
/// The pages, against a throw-away repository folder. Buttons that would log in to Avanza run against a scripted
/// runner (their command lines are checked); offline buttons run the real CLI code.
/// </summary>
public sealed class ViewModelTests : IDisposable
{
    private readonly TempWorkspace _ws = new();
    private readonly FakeEnvironment _env = new();

    public void Dispose() => _ws.Dispose();

    private ShellViewModel Shell(ScriptedRunner? runner = null)
    {
        QaEngine engine = runner is null ? new QaEngine(new ImmediateDispatcher()) : new QaEngine(new ImmediateDispatcher(), runner.Run, null);
        return new(_ws.Workspace, engine, _ws.Time, new EngineAccountSource(engine, _ws.Workspace), _env);
    }

    private void SaveMaCross() =>
        PaperConfig.SaveStrategy(Path.Combine(_ws.Workspace.ConfigDir, PaperConfig.FileName),
            new PaperStrategy("ma-cross", new Dictionary<string, string> { ["fast"] = "20", ["slow"] = "100" }));

    // ---- Status -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_OnAFreshSetup_ShowsTheChecklist_AndTheButtonGoesToInstruments()
    {
        ShellViewModel shell = Shell();
        await shell.Status.RefreshAsync();

        Assert.Equal("Status, Saturday 2026-09-26 12:00 (Stockholm)", shell.Status.Heading);
        Assert.Contains(shell.Status.Lines, l => l.Label == "Allowlist" && l.IsTodo);
        Assert.Contains(shell.Status.Lines, l => l.Label == "Kill switch" && l.IsOk);
        Assert.StartsWith("1. Choose what may be traded", shell.Status.Steps[0], StringComparison.Ordinal);
        Assert.Equal("Add instruments", shell.Status.NextActionLabel);

        shell.Status.NextActionCommand.Execute(null);
        Assert.Equal(PageKind.Instruments, shell.SelectedPage.Kind);
    }

    [Fact]
    public async Task Status_WhenReady_OffersToStartTheSession_AndAsksTheSessionPageToStart()
    {
        _ws.AllowEricB();
        SaveMaCross();
        var runner = new ScriptedRunner();
        ShellViewModel shell = Shell(runner);
        await shell.Status.RefreshAsync();

        Assert.Equal("Start the Paper session", shell.Status.NextActionLabel);
        shell.Status.NextActionCommand.Execute(null);
        Assert.Equal(PageKind.Session, shell.SelectedPage.Kind);
        await WaitUntil(() => runner.Calls.Count == 1);
        Assert.Equal(["paper", "run"], runner.Calls[0].Take(2));
    }

    [Theory]
    [InlineData("Kill switch", "FAIL", "Go to the session page to clear the kill switch", PageKind.Session)]
    [InlineData("Allowlist", "todo", "Add instruments", PageKind.Instruments)]
    [InlineData("Strategy", "todo", "Choose a strategy", PageKind.Strategy)]
    public void TheNextAction_FollowsTheFirstThingToFix(string label, string mark, string expected, PageKind page)
    {
        (string text, PageKind? target, bool start) = StatusViewModel.NextAction([new StatusLine(mark, label, "x"), new StatusLine("todo", "Strategy", "none")]);
        Assert.Equal((expected, page, false), (text, target, start));
    }

    [Fact]
    public void TheNextAction_IsNoButton_WhenSomethingIsBroken()
    {
        (_, PageKind? target, _) = StatusViewModel.NextAction([new StatusLine("FAIL", "Native engine", "missing")]);
        Assert.Null(target);
    }

    // ---- Instruments ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddingAnInstrument_ImportsItsHistoryThenAllowsIt_AndStopsAtTheFirstFailure()
    {
        var runner = new ScriptedRunner().Answer(0).Answer(0, ["added ERIC B (5240, Ericsson B)"]);
        ShellViewModel shell = Shell(runner);
        shell.LoginMethod = "totp";
        shell.Instruments.NewTicker = " ERIC-B ";
        await shell.Instruments.AddCommand.ExecuteAsync();

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(["history", "import", "ERIC-B"], runner.Calls[0].Take(3));
        Assert.Equal("totp", runner.Calls[0][^1]);
        Assert.Equal(["universe", "add", "ERIC-B"], runner.Calls[1].Take(3));
        Assert.Equal("added ERIC B (5240, Ericsson B)", shell.Instruments.Message);
        Assert.Equal(string.Empty, shell.Instruments.NewTicker);

        var failing = new ScriptedRunner().Answer(1, errors: ["error: No stock with ticker 'XYZ'"]);
        ShellViewModel other = Shell(failing);
        other.Instruments.NewTicker = "XYZ";
        await other.Instruments.AddCommand.ExecuteAsync();
        Assert.Single(failing.Calls);
        Assert.True(other.Instruments.MessageIsError);
        Assert.Contains("No stock with ticker 'XYZ'", other.Instruments.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheInstrumentList_ShowsTheAllowlist_AndRemoveRunsUniverseRemove()
    {
        _ws.AllowEricB();
        var runner = new ScriptedRunner();
        ShellViewModel shell = Shell(runner);
        await shell.Instruments.RefreshAsync();

        InstrumentRow row = Assert.Single(shell.Instruments.Rows);
        Assert.Equal(("ERIC B", "Ericsson B", "5240", "none yet"), (row.Ticker, row.Name, row.OrderbookId, row.History));
        Assert.False(shell.Instruments.AddCommand.CanExecute(null)); // no ticker typed

        await shell.Instruments.RemoveCommand.ExecuteAsync(row);
        Assert.Equal(["universe", "remove", "ERIC B"], runner.Calls.Single().Take(3));
    }

    [Fact]
    public async Task TheInstrumentChart_ShowsTheStoredCloses_WithRanges_AndTheSavedStrategysAverages()
    {
        _ws.AllowEricB();
        PaperConfig.SaveStrategy(Path.Combine(_ws.Workspace.ConfigDir, PaperConfig.FileName),
            new PaperStrategy("ma-cross", new Dictionary<string, string> { ["fast"] = "2", ["slow"] = "4" }));
        var days = new List<DailyBar>();
        for (DateOnly d = new(2026, 6, 1); days.Count < 80; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                decimal close = 100m + days.Count;
                days.Add(new DailyBar(d, close, close, close, close, 1000));
            }
        }

        using (HistoryStore store = HistoryStore.Open(_ws.Workspace.Store))
        {
            store.RegisterSource(AvanzaChartImporter.AvanzaPriceChart);
            store.UpsertDailyBars(new OrderbookId("5240"), days, AvanzaChartImporter.AvanzaPriceChart, "test", _ws.Time.GetUtcNow());
        }

        InstrumentsViewModel page = Shell().Instruments;
        await page.RefreshAsync();
        Assert.Equal("ERIC B", page.SelectedRow!.Ticker); // the first name is shown at once
        await page.LoadChartAsync();

        Assert.Equal(80, page.Chart.Main.Count); // 1Y holds all 80 days
        Assert.Equal(["2-day average", "4-day average"], page.Chart.Overlays.Select(o => o.Name));
        Assert.Equal(100, page.Chart.Baseline);
        Assert.Equal("179,00 kr", page.LastClose);
        Assert.Equal("up", page.RangeDirection);
        Assert.StartsWith("\u25B2 +79,00", page.RangeChange, StringComparison.Ordinal);
        Assert.Contains("2- and 4-day averages", page.ChartNote, StringComparison.Ordinal);

        page.SelectedRange = page.Ranges.Single(r => r.Label == "1M");
        Assert.InRange(page.Chart.Main.Count, 20, 24);
        Assert.EndsWith("· 1M", page.RangeChange, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheInstrumentChart_SaysSoWhenNothingIsStoredYet()
    {
        _ws.AllowEricB();
        InstrumentsViewModel page = Shell().Instruments;
        await page.RefreshAsync();
        await page.LoadChartAsync();
        Assert.True(page.Chart.IsEmpty);
        Assert.StartsWith("No stored prices yet", page.ChartNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStatusPage_ChartsThePaperAccountsValue_FromTheReports()
    {
        StatusViewModel status = Shell().Status;
        await status.RefreshAsync();
        Assert.True(status.PaperChart.IsEmpty);
        Assert.Equal("5\u00A0000,00 kr", status.PaperValue); // the starting cash
        Assert.Contains("after the first Paper session", status.PaperNote, StringComparison.Ordinal);

        foreach ((int day, decimal start, decimal end) in new[] { (28, 5000m, 5006m), (29, 5006m, 5010m) })
        {
            var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 9, day, 15, 32, 0, TimeSpan.Zero));
            var audit = new AuditLog(_ws.Workspace.AuditDir, clock);
            audit.Append("session-start", new { mode = "Paper" });
            audit.Append("end-of-day", new { day = new { startOfDayValue = start, accountValue = end, cash = end, feesPaid = 0m } });
        }

        await status.RefreshAsync();
        Assert.Equal([5000.0, 5006.0, 5010.0], status.PaperChart.Main.Select(p => p.Value));
        Assert.Equal(5000, status.PaperChart.Baseline);
        Assert.Equal("5\u00A0010,00 kr", status.PaperValue);
        Assert.StartsWith("+10,00 kr (+0,20\u00A0%) since 28 Sep", status.PaperChange, StringComparison.Ordinal);
        Assert.Equal("up", status.PaperDirection);
    }

    [Fact]
    public async Task TheOverview_ShowsTheNextSession_TheGate_TheLiveAccount_AndTheKillSwitch()
    {
        ShellViewModel shell = Shell();
        StatusViewModel status = shell.Status;
        await status.RefreshAsync();
        Assert.Equal("Overview", status.Title);
        Assert.Equal("Mon 28 Sep · decides at 09:10", status.NextSessionText); // Saturday 12:00 now
        Assert.Equal("in 1 d 21 h", status.NextSessionIn);
        Assert.Equal(10, status.GateDots.Count);
        Assert.DoesNotContain(true, status.GateDots);
        Assert.Equal("0 of 10 clean Paper days", status.GateProgress);
        Assert.Equal(("Not chosen", "neutral"), (status.LiveAccountText, status.LiveAccountTone));
        Assert.Equal(("Off", "ok"), (status.KillText, status.KillTone));

        var monday = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 15, 32, 0, TimeSpan.Zero));
        var audit = new AuditLog(_ws.Workspace.AuditDir, monday);
        audit.Append("session-start", new { mode = "Paper" });
        audit.Append("end-of-day", new { day = new { startOfDayValue = 5000m, accountValue = 5001m, cash = 5001m, feesPaid = 0m } });
        _env.Write(Trading.Accounts.AccountAllowlist.Variable, "900003193");
        await status.RefreshAsync();
        Assert.Equal([true, false, false, false, false, false, false, false, false, false], status.GateDots);
        Assert.Equal("1 of 10 clean Paper days", status.GateProgress);
        Assert.Equal(("***193", "live"), (status.LiveAccountText, status.LiveAccountTone)); // masked

        _env.Write(Trading.Accounts.AccountAllowlist.Variable, "900003193 700001987");
        shell.KillCommand.Execute(null);
        await status.RefreshAsync();
        Assert.Equal(("Not usable", "FAIL"), (status.LiveAccountText, status.LiveAccountTone));
        Assert.Equal(("ON", "FAIL"), (status.KillText, status.KillTone));
    }

    [Theory]
    [InlineData(-60, "now")]
    [InlineData(0.5, "now")]
    [InlineData(5, "in 5 min")]
    [InlineData(21 * 60 + 10, "in 21 h 10 min")]
    [InlineData(51 * 60 + 30, "in 2 d 3 h")]
    public void TheCountdown_IsShortAndPlain(double minutes, string expected) =>
        Assert.Equal(expected, StatusViewModel.Until(TimeSpan.FromMinutes(minutes)));

    // ---- Strategy ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheStrategyForm_HasNamedFields_AndSavingWritesPaperJson_ThroughTheRealCli()
    {
        ShellViewModel shell = Shell();
        StrategyViewModel s = shell.Strategy;
        await s.RefreshAsync();
        Assert.StartsWith("none", s.Saved, StringComparison.Ordinal);
        Assert.Equal(["buy-and-hold", "ma-cross", "random-targets"], s.Options.Select(o => o.Name));
        Assert.Equal("ma-cross", s.Selected.Name);
        Assert.Equal(["fast", "slow"], s.Selected.Fields.Select(f => f.Key));
        Assert.All(s.Selected.Fields, f => Assert.Equal("required", f.Hint));

        await s.SaveCommand.ExecuteAsync();
        Assert.True(s.MessageIsError);
        Assert.Equal("Fill in fast and slow first.", s.Message);

        s.Selected.Fields[0].Value = "20";
        s.Selected.Fields[1].Value = "100";
        await s.SaveCommand.ExecuteAsync();
        Assert.False(s.MessageIsError, s.Message);
        Assert.Equal("ma-cross(fast=20, slow=100)", s.Saved);
        Assert.Equal("ma-cross", PaperConfig.Load(Path.Combine(_ws.Workspace.ConfigDir, PaperConfig.FileName)).Strategy!.Name);

        // A fresh page reads it back into the form.
        StrategyViewModel again = Shell().Strategy;
        again.Selected = again.Options[0];
        await again.RefreshAsync();
        Assert.Equal("ma-cross", again.Selected.Name);
        Assert.Equal(["20", "100"], again.Selected.Fields.Select(f => f.Value));
    }

    [Fact]
    public async Task Backtest_RunsOnTheAllowlist_LeavingEmptyOptionalFieldsToTheirDefaults()
    {
        var runner = new ScriptedRunner().Answer(0, ["Sharpe 0.41"]);
        StrategyViewModel s = Shell(runner).Strategy;
        s.Selected = s.Options.First(o => o.Name == "random-targets");
        s.Selected.Fields.Single(f => f.Key == "seed").Value = "7";
        s.Selected.Fields.Single(f => f.Key == "p").Value = " ";
        await s.BacktestCommand.ExecuteAsync();

        string[] args = runner.Calls.Single();
        Assert.Equal(["backtest", "run", "--strategy", "random-targets", "--param", "seed=7", "--param", "rebalance=21"], args.Take(8));
        Assert.DoesNotContain("p= ", args);
        Assert.DoesNotContain("--tickers", args);
        Assert.Contains("Sharpe 0.41", s.BacktestOutput);
    }

    // ---- Session ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSession_StartsPaperRun_WithTheLoginMethod_AndCapturesItsLog()
    {
        var runner = new ScriptedRunner().Answer(0, ["Paper session: ma-cross(fast=20, slow=100) on ERIC B", "Session over: 1 order(s)"]);
        ShellViewModel shell = Shell(runner);
        shell.LoginMethod = "totp";
        await shell.Session.StartCommand.ExecuteAsync();

        Assert.Equal(CommandLines.PaperRun(_ws.Workspace, "totp"), runner.Calls.Single());
        Assert.Equal(["Paper session: ma-cross(fast=20, slow=100) on ERIC B", "Session over: 1 order(s)"], shell.Session.Log);
        Assert.Equal("The session is over. Its report is on the Reports page.", shell.Session.Message);
        Assert.Equal(2, shell.Activity.Count);
    }

    [Fact]
    public async Task TheSession_ExitCodesBecomePlainMessages()
    {
        ShellViewModel halted = Shell(new ScriptedRunner().Answer(3));
        await halted.Session.StartCommand.ExecuteAsync();
        Assert.StartsWith("HALTED", halted.Session.Message, StringComparison.Ordinal);

        ShellViewModel locked = Shell(new ScriptedRunner().Answer(4));
        await locked.Session.StartCommand.ExecuteAsync();
        Assert.Contains("Login is locked", locked.Session.Message, StringComparison.Ordinal);

        ShellViewModel failed = Shell(new ScriptedRunner().Answer(1, errors: ["error: No strategy: save one once"]));
        await failed.Session.StartCommand.ExecuteAsync();
        Assert.Equal("The session did not run: error: No strategy: save one once", failed.Session.Message);
        Assert.True(failed.Session.MessageIsError);
    }

    [Fact]
    public async Task Kill_WritesTheFlag_EvenWhileBusy_BlocksStart_AndIsClearedWithAReason()
    {
        ShellViewModel shell = Shell();
        await shell.Session.RefreshAsync();
        Assert.False(shell.Session.KillActive);
        Assert.True(shell.Session.StartCommand.CanExecute(null));

        shell.KillCommand.Execute(null);
        Assert.True(File.Exists(_ws.Workspace.KillFile));
        Assert.StartsWith("KILL written at 12:00:00", shell.Notice, StringComparison.Ordinal);
        await shell.Session.RefreshAsync();
        Assert.True(shell.Session.KillActive);
        Assert.Contains("Kill switch: ON", shell.Session.KillState, StringComparison.Ordinal);
        Assert.False(shell.Session.StartCommand.CanExecute(null));
        Assert.False(shell.Session.ResetKillCommand.CanExecute(null)); // needs a reason

        shell.Session.ResetReason = "checked the log, test kill";
        await shell.Session.ResetKillCommand.ExecuteAsync();
        Assert.False(shell.Session.KillActive, shell.Session.Message);
        Assert.Equal("The kill switch is cleared.", shell.Session.Message);
        Assert.False(File.Exists(_ws.Workspace.KillFile));
        Assert.Contains("kill-reset", File.ReadAllText(Directory.GetFiles(_ws.Workspace.AuditDir).Single()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSessionPage_ShowsTheAccount_AndWhenToStart()
    {
        ShellViewModel shell = Shell();
        await shell.Session.RefreshAsync();
        Assert.StartsWith("No paper account yet", shell.Session.Account, StringComparison.Ordinal);
        Assert.Equal("Next session: Monday 2026-09-28. Start it that morning before 09:10: press Start", shell.Session.Schedule);

        PaperBook.OpenOrCreate(Path.Combine(_ws.Workspace.StateDir, "paper"), PaperConfig.Load(Path.Combine(_ws.Workspace.ConfigDir, PaperConfig.FileName)), null, _ws.Time, out _);
        await shell.Session.RefreshAsync();
        Assert.StartsWith("Cash 5,000.00 SEK · started with 5,000.00", shell.Session.Account, StringComparison.Ordinal);
        Assert.Empty(shell.Session.Positions);
    }

    // ---- Reports ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reports_AreRebuiltFromTheAuditLog_WithTheGateCount()
    {
        ShellViewModel shell = Shell();
        await shell.Reports.RefreshAsync();
        Assert.Empty(shell.Reports.Days);
        Assert.StartsWith("No session has run yet", shell.Reports.Message, StringComparison.Ordinal);

        var monday = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 15, 32, 0, TimeSpan.Zero));
        var audit = new AuditLog(_ws.Workspace.AuditDir, monday);
        audit.Append("session-start", new { mode = "Paper" });
        audit.Append("end-of-day", new { day = new { startOfDayValue = 5000m, accountValue = 5001m, cash = 5001m, feesPaid = 0m } });

        await shell.Reports.RefreshAsync();
        ReportRow day = Assert.Single(shell.Reports.Days);
        Assert.Equal("2026-09-28 Mon", day.DateText);
        Assert.Equal("CLEAN", day.State);
        Assert.Same(day, shell.Reports.Selected);
        Assert.Equal("1 of 10 clean Paper days", shell.Reports.GateProgress);
        Assert.False(shell.Reports.GateMet);
        Assert.NotEmpty(shell.Reports.GateLines);
        Assert.Equal(10, shell.Reports.GateDots.Count); // one dot per day the gate needs
        Assert.Equal(shell.Reports.CleanDays, shell.Reports.GateDots.Count(d => d));
        Assert.False(Directory.Exists(_ws.Workspace.ReportsDir)); // read-only: nothing saved
    }

    // ---- Shell ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheShell_HasItsPages_APaperChip_AndAValidLoginMethod()
    {
        ShellViewModel shell = Shell();
        Assert.Equal(["Overview", "Trading", "Charts", "Accounts", "Instruments", "Strategy", "Reports"], shell.Pages.Select(p => p.Title));
        Assert.Equal(PageKind.Status, shell.SelectedPage.Kind);
        Assert.StartsWith("PAPER", shell.ModeBanner, StringComparison.Ordinal);

        shell.LoginMethod = "password";
        Assert.Contains(shell.LoginMethod, shell.LoginMethods);
        shell.LoginMethod = "totp";
        Assert.Equal("totp", shell.LoginMethod);
        Assert.Equal("Ready", shell.BusyText);
        Assert.False(shell.CancelCommand.CanExecute(null));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }
}
