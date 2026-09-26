namespace QuantAnalyst.Desktop.Core.Engine;

/// <summary>Runs an action on the UI thread (WPF: the dispatcher). View-model state changes only there.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Runs actions at once, on the calling thread: for tests.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
