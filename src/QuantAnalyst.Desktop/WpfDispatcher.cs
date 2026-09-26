using System.Windows.Threading;
using QuantAnalyst.Desktop.Core.Engine;

namespace QuantAnalyst.Desktop;

/// <summary>Runs view-model updates on the WPF UI thread.</summary>
public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
