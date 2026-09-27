using System.Windows;
using QuantAnalyst.Desktop.Core.ViewModels;

namespace QuantAnalyst.Desktop;

/// <summary>A charts page in its own window. Closing it lets go of the running session and the engine.</summary>
public partial class ChartsWindow : Window
{
    public ChartsWindow(ChartsViewModel charts)
    {
        ArgumentNullException.ThrowIfNull(charts);
        InitializeComponent();
        DataContext = charts;
        Loaded += async (_, _) => await charts.RefreshAsync();
        Closed += (_, _) => charts.Dispose();
    }
}
