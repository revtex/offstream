using Wpf.Ui.Abstractions;

namespace Offstream.App.Services;

/// <summary>
/// Hands WPF-UI's navigation the page instances the DI container built.
/// </summary>
/// <remarks>
/// Without this, <see cref="Wpf.Ui.Controls.NavigationView"/> falls back to
/// <see cref="Wpf.Ui.Controls.NavigationViewActivator"/>, which reflects over a page's
/// constructors and picks one it can satisfy — so a page whose ViewModel dependency is missing
/// gets constructed anyway with nulls rather than failing. Resolving through the container
/// instead means a page is either fully wired or it does not appear at all, and
/// <c>AppServicesTests</c> can assert the wiring without starting a window.
/// </remarks>
public sealed class PageProvider(IServiceProvider services) : INavigationViewPageProvider
{
    private readonly IServiceProvider _services =
        services ?? throw new ArgumentNullException(nameof(services));

    /// <inheritdoc />
    public object? GetPage(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);
        return _services.GetService(pageType);
    }
}
