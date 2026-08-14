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
    /// <summary>How long the whole shutdown gets before the process stops waiting for it.</summary>
    /// <remarks>
    /// Generous, because a queued encode is a file the user is waiting for and the backlog is
    /// drained rather than dropped — <see cref="Offstream.Core.Encoding.EncodeBacklog"/>'s own
    /// completion path takes no deadline at all. Finite, because it is the only thing standing
    /// between one wedged ffmpeg and a process with no window, no tray icon, and no way to stop
    /// it short of Task Manager.
    /// </remarks>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    private readonly IHost _host;

    /// <summary>Held for the life of the process; see <see cref="SingleInstance"/>.</summary>
    private SingleInstance? _instance;

    /// <summary>The in-app log sink, shared with the activity log on the Record page.</summary>
    public static InMemoryLogSink LogSink { get; } = new();

    public App()
    {
        OffstreamPaths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()

            // HttpClient's own logging writes four Information lines per request — start,
            // sending, headers received, end — none of which mean anything to the person
            // reading the activity log. During a recording that is most of the traffic on the
            // pane: every line is a dispatcher post, a realised list item and a scroll-to-end
            // on the UI thread, for text like "End processing HTTP request after 501.7018ms".
            // Warnings and failures still come through; the play-by-play does not.
            .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Extensions.Http", Serilog.Events.LogEventLevel.Warning)

            // The generic host announcing itself: "Application started. Press Ctrl+C to shut
            // down.", the hosting environment, and the content root. All three were the first
            // thing in the activity log on every launch, and none of them is Offstream talking —
            // the Ctrl+C line is advice for a console application, which this is not.
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.Sink(LogSink)
            .WriteTo.File(
                OffstreamPaths.LogFile,
                // Log files are diagnostics, not UI: keep them culture-invariant so a
                // French user's log is still readable by whoever is debugging it.
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 7,

                // A daily roll bounds the file count but not the size of any one file, and an
                // overnight session is exactly the case that produces a large one. Rolling on
                // size as well puts a real ceiling on the directory: seven files of 16 MB.
                // Serilog's default is to stop writing at the size limit rather than roll, which
                // would lose the end of the session — the part worth reading after a failure.
                fileSizeLimitBytes: 16L * 1024 * 1024,
                rollOnFileSizeLimit: true,
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

        // Before anything else touches the settings file: two instances sharing one file both
        // write the whole document, so the second to save silently reverts the first.
        _instance = SingleInstance.TryAcquire(OnActivationRequested);

        if (_instance is null)
        {
            // The running instance has been asked to show itself. Shutdown() rather than
            // returning, or this process would sit there with a host it never started.
            Shutdown();
            return;
        }

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
    /// Shows the window because someone launched Offstream again.
    /// </summary>
    /// <remarks>
    /// Arrives on a thread-pool thread, so it goes through the ViewModel's command rather than
    /// touching the window directly — <see cref="ShellViewModel.Show"/> marshals to the
    /// dispatcher, which is the same path the tray icon's own click takes.
    /// </remarks>
    private void OnActivationRequested()
    {
        Log.Information("Another launch asked for the window.");

        _host.Services.GetRequiredService<ShellViewModel>().Show();
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

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Offstream exiting.");

        // Released before the host stops, so the next launch is never told "already running"
        // by a process that is on its way out.
        _instance?.Dispose();
        _instance = null;

        // Blocking, and on a thread-pool thread rather than this one.
        //
        // This method used to be `async void`, which WPF does not await. It ran as far as its
        // first await, returned, and let Application.Run finish tearing the Dispatcher down —
        // and the continuation holding the rest of the shutdown was posted to a Dispatcher that
        // would never run it again. So on every exit the host was never stopped, the capture
        // device was never closed, the encode backlog was never drained, and the log was never
        // flushed, which is why none of this left a trace to read afterwards. Closing the window
        // mid-recording left all of it running with no window and no tray icon to stop it from.
        //
        // Task.Run is what makes blocking here safe: it keeps the await continuations off the
        // Dispatcher. Waiting on this thread for work that wanted to resume on this thread is
        // the other way to hang an exit, and swapping one hang for another is no fix.
        if (!Task.Run(ShutdownAsync).Wait(ShutdownTimeout))
        {
            Log.Warning(
                "Shutdown did not finish within {Seconds:F0}s. Leaving whatever is still running "
                + "behind rather than staying up with nothing on screen to stop it.",
                ShutdownTimeout.TotalSeconds);

            Log.CloseAndFlush();

            // The guarantee the user is owed: closing Offstream ends Offstream. Everything worth
            // saving has either finished or had its chance by now, and the alternative - the one
            // that prompted this - is a process only Task Manager can reach.
            Environment.Exit(0);
        }

        Log.CloseAndFlush();

        base.OnExit(e);
    }

    /// <summary>Stops the host and disposes everything the container owns.</summary>
    private async Task ShutdownAsync()
    {
        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));

            // DisposeAsync rather than the Dispose the `using` used to call. RecordingController
            // is a singleton implementing IAsyncDisposable *only*, and the container refuses to
            // dispose such a service synchronously — ServiceProvider.Dispose() throws
            // InvalidOperationException rather than falling back to a blocking wait. The
            // controller is what owns the running session, so the sync path threw at precisely
            // the moment the recording most needed stopping.
            if (_host is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
            else
            {
                _host.Dispose();
            }
        }
#pragma warning disable CA1031 // Nothing here is worth leaving the process up for.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Error(ex, "Offstream did not shut down cleanly.");
        }
    }
}
