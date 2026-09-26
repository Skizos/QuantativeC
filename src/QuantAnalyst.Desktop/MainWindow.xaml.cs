using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using QuantAnalyst.Desktop.Core.ViewModels;

namespace QuantAnalyst.Desktop;

/// <summary>
/// The window. Code-behind only for what XAML can't say: the periodic refresh, keeping the activity log scrolled to
/// the end, and not closing under a running session without asking (closing stops it the way Stop does).
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(20) };
    private bool _closing;

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        InitializeComponent();
        DataContext = shell;
        ((INotifyCollectionChanged)shell.Activity).CollectionChanged += (_, _) =>
        {
            if (ActivityList.Items.Count > 0)
            {
                ActivityList.ScrollIntoView(ActivityList.Items[^1]);
            }
        };
        _refresh.Tick += async (_, _) =>
        {
            // Pages that only read files refresh while idle; the session page also refreshes the paper account while it runs.
            if (!shell.Engine.IsBusy || shell.SelectedPage is SessionViewModel)
            {
                await shell.RefreshCurrentAsync();
            }
        };
        Loaded += async (_, _) =>
        {
            _refresh.Start();
            await shell.Status.RefreshAsync();
        };
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing || !_shell.Engine.IsBusy)
        {
            return;
        }

        e.Cancel = true;
        string what = _shell.Engine.CurrentCommand ?? "a command";
        MessageBoxResult answer = MessageBox.Show(this,
            $"{what} is still running. Stop it and close? A Paper session then cancels its working orders and writes its partial report.",
            "QuantAnalyst", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _closing = true;
        _shell.Engine.Cancel();
        for (int i = 0; i < 300 && _shell.Engine.IsBusy; i++)
        {
            await Task.Delay(100);
        }

        Close();
    }
}
