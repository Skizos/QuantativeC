using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Desktop.Core.Engine;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>The in-process engine: the CLI's own code, streamed line by line, one command at a time, stoppable.</summary>
public sealed class EngineTests : IDisposable
{
    private readonly TempWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public async Task ARealQaCommand_RunsInProcess_AndEveryLineIsStreamed()
    {
        var engine = new QaEngine(new ImmediateDispatcher());
        var streamed = new List<OutputLine>();
        engine.LineWritten += streamed.Add;

        CommandResult result = await engine.RunAsync("List", ["universe", "list", "--config-dir", _ws.Workspace.ConfigDir]);

        Assert.True(result.Succeeded, result.Text);
        Assert.Contains("The allowlist is empty", result.Text, StringComparison.Ordinal);
        Assert.Equal(result.Lines, streamed);
        Assert.False(engine.IsBusy);
    }

    [Fact]
    public async Task AFailingCommand_ReportsItsExitCode_AndItsErrorLines()
    {
        var engine = new QaEngine(new ImmediateDispatcher());
        CommandResult result = await engine.RunAsync("Add", ["universe", "add", "ERIC-B", "--config-dir", _ws.Workspace.ConfigDir, "--store", _ws.Workspace.Store]);

        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("No history store", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneCommandAtATime_AndCancelStopsTheRunningOne_LikeCtrlC()
    {
        var started = new TaskCompletionSource();
        int Blocking(string[] args, TextWriter output, TextWriter error, AvanzaCliServices services)
        {
            output.WriteLine("waiting for 09:10");
            started.SetResult();
            services.Cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            services.Cancellation.ThrowIfCancellationRequested();
            return 0;
        }

        var engine = new QaEngine(new ImmediateDispatcher(), Blocking, null);
        Task<CommandResult> running = engine.RunAsync("Paper session", ["paper", "run"]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(engine.IsBusy);
        Assert.Equal("Paper session", engine.CurrentCommand);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync("Other", ["status"]));

        engine.Cancel();
        CommandResult result = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(result.Cancelled);
        Assert.Equal(QaEngine.CancelledExitCode, result.ExitCode);
        Assert.Equal(["waiting for 09:10", "Stopped."], result.Lines.Select(l => l.Text));
        Assert.False(engine.IsBusy);
    }

    [Fact]
    public async Task ABankIdLogin_ShowsItsQrCodeAsAnImage_WhileWaiting_AndClearsItAfterwards()
    {
        QaEngine? engine = null;
        (bool Waiting, byte[]? Png, string Status)? during = null;
        int Login(string[] args, TextWriter output, TextWriter error, AvanzaCliServices services)
        {
            Avanza.Auth.IBankIdPrompt prompt = services.BankIdPrompt!(error, false);
            prompt.ShowQrCode("bankid.0123456789abcdef.0.feedface");
            prompt.ShowStatus("Waiting for you to approve in the BankID app …");
            during = (engine!.BankId.IsWaiting, engine.BankId.QrPng, engine.BankId.Status);
            prompt.Completed();
            return 0;
        }

        engine = new QaEngine(new ImmediateDispatcher(), Login, null);
        await engine.RunAsync("Login", ["login"]);

        Assert.NotNull(during);
        Assert.True(during.Value.Waiting);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, during.Value.Png![..4]);
        Assert.Equal("Waiting for you to approve in the BankID app …", during.Value.Status);
        Assert.False(engine.BankId.IsWaiting);
        Assert.Null(engine.BankId.QrPng);
    }

    [Fact]
    public async Task Commands_GetNoKeyboard_SoNothingCanWaitForTypedInput()
    {
        string? typed = "not read";
        int Reads(string[] args, TextWriter output, TextWriter error, AvanzaCliServices services)
        {
            typed = services.Input.ReadLine();
            return 0;
        }

        await new QaEngine(new ImmediateDispatcher(), Reads, null).RunAsync("Read", ["x"]);
        Assert.Null(typed);
    }

    [Fact]
    public async Task AnUnexpectedException_IsAFailedResult_NotACrash()
    {
        int Throws(string[] args, TextWriter output, TextWriter error, AvanzaCliServices services) => throw new FormatException("bad");
        var engine = new QaEngine(new ImmediateDispatcher(), Throws, null);

        CommandResult result = await engine.RunAsync("Boom", ["x"]);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FormatException: bad", result.Errors.Single(), StringComparison.Ordinal);
        Assert.False(engine.IsBusy);
    }

    [Fact]
    public void TheLineWriter_SplitsLines_AndKeepsAPartialLineUntilComplete()
    {
        var lines = new List<string>();
        var w = new LineWriter(lines.Add);
        w.Write("one\r\ntw");
        w.Write('o');
        w.WriteLine();
        w.Write("three");
        Assert.Equal(["one", "two"], lines);
        w.Complete();
        Assert.Equal(["one", "two", "three"], lines);
    }

    [Fact]
    public void TheWorkspace_IsFoundAboveTheApp_AndEveryPathIsAbsoluteInsideIt()
    {
        string deep = Path.Combine(_ws.Root, "src", "QuantAnalyst.Desktop", "bin", "Debug");
        Directory.CreateDirectory(deep);
        Workspace found = Workspace.Find(deep)!;

        Assert.Equal(_ws.Root, found.Root);
        string[] paths = [found.ConfigDir, found.Store, found.StateDir, found.AuditDir, found.ReportsDir, found.PromotionDir, found.KillFile, found.Ledger];
        Assert.All(paths, p => Assert.True(Path.IsPathRooted(p) && p.StartsWith(found.Root, StringComparison.Ordinal), p));
        Assert.Equal(Path.Combine(found.Root, "data", "quant.duckdb"), found.Store);
        Assert.Null(Workspace.Find(Path.GetTempPath()));
    }

    [Fact]
    public void CommandLines_AreWhatYouWouldType_PaperOnly_WithEveryPathExplicit()
    {
        Workspace w = _ws.Workspace;
        IReadOnlyList<string> run = CommandLines.PaperRun(w, "bankid");
        Assert.Equal(["paper", "run"], run.Take(2));
        Assert.Equal("bankid", run[^1]);
        foreach (string option in new[] { "--config-dir", "--store", "--state-dir", "--audit-dir", "--kill-file", "--promotion-dir", "--reports-dir" })
        {
            Assert.True(Path.IsPathRooted(run[run.ToList().IndexOf(option) + 1]), option);
        }

        Assert.Equal(
            "qa paper run --config-dir config --store data/quant.duckdb --state-dir state --audit-dir audit --kill-file KILL --promotion-dir promotion --reports-dir reports/eod --login bankid",
            CommandLines.Display(w, run).Replace('\\', '/'));
        Assert.Equal(
            ["backtest", "run", "--strategy", "ma-cross", "--param", "fast=20", "--param", "slow=100"],
            CommandLines.Backtest(w, "ma-cross", [new("fast", "20"), new("slow", "100")]).Take(8));
        Assert.DoesNotContain("--tickers", CommandLines.Backtest(w, "buy-and-hold", []));

        IReadOnlyList<string>[] all =
        [
            run, CommandLines.HistoryImport(w, "ERIC-B", "totp"), CommandLines.UniverseAdd(w, "ERIC-B"), CommandLines.UniverseRemove(w, "ERIC-B"),
            CommandLines.Backtest(w, "ma-cross", []), CommandLines.SaveStrategy(w, "ma-cross", []), CommandLines.KillReset(w, "checked"),
        ];
        Assert.All(all, args => Assert.DoesNotContain(args, a => a is "--mode" or "promote" || a.Contains("confirm", StringComparison.OrdinalIgnoreCase)));
    }
}
