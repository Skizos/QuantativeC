using System.ComponentModel;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Mvvm;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>The app's pages, in navigation order.</summary>
public enum PageKind
{
    Status,
    Instruments,
    Strategy,
    Session,
    Reports,
}

/// <summary>
/// A page: its title, a message line (with whether it is an error), a refresh, and awareness of the engine's busy
/// state so buttons that start a command are disabled while another one runs.
/// </summary>
public abstract class PageViewModel : ObservableObject
{
    private string _message = string.Empty;
    private bool _messageIsError;

    protected PageViewModel(PageKind kind, string title, string subtitle, QaEngine engine)
    {
        Kind = kind;
        Title = title;
        Subtitle = subtitle;
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Engine.PropertyChanged += OnEnginePropertyChanged;
    }

    public PageKind Kind { get; }

    public string Title { get; }

    /// <summary>Gets one line under the title saying what the page is for.</summary>
    public string Subtitle { get; }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    public bool IsBusy => Engine.IsBusy;

    protected QaEngine Engine { get; }

    /// <summary>Reloads what the page shows from the files the CLI uses. Never throws: problems become the message.</summary>
    public abstract Task RefreshAsync();

    protected void Say(string text, bool isError = false)
    {
        Message = text;
        MessageIsError = isError;
    }

    /// <summary>What went wrong in a failed command, in one line: its first error, else its last output line.</summary>
    protected static string Why(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Cancelled
            ? "stopped"
            : result.Errors.FirstOrDefault() ?? (result.Lines.Count > 0 ? result.Lines[^1].Text : $"exit code {result.ExitCode}");
    }

    /// <summary>Called when the engine starts or finishes a command: refresh the page's commands here.</summary>
    protected virtual void OnBusyChanged()
    {
    }

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QaEngine.IsBusy))
        {
            OnPropertyChanged(nameof(IsBusy));
            OnBusyChanged();
        }
    }
}
