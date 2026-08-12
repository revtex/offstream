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

    public ShellWindow(
        ShellViewModel viewModel,
        INavigationService navigationService,
        INavigationViewPageProvider pageProvider,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(navigationService);

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
