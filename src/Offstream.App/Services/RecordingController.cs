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
public sealed class RecordingController : IAsyncDisposable
{
    private readonly IRecordingSessionFactory _factory;

    private readonly SettingsDocument _settings;

    public RecordingController(IRecordingSessionFactory factory, SettingsDocument settings)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // Everything on the format line is read from settings, so everything on it goes stale the
        // moment a setting changes. Nothing else was telling the page: it re-read on start and on
        // stop, which meant changing the format or the capture device while idle left the line
        // describing the file the previous settings would have produced.
        _settings.Changed += OnSettingsChanged;
    }

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

    /// <summary>Raised when <see cref="FormatSummary"/> may have changed under the page.</summary>
    /// <remarks>
    /// Separate from <see cref="StateChanged"/>, which promises to mean "IsRunning changed" and
    /// would stop meaning it if settings borrowed it.
    /// </remarks>
    public event EventHandler? OutputChanged;

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
    /// <para>
    /// Read from settings rather than remembered, so an idle page shows what pressing Start would
    /// produce and a running one shows what it is producing. Lossless formats omit the bitrate
    /// — the setting exists but does not apply.
    /// </para>
    /// <para>
    /// <b>The sample rate is shown while idle too</b> (2026-09-02). It used to appear only once a
    /// session existed, on the grounds that the capture endpoint's rate is not knowable until the
    /// endpoint is open. That is wrong: the rate is a property of the endpoint, and
    /// <see cref="NAudio.CoreAudioApi.MMDevice"/> reports its mix format at any time. Withholding
    /// it made one third of this line behave unlike the other two, which read from settings and
    /// are always there — so the line grew a word on Start for no reason the user could see.
    /// </para>
    /// <para>
    /// It is the same endpoint capture will open — <see cref="Offstream.Core.Audio.LoopbackAudioCapture"/>
    /// resolves the identical id through <see cref="AudioEndpoints.Resolve"/> and takes its format
    /// from the device — so the idle figure is the one the recording will use, not a guess. A
    /// device that cannot be read prints nothing rather than a placeholder: this line describes
    /// the file, and a wrong number on it is worse than a short one.
    /// </para>
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

            if (SampleRateHertz() is { } hertz)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{hertz / 1000d:0.#}K"));
            }

            return string.Join(' ', parts);
        }
    }

    /// <summary>
    /// The rate audio is being captured at, or would be captured at from a standing start.
    /// </summary>
    /// <remarks>
    /// A running session already knows, and is asked first: its format came from the device when
    /// the capture opened, and re-reading the endpoint could disagree with the file being written
    /// if the default endpoint moved underneath us. Idle, the endpoint is asked directly.
    /// </remarks>
    private int? SampleRateHertz()
    {
        if (_session?.Level.Format.SampleRate is { } running) return running;

        try
        {
            using var device = AudioEndpoints.Resolve(_settings.Current.Recording.AudioEndpointDeviceId);
            return device.AudioClient.MixFormat.SampleRate;
        }
        catch (Exception ex)
        {
            // Nothing here is worth interrupting anyone over: the line simply comes up one word
            // short, and Start reports a missing endpoint properly when it matters.
            Log.Debug(ex, "Could not read the capture endpoint's sample rate for the format line");
            return null;
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

        _settings.Changed -= OnSettingsChanged;

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
        session.Ended += OnSessionEnded;

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
        session.Ended -= OnSessionEnded;

        await session.DisposeAsync();
    }

    /// <summary>A setting was saved, so <see cref="FormatSummary"/> may no longer be current.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e) =>
        OutputChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Releases a session that stopped by itself — the recording timer elapsed, or the audio
    /// endpoint went away mid-recording.
    /// </summary>
    /// <remarks>
    /// The same teardown <see cref="StopAsync"/> does, and it cannot wait until the user presses
    /// Stop. Until it runs, the page still offers to stop a session that has stopped, the file
    /// counter this run reached is unwritten, and the capture is still open on an endpoint the next
    /// start would like to have.
    /// </remarks>
    private void OnSessionEnded(object? sender, EventArgs e) => _ = ReleaseEndedSessionAsync();

    private async Task ReleaseEndedSessionAsync()
    {
        try
        {
            await _gate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            // The controller was disposed as the session ended; the teardown already happened.
            return;
        }

        try
        {
            var session = _session;

            // Stop was pressed while the session was ending itself: that path has released it.
            if (session is null) return;

            _session = null;

            CaptureRuntimeState(session);
            await Release(session);
        }
        finally
        {
            _gate.Release();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
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
