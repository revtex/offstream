using System.IO.Abstractions;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Offstream.App.ViewModels;
using Offstream.App.Views;
using Offstream.App.Views.Pages;
using Offstream.Core.Diagnostics;
using Offstream.Core.Interop;
using Offstream.Core.Settings;
using Offstream.Core.Spotify.Auth;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using Wpf.Ui;
using Wpf.Ui.Abstractions;

namespace Offstream.App.Services;

/// <summary>
/// Everything the shell resolves, in one place.
/// </summary>
/// <remarks>
/// Deliberately not inline in <see cref="App"/>. Registration is the part of the composition
/// root that can actually be wrong — a page reachable from the navigation but never registered
/// throws only when the user clicks that tab — and a static method taking an
/// <see cref="IServiceCollection"/> is something a test can call without a message loop, a
/// window, or an STA thread. <c>AppServicesTests</c> does exactly that.
/// </remarks>
public static class AppServices
{
    /// <summary>Registers the shell's views, ViewModels and services.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="configuration">Host configuration, read for the Spotify Client ID.</param>
    /// <param name="logSink">
    /// The in-memory sink Serilog writes to, shared with the activity log. Passed in rather
    /// than constructed here because <see cref="App"/> installs it into the logger before the
    /// host exists, and the pane must show lines logged during startup.
    /// </param>
    public static IServiceCollection AddOffstream(
        this IServiceCollection services,
        IConfiguration configuration,
        InMemoryLogSink logSink)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logSink);

        services.AddSingleton(logSink);

        // Settings (plan §6). The shell reads these at startup through LoadOrDefault, which
        // puts a window on screen even when the file is unusable.
        services.AddSingleton<IFileSystem, FileSystem>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<SettingsStore>();

        // One working copy of the settings, shared by the two pages that edit them. Two
        // documents would each save the whole file, and whichever tab was touched second would
        // silently revert the other.
        services.AddSingleton<SettingsDocument>();
        services.AddSingleton<IAudioDeviceCatalog, AudioDeviceCatalog>();
        services.AddSingleton<IFolderPicker, FolderPicker>();

        // Navigation. NavigationService takes the page provider, so the container is what
        // builds every page - see PageProvider for why that matters.
        services.AddSingleton<INavigationViewPageProvider, PageProvider>();
        services.AddSingleton<INavigationService, NavigationService>();

        services.AddSingleton<ShellWindow>();
        services.AddSingleton<ShellViewModel>();

        // Recording (plan §§3-4). The controller is a singleton because the session it owns is
        // the app's - starting one from a page and finding it gone when the tab is switched away
        // would be a recording lost to navigation.
        services.AddSingleton<IRecordingSessionFactory, RecordingSessionFactory>();
        services.AddSingleton<IProcessManager, ProcessManager>();
        services.AddSingleton<RecordingController>();

        // Pages are singletons to match NavigationCacheMode.Enabled on the navigation items:
        // switching tabs must not discard the activity log or a half-filled settings form.
        services.AddSingleton<RecordPage>();
        services.AddSingleton<RecordViewModel>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AdvancedPage>();
        services.AddSingleton<AdvancedViewModel>();

        AddSpotify(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers the Spotify auth pieces from <see cref="Offstream.Core.Spotify.Auth"/>, when a
    /// Client ID is configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing resolves <see cref="SpotifyAuthenticator"/> from the container yet. The Settings
    /// page offers the Client ID field, but not the browser sign-in: no recording path consumes
    /// <c>SpotifyMetadataProvider</c> yet, so a refresh token obtained here would be read by
    /// nothing. This is the infrastructure that sign-in builds on — an
    /// <see cref="IHttpClientFactory"/>-routed <see cref="ISpotifyOAuthClient"/> instead of the
    /// SDK's own bare <c>new HttpClient()</c>, and the options-pattern binding plan §10 Phase 4
    /// asks for. <c>tools/Offstream.SpotifyAuthProbe</c> is what actually exercises the PKCE
    /// flow end to end today.
    /// </para>
    /// <para>
    /// The Client ID still comes from configuration — <c>appsettings.json</c>, user secrets or
    /// the <c>Spotify__ClientId</c> environment variable — rather than from
    /// <see cref="MetadataSettings.SpotifyClientId"/>. Reading the settings value instead would
    /// mean these registrations had to become unconditional and re-read after every edit, which
    /// is work for the sign-in change to do rather than this one.
    /// </para>
    /// </remarks>
    private static void AddSpotify(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient();

        var clientId = configuration["Spotify:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId)) return;

        services.AddSingleton(new SpotifyAuthOptions { ClientId = clientId });
        services.AddSingleton<IBrowserLauncher, BrowserLauncher>();

        services.AddTransient<ISpotifyOAuthClient>(provider =>
        {
            var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(Offstream));
            var config = SpotifyClientConfig.CreateDefault().WithHTTPClient(new NetHttpClient(httpClient));

            return new SpotifyOAuthClient(config);
        });

        services.AddTransient<SpotifyPkceFlow>();

        services.AddTransient(provider => new SpotifyAuthenticator(
            provider.GetRequiredService<SpotifyAuthOptions>(),
            provider.GetRequiredService<SpotifyPkceFlow>(),
            provider.GetRequiredService<IBrowserLauncher>(),
            redirectUri => new SpotifyLoopbackListener(redirectUri)));
    }
}
