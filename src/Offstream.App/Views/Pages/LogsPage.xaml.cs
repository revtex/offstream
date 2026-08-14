using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Offstream.App.ViewModels;

namespace Offstream.App.Views.Pages;

/// <summary>The Logs tab. Code-behind is wiring and auto-scroll only.</summary>
/// <remarks>
/// Shares <see cref="RecordViewModel"/> with the Record page — see the page's own remarks for
/// why the log does not get a ViewModel of its own.
/// </remarks>
public partial class LogsPage : UserControl
{
    private ScrollViewer? _logScroller;

    public LogsPage(RecordViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        DataContext = viewModel;
        InitializeComponent();

        ((INotifyCollectionChanged)viewModel.LogLines).CollectionChanged += OnLogChanged;
    }

    /// <summary>
    /// The log's scroll viewer, found on first use rather than at load.
    /// </summary>
    /// <remarks>
    /// The shell keeps every page loaded but hides the ones that are not showing, so the template
    /// is applied — but resolving this once at <c>Loaded</c> would still be a visual-tree walk
    /// racing the list's own template application. Null until the first line arrives costs
    /// nothing and cannot be early.
    /// </remarks>
    private ScrollViewer? Scroller => _logScroller ??= FindScroller(LogList);

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
        if (e.Action != NotifyCollectionChangedAction.Add || Scroller is not { } scroller) return;

        // A one-pixel tolerance: the offset lands on fractional values after a resize, so an
        // exact comparison reads as "scrolled up" when the view is visibly at the bottom.
        if (scroller.ScrollableHeight - scroller.VerticalOffset > 1) return;

        scroller.ScrollToEnd();
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
