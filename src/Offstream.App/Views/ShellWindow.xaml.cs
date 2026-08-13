using Offstream.App.ViewModels;
using Offstream.App.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace Offstream.App.Views;

/// <summary>
/// The shell window: title bar, top navigation, and the frame the pages render into.
/// </summary>
/// <remarks>
/// Code-behind is wiring only, per the MVVM convention in CLAUDE.md. The wiring that has to
/// live here is navigation: <see cref="INavigationService"/> needs the concrete
/// <see cref="NavigationView"/> from the XAML, which nothing outside this class can reach.
/// </remarks>
public partial class ShellWindow : FluentWindow, INavigationWindow
{
    private readonly INavigationViewPageProvider _pageProvider;
    private readonly IServiceProvider _services;
    private readonly ShellViewModel _viewModel;

    public ShellWindow(
        ShellViewModel viewModel,
        INavigationService navigationService,
        INavigationViewPageProvider pageProvider,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(navigationService);

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _pageProvider = pageProvider ?? throw new ArgumentNullException(nameof(pageProvider));
        _services = services ?? throw new ArgumentNullException(nameof(services));

        DataContext = viewModel;
        InitializeComponent();

        SetPageService(_pageProvider);
        SetServiceProvider(_services);
        navigationService.SetNavigationControl(RootNavigation);

        // Nothing is selected until something navigates, and an empty frame on launch reads as
        // a broken window rather than an empty tab.
        Loaded += (_, _) => Navigate(typeof(RecordPage));

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

    /// <inheritdoc />
    public INavigationView GetNavigation() => RootNavigation;

    /// <inheritdoc />
    public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);

    /// <inheritdoc />
    public void SetPageService(INavigationViewPageProvider navigationViewPageProvider) =>
        RootNavigation.SetPageProviderService(navigationViewPageProvider);

    /// <inheritdoc />
    public void SetServiceProvider(IServiceProvider serviceProvider) =>
        RootNavigation.SetServiceProvider(serviceProvider);

    /// <inheritdoc />
    public void ShowWindow() => Show();

    /// <inheritdoc />
    public void CloseWindow() => Close();
}
