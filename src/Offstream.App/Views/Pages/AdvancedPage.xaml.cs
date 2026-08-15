using System.Windows;
using System.Windows.Controls;
using Offstream.App.ViewModels;

namespace Offstream.App.Views.Pages;

/// <summary>The Advanced tab. Code-behind is wiring only.</summary>
public partial class AdvancedPage : UserControl
{
    public AdvancedPage(AdvancedViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>
    /// Opens the licence and third-party notices.
    /// </summary>
    /// <remarks>
    /// Opening a window is a view's job, not a ViewModel's — a command for this would put a
    /// <see cref="Window"/> reference into <see cref="AdvancedViewModel"/>, which is the thing
    /// the MVVM convention in CLAUDE.md exists to prevent. <c>ShowDialog</c> rather than
    /// <c>Show</c>: it is what makes the Close button's <c>IsCancel</c> close the window, and it
    /// stops a second copy opening behind the first.
    /// </remarks>
    private void OnShowNotices(object sender, RoutedEventArgs e) =>
        new NoticesWindow { Owner = Window.GetWindow(this) }.ShowDialog();
}
