using System.Globalization;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Offstream.App.Services;
using Offstream.App.ViewModels;
using Offstream.App.Views;
using Offstream.Core;
using Offstream.Core.Diagnostics;
using Offstream.Core.Settings;
using Serilog;

namespace Offstream.App;

/// <summary>
/// Application entry point: builds the generic host, wires logging, shows the shell.
/// </summary>
public partial class App : Application
{
    private readonly IHost _host;

    /// <summary>The in-app log sink, shared with the activity log on the Record page.</summary>
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
                services.AddOffstream(context.Configuration, LogSink))
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        await _host.StartAsync();

        Log.Information("Offstream starting. Settings: {Settings}", OffstreamPaths.SettingsFile);

        var settings = LoadSettings(out var problem);

        // Both of these have to happen before the shell is resolved. The language decides which
        // satellite assembly x:Static reads its strings from, and those are resolved once when
        // the XAML loads; the theme decides what the window looks like on its first frame.
        ApplyLanguage(settings.App.Language);
        ThemeService.Apply(ShellTheme.System);

        var shell = _host.Services.GetRequiredService<ShellWindow>();
        _host.Services.GetRequiredService<ShellViewModel>().StartupWarning = problem;
        shell.Show();
    }

    /// <summary>
    /// Reads settings without letting a bad file stop the app from opening.
    /// </summary>
    /// <remarks>
    /// Plan §6's exit criterion: a corrupted <c>settings.json</c> fails with a clear message
    /// rather than a crash. The message goes to two places on purpose — the log, so it survives
    /// the session, and <see cref="ShellViewModel.StartupWarning"/>, so the user sees it without
    /// going looking.
    /// </remarks>
    private OffstreamSettings LoadSettings(out string? problem)
    {
        var settings = _host.Services.GetRequiredService<SettingsStore>().LoadOrDefault(out problem);

        if (problem is not null)
        {
            Log.Warning("Settings could not be read, starting on defaults: {Problem}", problem);
        }

        return settings;
    }

    /// <summary>
    /// Applies the configured UI language, or leaves the system's in place.
    /// </summary>
    /// <remarks>
    /// A change takes effect on the next launch. Re-reading every string on the fly would mean
    /// routing all of them through a binding that can be invalidated, which buys nothing for a
    /// setting that is touched once — and the predecessor rebuilt its entire form to do it.
    /// </remarks>
    private static void ApplyLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return;

        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // A hand-edited settings file can name a culture that does not exist. Falling back
            // to the system language is strictly better than refusing to start.
            Log.Warning("Ignoring unknown UI language {Language}.", language);
        }
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
