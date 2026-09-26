using System.Collections.Specialized;
using System.Windows.Controls;
using QuantAnalyst.Desktop.Core.ViewModels;

namespace QuantAnalyst.Desktop.Views;

/// <summary>The session page; the only code-behind keeps the log scrolled to its newest line.</summary>
public partial class SessionView : UserControl
{
    public SessionView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SessionViewModel old)
            {
                ((INotifyCollectionChanged)old.Log).CollectionChanged -= ScrollToEnd;
            }

            if (e.NewValue is SessionViewModel now)
            {
                ((INotifyCollectionChanged)now.Log).CollectionChanged += ScrollToEnd;
            }
        };
    }

    private void ScrollToEnd(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }
}
