using Offstream.App.ViewModels;
using Offstream.App.Views.Pages;
using Wpf.Ui.Controls;

namespace Offstream.App.Views;

/// <summary>
/// The shell window: title bar, tab strip, transport, and the pages.
/// </summary>
/// <remarks>
/// <para>
/// Code-behind is wiring only, per the MVVM convention in CLAUDE.md. What has to live here is
/// putting the pages into their hosts: a ViewModel that held <see cref="System.Windows.Controls.Page"/>
/// instances would be a ViewModel that references the views it is supposed to be independent of.
/// The window is the one place that is allowed to know about both.
/// </para>
/// <para>
/// <b>Every page is loaded at once and switched by visibility.</b> Every page is a DI
/// singleton, so this costs one construction each for the life of the process and buys the thing
/// a navigation frame had to be configured for: state survives a tab switch. The activity log, a
/// half-typed filename template and the meter's own sampling timer are all still there on the way
/// back. It also drops a WPF-UI trap — its content presenter wrapped every page in a scroll
/// viewer that measured with infinite height, which is what used to make the Record page's log
/// grow off the bottom of the window instead of scrolling inside its own box.
/// </para>
/// </remarks>
public partial class ShellWindow : FluentWindow
{
    private readonly ShellViewModel _viewModel;

    public ShellWindow(
        ShellViewModel viewModel,
        RecordPage recordPage,
        SettingsPage settingsPage,
        AdvancedPage advancedPage,
        MetadataPage metadataPage,
        LogsPage logsPage)
    {
        ArgumentNullException.ThrowIfNull(recordPage);
        ArgumentNullException.ThrowIfNull(settingsPage);
        ArgumentNullException.ThrowIfNull(advancedPage);
        ArgumentNullException.ThrowIfNull(metadataPage);
        ArgumentNullException.ThrowIfNull(logsPage);

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        DataContext = viewModel;
        InitializeComponent();

        RecordHost.Content = recordPage;
        SettingsHost.Content = settingsPage;
        AdvancedHost.Content = advancedPage;
        MetadataHost.Content = metadataPage;
        LogsHost.Content = logsPage;

        StateChanged += OnStateChanged;
        viewModel.ShowRequested += (_, _) => Surface();
        viewModel.ExitRequested += (_, _) => Close();
    }

    /// <summary>
    /// Hides the window into the tray when it is minimised and the setting asks for it.
    /// </summary>
    /// <remarks>
    /// Hiding is what removes the taskbar button; a minimised-but-visible window would leave
    /// the app in two places at once. <see cref="ShellViewModel.IsInTray"/> is set here rather
    /// than inferred from <see cref="UIElement.Visibility"/> so the icon appears in the same
    /// frame the window leaves, with no flicker of an empty taskbar slot.
    /// </remarks>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Qualified: the inherited WindowState property hides the enum of the same name.
        if (WindowState != System.Windows.WindowState.Minimized || !_viewModel.MinimizeToTray) return;

        _viewModel.IsInTray = true;
        Hide();
    }

    /// <summary>
    /// Brings the window back from the tray, or to the front if it was merely buried.
    /// </summary>
    /// <remarks>
    /// All three steps are needed and in this order. <see cref="Window.Show"/> undoes the
    /// <see cref="Window.Hide"/>; the window is still <see cref="WindowState.Minimized"/> until
    /// it is restored, so skipping that shows a window that is not on screen; and without
    /// <see cref="Window.Activate"/> it comes back behind whatever the user was looking at.
    /// </remarks>
    private void Surface()
    {
        _viewModel.IsInTray = false;

        Show();
        WindowState = System.Windows.WindowState.Normal;
        _ = Activate();
    }
}
