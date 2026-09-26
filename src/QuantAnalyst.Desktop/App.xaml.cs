using System.Windows;
using System.Windows.Threading;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.ViewModels;

namespace QuantAnalyst.Desktop;

/// <summary>
/// Starts the window in the repository folder (the same config\, data\, state\ … the CLI uses). The app trades Paper
/// only; promotion and anything live stay in the terminal, as your own commands (docs/guide.md).
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        Workspace? workspace = (e.Args.Length > 0 ? Workspace.Find(e.Args[0]) : null)
                               ?? Workspace.Find(AppContext.BaseDirectory)
                               ?? Workspace.Find(Environment.CurrentDirectory);
        if (workspace is null)
        {
            MessageBox.Show(
                "QuantAnalyst could not find its repository (the folder with QuantAnalyst.sln). Start it with .\\qa-app.ps1 from the repository folder.",
                "QuantAnalyst", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        Environment.CurrentDirectory = workspace.Root;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QA_RUNNER")))
        {
            Environment.SetEnvironmentVariable("QA_RUNNER", "owner"); // who ran each backtest, in the trial ledger
        }

        var engine = new QaEngine(new WpfDispatcher(Dispatcher));
        var shell = new ShellViewModel(workspace, engine, TimeProvider.System);
        var window = new MainWindow(shell);
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Something went wrong: {e.Exception.Message}\n\nThe window stays open; the activity log below has the details.", "QuantAnalyst",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
