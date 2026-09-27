using System.Globalization;
using QuantAnalyst.Cli;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Desktop.Core.Mvvm;

namespace QuantAnalyst.Desktop.Core.Engine;

/// <summary>One line a command printed; <see cref="IsError"/> for its error stream.</summary>
public sealed record OutputLine(string Text, bool IsError);

/// <summary>How a command ended. Exit codes are the CLI's (0 ok, 1 error, 2 rejected/broken, 3 halt, 4 locked).</summary>
public sealed record CommandResult(string Title, int ExitCode, bool Cancelled, IReadOnlyList<OutputLine> Lines)
{
    public bool Succeeded => ExitCode == 0 && !Cancelled;

    public IEnumerable<string> Errors => Lines.Where(l => l.IsError).Select(l => l.Text);

    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Text));
}

/// <summary>Runs one <c>qa</c> command: the CLI's own entry point, or a test double.</summary>
internal delegate int QaRunner(string[] args, TextWriter output, TextWriter error, AvanzaCliServices services);

/// <summary>
/// Runs <c>qa</c> commands in-process (docs/plans/10-windows-app.md): the same code, rules and exit codes as the
/// terminal, on a background thread, <b>one at a time</b>. Every printed line reaches <see cref="LineWritten"/> on the UI
/// thread as it is written. <see cref="Cancel"/> is the terminal's Ctrl+C. A BankID login shows its QR code through
/// <see cref="BankId"/>. Typed confirmations get no input (<see cref="TextReader.Null"/>), so nothing waits for a keyboard.
/// </summary>
public sealed class QaEngine : ObservableObject
{
    /// <summary>The exit code reported for a command stopped with <see cref="Cancel"/> before it finished on its own.</summary>
    public const int CancelledExitCode = 130;

    private readonly IUiDispatcher _ui;
    private readonly QaRunner _run;
    private readonly AvanzaCliServices _services;
    private CancellationTokenSource? _cts;
    private string? _current;

    public QaEngine(IUiDispatcher ui)
        : this(ui, null, null)
    {
    }

    /// <summary>Test seam: another runner (e.g. one that blocks until cancelled) or other services (e.g. a fake Avanza).</summary>
    internal QaEngine(IUiDispatcher ui, QaRunner? run, AvanzaCliServices? services)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _run = run ?? QaCli.Run;
        _services = services ?? AvanzaCliServices.Default;
        BankId = new AppBankIdPrompt(ui);
    }

    /// <summary>Raised on the UI thread for every line any command prints.</summary>
    public event Action<OutputLine>? LineWritten;

    public AppBankIdPrompt BankId { get; }

    /// <summary>Gets the UI thread's dispatcher (for models that apply a running command's events on it).</summary>
    internal IUiDispatcher Ui => _ui;

    /// <summary>Gets the title of the running command, or null when idle.</summary>
    public string? CurrentCommand
    {
        get => _current;
        private set
        {
            if (Set(ref _current, value))
            {
                OnPropertyChanged(nameof(IsBusy));
            }
        }
    }

    public bool IsBusy => _current is not null;

    /// <summary>
    /// Runs <c>qa</c> with <paramref name="args"/> and returns when it has finished. Throws when another command is
    /// running: the caller disables its buttons while <see cref="IsBusy"/>. A Paper session reports to
    /// <paramref name="observer"/> (the Trading page's live charts), which can't change what it does.
    /// </summary>
    public async Task<CommandResult> RunAsync(string title, IReadOnlyList<string> args, Trading.Observation.ISessionObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (IsBusy)
        {
            throw new InvalidOperationException($"'{CurrentCommand}' is still running; wait for it or stop it first.");
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        CurrentCommand = title;
        var lines = new List<OutputLine>();
        void Emit(string text, bool isError)
        {
            var line = new OutputLine(text, isError);
            lock (lines)
            {
                lines.Add(line);
            }

            _ui.Post(() => LineWritten?.Invoke(line));
        }

        var output = new LineWriter(t => Emit(t, false));
        var error = new LineWriter(t => Emit(t, true));
        AvanzaCliServices services = _services with { Cancellation = cts.Token, BankIdPrompt = (_, _) => BankId, Input = TextReader.Null, SessionObserver = observer };
        int code;
        bool cancelled = false;
        try
        {
            code = await Task.Run(() => _run([.. args], output, error, services), CancellationToken.None).ConfigureAwait(false);
            cancelled = cts.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            code = CancelledExitCode;
            cancelled = true;
            Emit("Stopped.", true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            code = 1;
            Emit(string.Create(CultureInfo.InvariantCulture, $"error: {ex.GetType().Name}: {ex.Message}"), true);
        }
        finally
        {
            output.Complete();
            error.Complete();
            _cts = null;
            _ui.Post(() =>
            {
                BankId.Reset();
                CurrentCommand = null;
            });
        }

        lock (lines)
        {
            return new CommandResult(title, code, cancelled, [.. lines]);
        }
    }

    /// <summary>
    /// Runs a read-only Avanza query in-process (e.g. the Accounts page's overview), one at a time like a command:
    /// busy while it runs, stopped by <see cref="Cancel"/>, with the app's BankID QR code. Its result comes back
    /// typed instead of printed; the activity log gets one line saying what ran.
    /// </summary>
    internal async Task<T> QueryAsync<T>(string title, Func<AvanzaCliServices, Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (IsBusy)
        {
            throw new InvalidOperationException($"'{CurrentCommand}' is still running; wait for it or stop it first.");
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        CurrentCommand = title;
        _ui.Post(() => LineWritten?.Invoke(new OutputLine(title + " …", false)));
        AvanzaCliServices services = _services with { Cancellation = cts.Token, BankIdPrompt = (_, _) => BankId, Input = TextReader.Null };
        try
        {
            return await Task.Run(() => body(services), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _cts = null;
            _ui.Post(() =>
            {
                BankId.Reset();
                CurrentCommand = null;
            });
        }
    }

    /// <summary>Stops the running command the way Ctrl+C does (a Paper session cancels its orders and writes its report).</summary>
    public void Cancel() => _cts?.Cancel();
}
