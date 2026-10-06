using System.Windows;
using QuantAnalyst.Desktop.Core.ViewModels;

namespace QuantAnalyst.Desktop;

/// <summary>A page in a window of its own. It refreshes the page when it opens and tells the shell when it closes.</summary>
public partial class PageWindow : Window
{
    public PageWindow(PageViewModel page, Action<PageViewModel> closed)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(closed);
        InitializeComponent();
        DataContext = page;
        Loaded += async (_, _) => await page.RefreshAsync();
        Closed += (_, _) => closed(page);
    }
}
