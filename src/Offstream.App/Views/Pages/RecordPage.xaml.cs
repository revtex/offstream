using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Offstream.App.ViewModels;

namespace Offstream.App.Views.Pages;

/// <summary>The Record tab. Code-behind is wiring only.</summary>
public partial class RecordPage : UserControl
{
    private ScrollViewer? _logScroller;

    public RecordPage(RecordViewModel viewModel)
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
    /// The log lives in a collapsed expander, so at <c>Loaded</c> its template has not been
    /// applied and there is no scroll viewer to find yet — resolving it there once would leave
    /// auto-scroll permanently off. Null until the user opens the log, which is also exactly
    /// when following the tail starts to matter.
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
