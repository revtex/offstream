using System.Windows.Controls;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Offstream.App.Services;
using Offstream.App.ViewModels;
using Offstream.App.Views;
using Offstream.App.Views.Pages;
using Offstream.Core.Diagnostics;
using Offstream.Core.Settings;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// The composition root.
/// </summary>
/// <remarks>
/// <para>
/// Asserts what is <em>registered</em>, never what resolves. Building a page means constructing
/// a WPF control, which needs an STA thread and an <see cref="System.Windows.Application"/> —
/// so a resolution test would be a desktop test, excluded from CI, and this is exactly the
/// wiring that most needs checking on every push.
/// </para>
/// <para>
/// A page missing from the container is invisible until someone clicks that tab, because
/// <see cref="PageProvider"/> hands navigation a null and the failure surfaces as a blank frame
/// rather than an exception at startup.
/// </para>
/// </remarks>
public sealed class AppServicesTests
{
    [Fact]
    public void AddOffstream_RegistersEveryPageTheNavigationCanReach()
    {
        var services = Build();

        var pages = typeof(RecordPage).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(RecordPage).Namespace)
            .Where(type => type.IsSubclassOf(typeof(Page)) && !type.IsAbstract)
            .ToList();

        Assert.NotEmpty(pages);

        foreach (var page in pages)
        {
            Assert.Contains(services, descriptor => descriptor.ServiceType == page);
        }
    }

    [Theory]
    [InlineData(typeof(ShellWindow))]
    [InlineData(typeof(ShellViewModel))]
    [InlineData(typeof(RecordViewModel))]
    [InlineData(typeof(INavigationService))]
    [InlineData(typeof(INavigationViewPageProvider))]
    [InlineData(typeof(SettingsStore))]
    [InlineData(typeof(SettingsDocument))]
    [InlineData(typeof(SettingsViewModel))]
    [InlineData(typeof(AdvancedViewModel))]
    [InlineData(typeof(IAudioDeviceCatalog))]
    [InlineData(typeof(IFolderPicker))]
    [InlineData(typeof(RecordingController))]
    [InlineData(typeof(IRecordingSessionFactory))]
    public void AddOffstream_RegistersTheShell(Type serviceType) =>
        Assert.Contains(Build(), descriptor => descriptor.ServiceType == serviceType);

    /// <summary>
    /// The session belongs to the app, not to the page that started it. A transient controller
    /// would give the Record page a different one every time it was rebuilt, so a recording
    /// started before switching tabs would become unstoppable.
    /// </summary>
    [Fact]
    public void AddOffstream_KeepsOneRecordingController()
    {
        var descriptor = Assert.Single(Build(), item => item.ServiceType == typeof(RecordingController));

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    /// <summary>
    /// Pages are cached by the navigation (<c>NavigationCacheMode.Enabled</c>), so a transient
    /// registration would hand out a second instance the cache never shows — a half-filled
    /// settings form that silently stops being the one on screen.
    /// </summary>
    [Fact]
    public void AddOffstream_ScopesPagesAndViewModelsAsSingletons()
    {
        var services = Build();

        Type[] cached =
        [
            typeof(RecordPage), typeof(SettingsPage), typeof(AdvancedPage),
            typeof(RecordViewModel), typeof(SettingsViewModel), typeof(AdvancedViewModel),
        ];

        foreach (var type in cached)
        {
            var descriptor = Assert.Single(services, item => item.ServiceType == type);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }
    }

    /// <summary>
    /// The activity log has to show lines written during startup, which happens before the host
    /// is built — so the sink is passed in, not constructed by the container.
    /// </summary>
    [Fact]
    public void AddOffstream_SharesTheSinkItWasGiven()
    {
        var sink = new InMemoryLogSink();
        var services = new ServiceCollection().AddOffstream(Configuration(), sink);

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(InMemoryLogSink));
        Assert.Same(sink, descriptor.ImplementationInstance);
    }

    /// <summary>
    /// Spotify registration is gated on a configured Client ID. Nothing signs in yet (PR 3), and
    /// an unconfigured build must not register a client that would throw the moment it is used.
    /// </summary>
    [Fact]
    public void AddOffstream_WithoutAClientId_SkipsSpotify()
    {
        var services = Build();

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(Offstream.Core.Spotify.Auth.SpotifyAuthOptions));
    }

    [Fact]
    public void AddOffstream_WithAClientId_RegistersSpotify()
    {
        var services = new ServiceCollection()
            .AddOffstream(Configuration(("Spotify:ClientId", "0123456789abcdef")), new InMemoryLogSink());

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(Offstream.Core.Spotify.Auth.SpotifyAuthOptions));
    }

    private static IServiceCollection Build() =>
        new ServiceCollection().AddOffstream(Configuration(), new InMemoryLogSink());

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();
}
