using System.Globalization;
using System.IO;
using Offstream.App.Resources;
using Offstream.Core.Audio;
using Offstream.Core.Diagnostics;
using Offstream.Core.Encoding;
using Offstream.Core.Recording;
using Offstream.Core.Settings;
using Serilog;

namespace Offstream.App.Services;

/// <summary>
/// Owns the recording session's lifetime, so the ViewModel does not have to.
/// </summary>
/// <remarks>
/// <para>
/// <b>A session is built per start, never reused.</b> <see cref="RecordingSession.StopAsync"/>
/// disposes the poller it owns, so a stopped session cannot be started again — and settings read
/// at start are a snapshot, which is what makes "change a setting, then restart recording" mean
/// something.
/// </para>
/// <para>
/// <b>Starting reports why it failed instead of throwing at the ViewModel.</b> The three ways it
/// realistically fails — no ffmpeg, no audio endpoint, unreadable settings — are all things the
/// user can fix, and all belong in the activity log next to everything else rather than behind a
/// dialog.
/// </para>
/// </remarks>
public sealed class RecordingController(IRecordingSessionFactory factory, SettingsDocument settings)
    : IAsyncDisposable
{
    private readonly IRecordingSessionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    private readonly SettingsDocument _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>
    /// Serialises start against stop.
    /// </summary>
    /// <remarks>
    /// Both are async and both are reachable from one button, so without this a double-click
    /// during the seconds <see cref="StopAsync"/> spends draining the encode queue can start a
    /// second session over the first one's capture device.
    /// </remarks>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private RecordingSession? _session;
    private bool _disposed;

    /// <summary>Progress from the running session, forwarded verbatim.</summary>
    public event EventHandler<RecordingProgress>? Progress;

    /// <summary>Raised whenever <see cref="IsRunning"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>A finished file landed in the library.</summary>
    public event EventHandler<TrackSavedEventArgs>? TrackSaved;

    /// <summary>The current track's metadata lookup came back, art and destination with it.</summary>
    public event EventHandler<TrackEnrichedEventArgs>? TrackEnriched;

    /// <summary>Whether a session is running.</summary>
    public bool IsRunning => _session?.IsRunning == true;

    /// <summary>The running session's level meter, or null when nothing is running.</summary>
    /// <remarks>
    /// Read by whatever draws the meter, at its own rate — see <see cref="AudioLevelMeter"/> for
    /// why the level is pulled rather than pushed.
    /// </remarks>
    public AudioLevelMeter? Level => _session?.Level;

    /// <summary>
    /// What the output is, as the display prints it — <c>MP3 320K 48K</c>.
    /// </summary>
    /// <remarks>
    /// Read from settings rather than remembered, so an idle page shows what pressing Start would
    /// produce and a running one shows what it is producing. The sample rate only appears once a
    /// session exists: it is the capture endpoint's rate, which is not knowable until the endpoint
    /// is open, and printing a guess on the one line of the page that claims to describe the file
    /// would be worse than printing nothing. Lossless formats omit the bitrate for the same
    /// reason — the setting exists but does not apply.
    /// </remarks>
    public string FormatSummary
    {
        get
        {
            var recording = _settings.Current.ToRecordingSettings();
            var profile = EncodingProfiles.For(recording.MediaFormat);

            var parts = new List<string>(3) { profile.Extension.ToUpperInvariant() };

            if (profile.SupportsBitrate)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{recording.BitrateKbps}K"));
            }

            if (_session?.Level.Format.SampleRate is { } hertz)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{hertz / 1000d:0.#}K"));
            }

            return string.Join(' ', parts);
        }
    }

    /// <summary>The library root, so paths can be shown relative to it rather than in full.</summary>
    public string? OutputPath => _settings.Current.Output.Path;

    /// <summary>Starts recording.</summary>
    /// <returns>Null when a session started; otherwise why one did not.</returns>
    public async Task<string?> StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync();

        try
        {
            if (_session is not null) return null;

            // Re-read rather than trust the copy the window opened with: a file corrected since
            // startup should work without a restart, and the counter this session increments has
            // to be written back onto what is on disk now.
            var settings = _settings.Reload();
            var problem = _settings.LoadProblem;

            if (problem is not null)
            {
                // Recording on defaults would write to a path the user did not choose. Refusing
                // is the other half of "an unreadable settings file does not stop the app from
                // opening" — it opens, and it says what is wrong before it touches the disk.
                Log.Warning("Not starting: settings could not be read. {Problem}", problem);
                return Strings.RecordCannotStartSettings;
            }

            _session = await BuildAsync(settings);
        }
        catch (FFmpegNotFoundException ex)
        {
            Log.Error(ex, "Not starting: ffmpeg is unavailable.");
            return Strings.RecordCannotStartFfmpeg;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            // Most often a render endpoint that has gone away since the settings page listed it:
            // headphones unplugged between choosing the device and pressing record.
            Log.Error(ex, "Not starting: the audio endpoint could not be opened.");
            return Strings.RecordCannotStartAudioDevice;
        }
        finally
        {
            _gate.Release();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);

        return null;
    }

    /// <summary>Finishes the current track, drains the encode backlog, and stops.</summary>
    /// <remarks>
    /// Slow on purpose — a queued encode is a file the user is waiting for.
    /// </remarks>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();

        var session = _session;
        _session = null;

        try
        {
            if (session is null) return;

            await session.StopAsync();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Log.Warning(ex, "The session did not stop cleanly.");
        }
        finally
        {
            if (session is not null)
            {
                CaptureRuntimeState(session);
                await Release(session);
            }

            _gate.Release();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();

        _gate.Dispose();
    }

    /// <summary>
    /// Builds a session, subscribes to it, and starts it — disposing it if starting fails.
    /// </summary>
    /// <remarks>
    /// <see cref="RecordingSession"/> opens the capture device in its constructor, so a session
    /// that throws out of <see cref="RecordingSession.Start"/> is still holding a WASAPI client.
    /// Leaking one means the next start finds the endpoint busy and fails for a reason that has
    /// nothing to do with why the first one failed.
    /// </remarks>
    private async Task<RecordingSession> BuildAsync(OffstreamSettings settings)
    {
        var session = _factory.Create(settings, new Progress<RecordingProgress>(OnProgress));

        session.TrackSaved += OnTrackSaved;
        session.TrackEnriched += OnTrackEnriched;
        session.Failed += OnFailed;

        try
        {
            session.Start();
        }
        catch
        {
            await Release(session);
            throw;
        }

        return session;
    }

    /// <summary>
    /// Writes back the one thing a session changes while it runs: the file counter.
    /// </summary>
    /// <remarks>
    /// <see cref="RecordingSession"/> increments it per saved recording, on its own working copy
    /// of the settings. Without this the increments died with the session, so the next run
    /// restarted numbering at 1 — every night's recordings landing on the same names as the
    /// previous night's, and the "already recorded?" check answering for the wrong file.
    /// </remarks>
    private void CaptureRuntimeState(RecordingSession session)
    {
        var problem = _settings.Update(current => current.CaptureRuntimeState(session.Settings));

        if (problem is not null) Log.Warning("The file counter could not be saved: {Problem}", problem);
    }

    private async ValueTask Release(RecordingSession session)
    {
        session.TrackSaved -= OnTrackSaved;
        session.TrackEnriched -= OnTrackEnriched;
        session.Failed -= OnFailed;

        await session.DisposeAsync();
    }

    /// <summary>Forwards progress, and logs the part of it worth reading.</summary>
    /// <remarks>
    /// Progress arrives on every poll — around fourteen times a second — because that is what
    /// carries the elapsed counter. Only reports with a message are worth a log line; logging the
    /// rest would bury the session in ticks.
    /// </remarks>
    private void OnProgress(RecordingProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.Message)) Log.Information("{Message}", progress.Message);

        Progress?.Invoke(this, progress);
    }

    private void OnTrackSaved(object? sender, TrackSavedEventArgs e) => TrackSaved?.Invoke(this, e);

    private void OnTrackEnriched(object? sender, TrackEnrichedEventArgs e) => TrackEnriched?.Invoke(this, e);

    private static void OnFailed(object? sender, RecordingFailedEventArgs e) =>
        Log.Error(e.Exception, "{Message}", e.Message);
}
