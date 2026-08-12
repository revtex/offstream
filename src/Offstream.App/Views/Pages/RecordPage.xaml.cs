using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Offstream.App.ViewModels;

namespace Offstream.App.Views.Pages;

/// <summary>The Record tab. Code-behind is wiring only.</summary>
public partial class RecordPage : Page
{
    private ScrollViewer? _logScroller;

    public RecordPage(RecordViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        DataContext = viewModel;
        InitializeComponent();

        ((INotifyCollectionChanged)viewModel.LogLines).CollectionChanged += OnLogChanged;
        Loaded += (_, _) => _logScroller = FindScroller(LogList);
    }

    /// <summary>
    /// Follows the log, but only while the user is already at the end of it.
    /// </summary>
    /// <remarks>
    /// A console that always jumps to the newest line makes reading back through a failure
    /// impossible during a session — which is exactly when someone wants to. Scrolling up is
    /// therefore taken as "stop following"; scrolling back to the bottom resumes.
    /// </remarks>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _logScroller is null) return;

        // A one-pixel tolerance: the offset lands on fractional values after a resize, so an
        // exact comparison reads as "scrolled up" when the view is visibly at the bottom.
        if (_logScroller.ScrollableHeight - _logScroller.VerticalOffset > 1) return;

        _logScroller.ScrollToEnd();
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            if (child is ScrollViewer scroller) return scroller;

            if (FindScroller(child) is { } found) return found;
        }

        return null;
    }
}
