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

        services.AddSingleton<ShellWindow>();
        services.AddSingleton<ShellViewModel>();

        // Recording (plan §§3-4). The controller is a singleton because the session it owns is
        // the app's - starting one from a page and finding it gone when the tab is switched away
        // would be a recording lost to navigation.
        services.AddSingleton<IRecordingSessionFactory, RecordingSessionFactory>();
        services.AddSingleton<ISpotifyAccount, SpotifyAccount>();
        services.AddSingleton<IProcessManager, ProcessManager>();
        services.AddSingleton<RecordingController>();
        services.AddSingleton<ReadinessProbe>();

        // Pages are singletons because the shell keeps all three loaded and switches them by
        // visibility: switching tabs must not discard the activity log or a half-filled form.
        services.AddSingleton<RecordPage>();
        services.AddSingleton<RecordViewModel>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AdvancedPage>();
        services.AddSingleton<AdvancedViewModel>();

        AddSpotify(services, configuration);

        return services;
    }

    /// <summary>Registers the HTTP clients the metadata layer runs on.</summary>
    /// <remarks>
    /// <para>
    /// <b>The Spotify auth objects are deliberately not registered here.</b> They were, keyed off
    /// a <c>Spotify:ClientId</c> configuration value, back when nothing consumed them. They have
    /// a consumer now — <see cref="ISpotifyAccount"/> — and it builds them per call, because the
    /// Client ID is the user's own and lives in <see cref="MetadataSettings.SpotifyClientId"/>,
    /// where it changes without an app restart. A singleton
    /// <see cref="Offstream.Core.Spotify.Auth.SpotifyAuthOptions"/> captured at startup would
    /// sign the user in with whatever Client ID was on disk when the window opened.
    /// </para>
    /// <para>
    /// <paramref name="configuration"/> is still taken because
    /// <c>tools/Offstream.SpotifyAuthProbe</c> and the appsettings file document the same key,
    /// and because host configuration is where an override would go if one is ever wanted.
    /// </para>
    /// </remarks>
    private static void AddSpotify(IServiceCollection services, IConfiguration configuration)
    {
        // The SDK's token requests share this one, so they use the app's handler pool rather than
        // the bare `new HttpClient()` SpotifyAPI.Web would otherwise construct per client.
        services.AddHttpClient();

        // Metadata lookups and cover-art fetches get a client of their own, with a short timeout:
        // the default hundred seconds is far longer than a finished recording should ever wait on
        // a provider that has stopped answering.
        services.AddHttpClient(
            RecordingSessionFactory.MetadataHttpClient,
            client =>
            {
                client.Timeout = RecordingSessionFactory.MetadataRequestTimeout;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Offstream/1.0");
            });
    }
}
