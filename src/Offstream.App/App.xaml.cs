using System.Globalization;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Offstream.App.ViewModels;
using Offstream.App.Views;
using Offstream.Core;
using Offstream.Core.Diagnostics;
using Offstream.Core.Spotify.Auth;
using Serilog;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;

namespace Offstream.App;

/// <summary>
/// Application entry point: builds the generic host, wires logging, shows the shell.
/// </summary>
public partial class App : Application
{
    private readonly IHost _host;

    /// <summary>The in-app log sink, shared with the console pane.</summary>
    public static InMemoryLogSink LogSink { get; } = new();

    public App()
    {
        OffstreamPaths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(LogSink)
            .WriteTo.File(
                OffstreamPaths.LogFile,
                // Log files are diagnostics, not UI: keep them culture-invariant so a
                // French user's log is still readable by whoever is debugging it.
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateLogger();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(LogSink);
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<ShellWindow>();

                ConfigureSpotify(services, context.Configuration);
            })
            .Build();
    }

    /// <summary>
    /// Registers the Spotify auth pieces from <see cref="Offstream.Core.Spotify.Auth"/>, when a
    /// Client ID is configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing resolves <see cref="SpotifyAuthenticator"/> from the container yet — there is no
    /// settings UI to type a Client ID into (Phase 5) and no shell screen to trigger a sign-in
    /// from (Phase 6). This is the infrastructure those phases build on: an
    /// <see cref="IHttpClientFactory"/>-routed <see cref="ISpotifyOAuthClient"/> instead of the
    /// SDK's own bare <c>new HttpClient()</c>, and the options-pattern binding plan §10 Phase 4
    /// asks for. <c>tools/Offstream.SpotifyAuthProbe</c> is what actually exercises the PKCE
    /// flow end to end today.
    /// </para>
    /// <para>
    /// The Client ID itself comes from configuration — <c>appsettings.json</c>, user secrets or
    /// the <c>Spotify__ClientId</c> environment variable, all of which
    /// <see cref="Host.CreateDefaultBuilder(string[])"/> already wires up — because there is
    /// nowhere else for it to live before Phase 5 gives Offstream real settings persistence.
    /// </para>
    /// </remarks>
    private static void ConfigureSpotify(IServiceCollection services, IConfiguration configuration)
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        await _host.StartAsync();

        Log.Information("Offstream starting. Settings: {Settings}", OffstreamPaths.SettingsFile);

        _host.Services.GetRequiredService<ShellWindow>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("Offstream exiting.");

        using (_host)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }

        await Log.CloseAndFlushAsync();

        base.OnExit(e);
    }
}
