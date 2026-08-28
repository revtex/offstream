using System.IO.Abstractions;
using Offstream.Core.Audio;
using Offstream.Core.Diagnostics;
using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Offstream.Core.Naming;
using Offstream.Core.Settings;
using Offstream.Core.Spotify;
using Serilog;

namespace Offstream.Core.Recording;

/// <summary>A track finished recording, whether or not it produced a file.</summary>
public sealed class TrackRecordedEventArgs(TrackRecording recording) : EventArgs
{
    public TrackRecording Recording { get; } = recording;

    public Track Track => Recording.Track;

    public RecordingOutcome Outcome => Recording.Outcome;
}

/// <summary>A recording reached its final path in the user's library.</summary>
public sealed class TrackSavedEventArgs(Track track, string path, TimeSpan duration) : EventArgs
{
    public Track Track { get; } = track;

    /// <summary>Where the finished file now lives.</summary>
    public string Path { get; } = path;

    public TimeSpan Duration { get; } = duration;
}

/// <summary>
/// The metadata lookup for the track being recorded has landed.
/// </summary>
/// <remarks>
/// Raised roughly a second into a track, not at the end of one: everything here — album, year,
/// cover art, and therefore the destination the template renders to — is unknown when the track
/// starts and known long before it finishes. A page that waits for the file to be written has
/// nothing to show for the three minutes in between.
/// </remarks>
public sealed class TrackEnrichedEventArgs(Track track, string? coverArtPath, string? destination) : EventArgs
{
    public Track Track { get; } = track;

    /// <summary>
    /// A temporary image file, or null when there is no art.
    /// </summary>
    /// <remarks>
    /// <b>Borrowed, not owned.</b> It is deleted once the encode has finished with it, which can
    /// be moments after this is raised — a handler that wants the image must read it now rather
    /// than keep the path.
    /// </remarks>
    public string? CoverArtPath { get; } = coverArtPath;

    /// <summary>Where this recording is currently expected to land, or null if that cannot be rendered.</summary>
    public string? Destination { get; } = destination;
}

/// <summary>A track was recorded but could not be turned into a file.</summary>
public sealed class RecordingFailedEventArgs(Track track, string message, Exception? exception = null) : EventArgs
{
    public Track Track { get; } = track;

    public string Message { get; } = message;

    public Exception? Exception { get; } = exception;
}

/// <summary>
/// Joins the pieces: capture feeds a buffer, Spotify polling decides when a track starts and
/// ends, a recorder writes each one, and finished recordings queue for encoding.
/// </summary>
/// <remarks>
/// <para>
/// This is the reference implementation's <c>Watcher</c>, with everything it should never have
/// contained taken out. That class held the recording rules, the audio-session routing, the
/// sleep-prevention calls, the timers, a static <c>Running</c> flag, and direct calls into the
/// WinForms form; a test for "should this track be recorded?" had to construct all of it. The
/// rules now live in <see cref="RecordingPolicy"/>, progress leaves through
/// <see cref="IProgress{T}"/> and events, and what remains here is only the joining-up.
/// </para>
/// <para>
/// <b>The destination name is claimed after encoding, not before.</b> The reference encoded
/// inline, so choosing a name and renaming onto it happened microseconds apart. Encoding is now
/// queued behind other tracks, and a name reserved minutes early can be taken in the meantime
/// by a recording that finishes first — so the encode goes to a temp file and the library name
/// is resolved at the moment of the rename.
/// </para>
/// <para>
/// <b>A failed encode keeps its WAV.</b> The captured audio is the irreplaceable part; ffmpeg
/// missing or failing is recoverable, and deleting the source in that case is how a night of
/// recording turns into nothing. The path is reported so the user can find it.
/// </para>
/// </remarks>
public sealed class RecordingSession : IAsyncDisposable
{
    private readonly IAudioCaptureSource _capture;
    private readonly SpotifyPoller _poller;
    private readonly RecordingSettings _settings;
    private readonly RecordingPolicy _policy;
    private readonly IFileSystem _fileSystem;
    private readonly ITrackEnricher? _enricher;
    private readonly EncodeBacklog _backlog;
    private readonly IProgress<RecordingProgress>? _progress;
    private readonly TimeProvider _time;

    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();

    /// <summary>Maps a queued encode back to the recording it came from, for the rename.</summary>
    private readonly Dictionary<string, TrackRecording> _pending = new(StringComparer.OrdinalIgnoreCase);

    private AudioCaptureBuffer? _buffer;
    private TrackRecorder? _recorder;
    private Task<TrackRecording>? _recording;
    private ITimer? _recordingTimer;
    private bool _stopAfterCurrentTrack;
    private bool _disposed;
    private int _captureLost;

    /// <param name="capture">
    /// The audio source feeding the shared buffer. Owned by the session and disposed with it —
    /// see <see cref="DisposeAsync"/> for why that has to be deterministic.
    /// </param>
    /// <param name="poller">Where track changes come from.</param>
    /// <param name="settings">The session's settings, including the counter it increments.</param>
    /// <param name="encoder">What the encode backlog runs.</param>
    /// <param name="fileSystem">Injected so naming and file moves are testable.</param>
    /// <param name="enricher">
    /// Looks each track up with the configured metadata provider. Null records exactly what the
    /// window title says — an artist and a title — which is all Offstream knew before this
    /// existed.
    /// </param>
    /// <param name="progress">Where stage changes are reported.</param>
    /// <param name="timeProvider">Injected for the recording timer and for dated folder names.</param>
    public RecordingSession(
        IAudioCaptureSource capture,
        SpotifyPoller poller,
        RecordingSettings settings,
        IAudioEncoder encoder,
        IFileSystem fileSystem,
        ITrackEnricher? enricher = null,
        IProgress<RecordingProgress>? progress = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(poller);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _capture = capture;
        _poller = poller;
        _settings = settings;
        _policy = new RecordingPolicy(settings);
        _fileSystem = fileSystem;
        _enricher = enricher;
        _progress = progress;
        _time = timeProvider ?? TimeProvider.System;

        _backlog = new EncodeBacklog(encoder);
        _backlog.Completed += OnEncodeCompleted;
        _backlog.Failed += OnEncodeFailed;

        Level = new AudioLevelMeter(capture.Format);
    }

    /// <summary>
    /// Live capture level, for a meter. Drained by whoever draws it, at its own rate.
    /// </summary>
    /// <remarks>
    /// Fed from the same callback that feeds the recording buffer, so it measures what is
    /// actually being captured rather than what the system volume is set to — the distinction
    /// that matters when Spotify is playing to a device this session is not recording.
    /// </remarks>
    public AudioLevelMeter Level { get; }

    /// <summary>A track finished recording — including the ones that produced nothing.</summary>
    public event EventHandler<TrackRecordedEventArgs>? TrackRecorded;

    /// <summary>A finished file landed in the library.</summary>
    public event EventHandler<TrackSavedEventArgs>? TrackSaved;

    /// <summary>The metadata lookup for the current track finished. See <see cref="TrackEnrichedEventArgs"/>.</summary>
    public event EventHandler<TrackEnrichedEventArgs>? TrackEnriched;

    /// <summary>A recording was lost, or could not be encoded.</summary>
    public event EventHandler<RecordingFailedEventArgs>? Failed;

    /// <summary>
    /// The session stopped without being asked to, and has finished stopping.
    /// </summary>
    /// <remarks>
    /// Two things end a session from the inside: the recording timer elapsing, and the audio
    /// endpoint going away mid-recording. Whoever owns the session has to hear about both, because
    /// a session that stops by itself is still held, still counted as running by anything that asks
    /// its owner, and still holding the file counter this run reached. Without this the Record page
    /// went on offering Stop for a session that had already stopped, and the capture stayed open on
    /// an endpoint the next start wanted.
    /// </remarks>
    public event EventHandler? Ended;

    /// <summary>Whether the session is running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// The settings this session is running on — the live object, not a copy.
    /// </summary>
    /// <remarks>
    /// Exposed for one reason: <see cref="RecordingSettings.InternalOrderNumber"/> is incremented
    /// here as files are saved, and something has to write it back when the session ends or the
    /// counter restarts from 1 on the next run. See <see cref="Settings.OffstreamSettings.CaptureRuntimeState"/>.
    /// </remarks>
    public RecordingSettings Settings => _settings;

    /// <summary>The track being recorded right now, if any.</summary>
    public Track? CurrentTrack
    {
        get
        {
            lock (_gate) return _recorder?.Track;
        }
    }

    /// <summary>Recordings queued for encoding, including the one in flight.</summary>
    public int PendingEncodes => _backlog.PendingCount;

    /// <summary>Starts capture, polling and the encode backlog.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning) return;

        IsRunning = true;

        _capture.StartCapture();
        _buffer = new AudioCaptureBuffer(_capture.Format);
        _capture.DataAvailable += OnAudioAvailable;
        _capture.Stopped += OnCaptureStopped;

        _backlog.Start();

        _poller.TrackChanged += OnTrackChanged;
        _poller.PlayStateChanged += OnPlayStateChanged;
        _poller.TrackTimeChanged += OnTrackTimeChanged;
        _poller.Start();

        StartRecordingTimer();

        Report(RecordingStage.WaitingForTrack, message: "Listening for Spotify.");
    }

    /// <summary>
    /// Finishes the current track, drains the encode backlog, and stops.
    /// </summary>
    /// <remarks>
    /// The current recording is <em>completed</em> rather than abandoned — a user pressing stop
    /// mid-song still wants the part that has played, subject to the minimum-length rule — and
    /// the queued encodes are drained rather than dropped.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return;

        IsRunning = false;

        _poller.TrackChanged -= OnTrackChanged;
        _poller.PlayStateChanged -= OnPlayStateChanged;
        _poller.TrackTimeChanged -= OnTrackTimeChanged;
        await _poller.DisposeAsync();

        await FinishCurrentTrackAsync();

        _capture.DataAvailable -= OnAudioAvailable;
        _capture.Stopped -= OnCaptureStopped;
        _capture.StopCapture();

        // The meter is drained by the UI, which stops asking once the session stops. Clearing it
        // means the display settles at silence instead of freezing on the last peak.
        Level.Reset();

        StopRecordingTimer();

        await _backlog.CompleteAsync(cancellationToken);

        Report(RecordingStage.Stopped, message: "Stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRunning)
        {
            try
            {
                await StopAsync();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Shutting down anyway; a failure here must not mask the caller's own.
            }
        }

        await _stopping.CancelAsync();

        _backlog.Completed -= OnEncodeCompleted;
        _backlog.Failed -= OnEncodeFailed;
        await _backlog.DisposeAsync();

        StopRecordingTimer();

        lock (_gate)
        {
            _recorder?.Dispose();
            _recorder = null;
        }

        // The capture is this session's to close, and closing it is not housekeeping. What it
        // holds is a WASAPI client on the endpoint and an endpoint-notification callback
        // registered with the audio service — and Windows keeps calling that callback until it is
        // unregistered. Left to the garbage collector, the callback outlives the object it calls
        // into, and the next start-and-stop cycle ends with the audio service invoking freed
        // interop memory: an 0xc0000005 in the event log, no managed exception, no log line.
        // Sessions are built per start and never reused, so nothing else can be holding this.
        _capture.DataAvailable -= OnAudioAvailable;
        _capture.Stopped -= OnCaptureStopped;
        _capture.Dispose();

        _stopping.Dispose();
    }

    private void OnAudioAvailable(object? sender, AudioDataEventArgs e)
    {
        _buffer?.Write(e.Data);
        Level.Write(e.Data);
    }

    /// <summary>
    /// The capture ended without being asked to: the endpoint was unplugged or disabled, or
    /// Windows moved the default somewhere else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The track in flight is finished rather than abandoned. Everything captured up to the moment
    /// the endpoint went is already in the buffer and is as much of that track as will ever exist,
    /// so it is saved on the same terms as any other stop — including the minimum-length rule that
    /// may still discard it. What cannot continue is the session: the stream it was reading has
    /// gone, and whatever replaces the endpoint can have a different sample rate and channel count.
    /// </para>
    /// <para>
    /// This is the other half of <see cref="Audio.AudioEndpointWatcher"/>, which detected the loss
    /// from the start and had nobody listening: the capture stopped, and the session went on
    /// believing it was recording until the user noticed the elapsed counter had frozen.
    /// </para>
    /// </remarks>
    private void OnCaptureStopped(object? sender, CaptureStoppedEventArgs e)
    {
        if (!IsRunning) return;

        // The capture reports its own ending once, but a stop the session asked for arrives here
        // too if it lands before the unsubscribe in StopAsync does.
        if (Interlocked.Exchange(ref _captureLost, 1) != 0) return;

        var reason = e.Exception?.Message ?? "The audio capture stopped unexpectedly.";

        RaiseFailed(CurrentTrack ?? new Track(), $"{reason} Recording has stopped.", e.Exception);

        StopItself();
    }

    /// <summary>
    /// Ends the session from inside it, off whatever thread noticed.
    /// </summary>
    /// <remarks>
    /// <see cref="StopAsync"/> finishes the current track and drains the encode backlog — seconds
    /// of work and disk I/O, on a thread that is either the poll loop or an audio callback.
    /// <see cref="Ended"/> is raised after it rather than before, so the owner tears down a session
    /// that has finished stopping rather than one still writing files.
    /// </remarks>
    private void StopItself() =>
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await StopAsync();
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    Log.Warning(ex, "The session did not stop cleanly after ending itself.");
                }

                Ended?.Invoke(this, EventArgs.Empty);
            },
            CancellationToken.None);

    /// <summary>
    /// The elapsed counter ticked. Reported so the shell can show a running time without
    /// polling the session or running a timer of its own.
    /// </summary>
    /// <remarks>
    /// The stage comes from whether a recorder is actually running, but the track name comes
    /// from the poller when it is not: an ad or a skipped track is still worth showing as
    /// "playing", and a now-playing line that blanks out whenever recording pauses reads as a
    /// bug rather than as the rule it is.
    /// </remarks>
    private void OnTrackTimeChanged(object? sender, TrackTimeChangedEventArgs e)
    {
        var recording = CurrentTrack;

        Report(
            recording is null ? RecordingStage.WaitingForTrack : RecordingStage.Recording,
            recording ?? _poller.CurrentTrack,
            TimeSpan.FromSeconds(e.TrackTimeSeconds));
    }

    /// <summary>
    /// Playback started or stopped. Starting is also a chance to record what is already on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A track is only admitted while it is playing</b> — <see cref="Track.IsNormalPlaying"/>
    /// is half of <see cref="RecordingPolicy.IsTypeAllowed"/> — and the admission check used to
    /// run in <see cref="OnTrackChanged"/> alone. <see cref="Track.Equals"/> ignores the play
    /// state, so a song that goes from paused to playing is not a new track and raised nothing
    /// but this event. Pressing record while Spotify was paused and then pressing play therefore
    /// left the session waiting for a track change that had already happened: the meter moved,
    /// the counter ran, and nothing was ever written until the user stopped and started again.
    /// The predecessor never met this because it blocked on Spotify producing audio before it
    /// began watching at all; Offstream starts listening immediately, and pays for it here.
    /// </para>
    /// <para>
    /// Only when nothing is being recorded. A pause mid-song leaves the recorder alone — what
    /// Spotify stops sending is silence, and the trim handles it — so a resume with a recorder
    /// still running is the ordinary case and must not start a second one.
    /// </para>
    /// </remarks>
    private void OnPlayStateChanged(object? sender, PlayStateChangedEventArgs e)
    {
        Report(
            e.Playing ? RecordingStage.Recording : RecordingStage.WaitingForTrack,
            message: e.Playing ? null : "Playback paused.");

        if (!e.Playing || CurrentTrack is not null) return;

        // Not when the recording timer has already elapsed: the session is winding down, and the
        // next thing it should do is stop rather than take on another track.
        if (_stopAfterCurrentTrack) return;

        // Only for the track already known. A single poll can see both a new song and a change of
        // play state — starting from a stopped Spotify does exactly that — and the track change
        // is raised straight after this, with the stop-the-outgoing-recorder handling that this
        // path deliberately lacks. Admitting the same track from both would start a recorder only
        // for the other to tear it down a moment later and discard the fragment as too short.
        // The poller has not yet stored this observation, so its current track is the previous
        // one; equality ignores the play state, which is precisely the comparison wanted here.
        if (e.Track.Equals(_poller.CurrentTrack)) Consider(e.Track);
    }

    /// <summary>
    /// The centre of the session: one track ends, the next begins.
    /// </summary>
    /// <remarks>
    /// Runs on the poller's loop. Stopping the previous recorder blocks only long enough for it
    /// to finish reading the shared capture buffer — fast, in-memory, never disk I/O — because
    /// the two recorders share that buffer and the incoming one's <see cref="TrackRecorder.Prime"/>
    /// would otherwise be free to discard the outgoing one's still-unread tail. Finalising to
    /// disk happens after this method returns, on its own task, exactly as before.
    /// </remarks>
    private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
    {
        StopCurrentRecorder();

        if (_stopAfterCurrentTrack)
        {
            // The recording timer elapsed during the previous track. Ending on a track boundary
            // rather than mid-song is the reference's behaviour, and the right one.
            _stopAfterCurrentTrack = false;
            Report(RecordingStage.Stopped, message: "Recording timer elapsed.");
            StopItself();

            return;
        }

        Consider(e.NewTrack);
    }

    /// <summary>
    /// Records <paramref name="track"/> if the settings allow it, and says why when they do not.
    /// </summary>
    /// <remarks>
    /// Shared by the two moments a track can become recordable: it changed, or it started
    /// playing. Everything specific to a track ending — stopping the outgoing recorder, honouring
    /// a recording timer that elapsed — stays with <see cref="OnTrackChanged"/>, because neither
    /// is true of a song resuming.
    /// </remarks>
    private void Consider(Track track)
    {
        if (!_policy.IsTypeAllowed(track))
        {
            Report(RecordingStage.WaitingForTrack, track, message: DescribeSkipped(track));
            return;
        }

        if (_policy.IsMaxOrderNumberAsFileExceeded)
        {
            Report(
                RecordingStage.WaitingForTrack,
                track,
                message: $"File counter reached its maximum ({_settings.OrderNumberMax}); not recording.");

            return;
        }

        var paths = PathsFor(track);

        // A shortcut, not the decision: it can only see what the title or media session gave us,
        // so a template using {album}, {year} or {track} renders somewhere else entirely until
        // enrichment lands. TrackRecorder asks again once it has — see TrackRecorder.AlreadyOnDisk.
        if (_settings.ExistingFilePolicy == ExistingFilePolicy.Skip && AlreadyRecorded(paths, track))
        {
            Report(
                RecordingStage.WaitingForTrack,
                track,
                message: $"Kept the file already on disk and did not record {track}.");

            return;
        }

        StartRecorder(track, paths);
    }

    /// <summary>
    /// Starts a recorder for <paramref name="track"/>, with its metadata lookup already in flight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The track is snapshotted first.</b> The poller owns the instance it hands over and
    /// keeps reporting against it; enrichment writes album, year and cover art onto the copy, so
    /// the two cannot interfere.
    /// </para>
    /// <para>
    /// <b>Enrichment starts here, not when the recording ends.</b> A lookup takes about a second
    /// and the track plays for minutes, so overlapping them makes it free; doing it after the
    /// recording would add that second to every track before its file appears.
    /// </para>
    /// <para>
    /// <b>And it is the recorder's to cancel.</b> The lookup outlives the track it belongs to on
    /// purpose — a finished recording is tagged after the song has ended — so it cannot be tied to
    /// the track boundary. What it must not outlive is the recording itself: a fragment that is
    /// discarded has nothing left to tag, and its lookup is stopped by
    /// <see cref="TrackRecorder"/> at the moment that is decided. The source is linked to the
    /// session's own token so a teardown still ends it, and handed over with the task.
    /// </para>
    /// </remarks>
    private void StartRecorder(Track detected, OutputPaths paths)
    {
        if (_buffer is null) return;

        var track = new Track(detected);

        var enrichmentCancellation = _enricher is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);

        var enrichment = enrichmentCancellation is null
            ? null
            : _enricher!.EnrichAsync(track, enrichmentCancellation.Token);

        AnnounceWhenEnriched(track, paths, enrichment);

        var recorder = new TrackRecorder(
            _buffer, _settings, track, paths, _fileSystem, enrichment, enrichmentCancellation);

        // Now, on the poll loop, not when the recording task gets scheduled: everything captured
        // after this instant belongs to the new track.
        recorder.Prime();

        lock (_gate)
        {
            _recorder = recorder;
            _recording = Task.Run(() => recorder.RunAsync(_stopping.Token), CancellationToken.None);
        }

        Report(RecordingStage.Recording, track, message: $"Recording {track}.");
    }

    /// <summary>
    /// Raises <see cref="TrackEnriched"/> once the lookup for this track has come back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fire and forget, deliberately: the recording must not wait on this, and the recorder is
    /// already awaiting the same task for the part that has to be in the file. Awaiting a task
    /// twice is fine — this is a second observer, not a second lookup.
    /// </para>
    /// <para>
    /// It reports with no provider configured too, and immediately. There is still a destination
    /// worth showing, and a page that only lit up for Spotify users would look broken to everyone
    /// else.
    /// </para>
    /// </remarks>
    private void AnnounceWhenEnriched(Track track, OutputPaths paths, Task<TrackEnrichment>? enrichment)
    {
        _ = AnnounceAsync();

        async Task AnnounceAsync()
        {
            string? coverArt = null;

            if (enrichment is not null)
            {
                try
                {
                    coverArt = (await enrichment).CoverArtPath;
                }
#pragma warning disable CA1031 // A failed lookup is already handled where it matters; this is display.
                catch (Exception)
#pragma warning restore CA1031
                {
                    // ITrackEnricher promises not to throw. If it breaks that promise the
                    // recording still stands, and so does everything else on this event.
                }
            }

            TrackEnriched?.Invoke(this, new TrackEnrichedEventArgs(track, coverArt, Destination(paths, track)));
        }
    }

    /// <summary>Where a track is currently expected to land, or null when that cannot be rendered.</summary>
    private string? Destination(OutputPaths paths, Track track)
    {
        try
        {
            return paths.ResolveMediaFilePath(track, _settings);
        }
        catch (UnrecognizedTrackException)
        {
            // An advertisement, or a track with nothing to build a name from. There is no
            // destination to show and that is not a fault worth reporting.
            return null;
        }
    }

    private void StopCurrentRecorder()
    {
        TrackRecorder? recorder;
        Task<TrackRecording>? recording;

        lock (_gate)
        {
            recorder = _recorder;
            recording = _recording;
            _recorder = null;
            _recording = null;
        }

        if (recorder is null || recording is null) return;

        recorder.Stop();

        // Bounded and fast: this recorder's background task only needs to reach the point where
        // it stops reading the capture buffer, not finish writing or closing its file. Skipping
        // this wait is the bug this fixes — the incoming recorder's Prime(), called right after
        // this method returns, discards whatever is in the buffer, and without this it could win
        // the race against this recorder's own tail-drain and take audio that was never its own.
        recorder.BufferDrained.GetAwaiter().GetResult();

        // Finalising touches the disk, so it is left on its own task rather than blocking the
        // poll loop that has the next track waiting.
        _ = recording.ContinueWith(
            completed => OnTrackFinished(recorder, completed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Stops the current track and waits for it, on the way out of the session.</summary>
    private async Task FinishCurrentTrackAsync()
    {
        Task<TrackRecording>? recording;
        TrackRecorder? recorder;

        lock (_gate)
        {
            recorder = _recorder;
            recording = _recording;
            _recorder = null;
            _recording = null;
        }

        if (recorder is null || recording is null) return;

        recorder.Stop();

        try
        {
            var result = await recording;
            Handle(result);
        }
        catch (OperationCanceledException)
        {
            // Torn down rather than stopped; nothing to save.
        }
        finally
        {
            recorder.Dispose();
        }
    }

    private void OnTrackFinished(TrackRecorder recorder, Task<TrackRecording> completed)
    {
        try
        {
            if (completed.IsCompletedSuccessfully)
            {
                Handle(completed.Result);
                return;
            }

            if (completed.IsCanceled) return;

            var track = recorder.Track;
            var exception = completed.Exception?.GetBaseException();

            RaiseFailed(track, $"Recording {track} failed: {exception?.Message}", exception);
        }
        finally
        {
            recorder.Dispose();
        }
    }

    /// <summary>Queues what is worth encoding, and reports what is not.</summary>
    private void Handle(TrackRecording recording)
    {
        TrackRecorded?.Invoke(this, new TrackRecordedEventArgs(recording));

        switch (recording.Outcome)
        {
            case RecordingOutcome.Captured when recording.Encode is not null:
                lock (_gate) _pending[recording.Encode.OutputPath] = recording;

                if (!_backlog.Enqueue(recording.Encode))
                {
                    lock (_gate) _pending.Remove(recording.Encode.OutputPath);
                    return;
                }

                Report(
                    RecordingStage.Encoding,
                    recording.Track,
                    recording.Duration,
                    $"Encoding {recording.Track}.");

                break;

            case RecordingOutcome.TooShort:
                Report(
                    RecordingStage.WaitingForTrack,
                    recording.Track,
                    recording.Duration,
                    $"Discarded {recording.Track}: shorter than "
                    + $"{_settings.MinimumRecordedLengthSeconds}s.");

                break;

            case RecordingOutcome.AlreadyRecorded:
                Report(
                    RecordingStage.WaitingForTrack,
                    recording.Track,
                    recording.Duration,
                    $"Kept the file already on disk and discarded this recording of {recording.Track}: "
                    + $"{recording.Destination}");

                break;

            case RecordingOutcome.Silent:
                RaiseFailed(
                    recording.Track,
                    "Nothing was captured. Spotify is playing to a different audio device than the one being recorded.");

                break;

            case RecordingOutcome.Cancelled:
            default:
                break;
        }
    }

    /// <summary>The encode finished: claim a library name, move the file onto it, tidy up.</summary>
    private void OnEncodeCompleted(object? sender, EncodeCompletedEventArgs e)
    {
        TrackRecording? recording;

        lock (_gate)
        {
            if (!_pending.Remove(e.Outcome.OutputPath, out recording)) return;
        }

        var track = recording.Track;

        try
        {
            var paths = PathsFor(track);

            // Before the folders are created and the counter is applied, so the answer is about
            // the name the template asked for rather than the one a counter just moved off it.
            var replacing = _fileSystem.File.Exists(paths.ResolveMediaFilePath(track, _settings));

            var outputFile = paths.GetOutputFileAndInitDirectories();
            var destination = outputFile.ToMediaFilePath()
                              ?? throw new UnrecognizedTrackException("Output path resolved to nothing.");

            // The last gate on the keep-what-is-there policy. TrackRecorder already checked, and
            // for one recording that is the end of it — but encodes queue, so two recordings of
            // the same track can both pass that check and reach here, where RenameFile would
            // replace the destination without a word.
            if (replacing && _settings.ExistingFilePolicy == ExistingFilePolicy.Skip)
            {
                TryDelete(e.Outcome.OutputPath);
                paths.DeleteFile(recording.Encode!.InputPath);
                TryDelete(recording.Encode.CoverArtPath);

                Report(
                    RecordingStage.WaitingForTrack,
                    track,
                    recording.Duration,
                    $"Kept the file already on disk and discarded this recording of {track}: {destination}");

                return;
            }

            paths.RenameFile(e.Outcome.OutputPath, destination);
            paths.DeleteFile(recording.Encode!.InputPath);
            TryDelete(recording.Encode.CoverArtPath);

            if (_settings.HasOrderNumberEnabled) _settings.InternalOrderNumber++;

            if (e.Outcome.CoverArtFailure is not null)
            {
                // Warning rather than the progress report's Information: the recording survived,
                // but a tag that was asked for did not get written, and at Information it sat
                // below the Problems filter — invisible to anyone who went looking for exactly
                // this. Logged directly for the same reason; a progress message cannot carry a
                // level.
                Log.Warning(
                    e.Outcome.CoverArtFailure,
                    "Cover art could not be embedded in {Track}. The recording itself is fine.",
                    track);

                Report(RecordingStage.Tagging, track);
            }

            TrackSaved?.Invoke(this, new TrackSavedEventArgs(track, destination, recording.Duration));

            Report(
                RecordingStage.WaitingForTrack,
                track,
                recording.Duration,
                DescribeSaved(outputFile, replacing));
        }
        catch (Exception ex) when (ex is UnrecognizedTrackException
                                       or SourceFileNotFoundException
                                       or DestinationPathNotFoundException
                                       or IOException
                                       or UnauthorizedAccessException)
        {
            RaiseFailed(track, $"Could not save {track}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The encode failed. The captured WAV is kept: it is the part that cannot be recreated,
    /// and a missing or broken ffmpeg is exactly the case where a user wants it back.
    /// </summary>
    private void OnEncodeFailed(object? sender, EncodeFailedEventArgs e)
    {
        TrackRecording? recording;

        lock (_gate)
        {
            _pending.Remove(e.Request.OutputPath, out recording);
        }

        var track = recording?.Track ?? e.Request.Track ?? new Track();

        TryDelete(e.Request.OutputPath);
        TryDelete(e.Request.CoverArtPath);

        RaiseFailed(
            track,
            $"Encoding {track} failed: {e.Exception.Message} The captured audio was kept at {e.Request.InputPath}.",
            e.Exception);
    }

    /// <summary>
    /// Says what the existing-file policy did, when it did anything.
    /// </summary>
    /// <remarks>
    /// The policy is otherwise invisible: overwriting and adding a counter both look exactly like
    /// an ordinary save from the outside, which leaves a user with no way to tell a setting that
    /// is working from one that is not.
    /// </remarks>
    private static string DescribeSaved(OutputFile outputFile, bool replacing) => (replacing, outputFile.IsCounted) switch
    {
        (true, false) => $"Saved {outputFile}, replacing the file already there.",
        (_, true) => $"Saved {outputFile}; the name the template asked for was taken.",
        _ => $"Saved {outputFile}.",
    };

    private OutputPaths PathsFor(Track track) =>
        new(_settings, track, _fileSystem, _time.GetLocalNow().DateTime);

    private bool AlreadyRecorded(OutputPaths paths, Track track)
    {
        try
        {
            return paths.IsPathFileNameExists(track, _settings);
        }
        catch (UnrecognizedTrackException)
        {
            // Nothing renderable to compare against; let the recording proceed and fail later
            // with a message about the track rather than silently skipping it here.
            return false;
        }
    }

    private void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            if (_fileSystem.File.Exists(path)) _fileSystem.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is not worth surfacing over the failure that caused it.
        }
    }

    private static string DescribeSkipped(Track track) =>
        track.Ad ? "Advertisement; not recording." : $"Not a recordable track: {track}.";

    private void StartRecordingTimer()
    {
        if (!_settings.HasRecordingTimerEnabled) return;

        _recordingTimer = _time.CreateTimer(
            _ => _stopAfterCurrentTrack = true,
            state: null,
            _settings.RecordingTimerDuration,
            Timeout.InfiniteTimeSpan);
    }

    private void StopRecordingTimer()
    {
        _recordingTimer?.Dispose();
        _recordingTimer = null;
    }

    /// <remarks>
    /// The message goes out on <see cref="Failed"/> only, and deliberately not on the progress
    /// report beside it. Both ended up in the activity log — the event at Error and the report's
    /// message at Information — so every failure was printed twice, identically, once in a colour
    /// that said act on this and once in one that said carry on. The report still fires, because
    /// it is what moves the display back to waiting; it just no longer narrates.
    /// </remarks>
    private void RaiseFailed(Track track, string message, Exception? exception = null)
    {
        Failed?.Invoke(this, new RecordingFailedEventArgs(track, message, exception));
        Report(RecordingStage.WaitingForTrack, track);
    }

    /// <summary>
    /// Emits a progress report, and works out what is playing while it does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The track a report is about is not always the track playing.</b> Encoding, tagging and
    /// the save message all describe the <i>previous</i> song and arrive well after the next one
    /// has started, because finalising to disk deliberately overlaps the following recording.
    /// Consumers that showed <see cref="RecordingProgress.Track"/> as the now-playing line
    /// therefore flipped back to the finished song for as long as it took the next elapsed tick
    /// to correct them — and anything keyed on that line changing, like the shell dropping a
    /// track's album and cover art, fired twice on a track it should not have touched at all.
    /// </para>
    /// <para>
    /// Both facts are computed here rather than at the eleven call sites, so a new report cannot
    /// forget to carry them and cannot disagree with the others about what "now" means. The live
    /// track is the one being recorded, falling back to whatever Spotify is showing when nothing
    /// is being recorded — an advertisement is still playing, and a now-playing line that blanks
    /// out for one reads as a bug. Identity is by reference, not by name: the poller hands each
    /// track over once and the session snapshots it, so two plays of the same song are two
    /// objects and are told apart correctly.
    /// </para>
    /// </remarks>
    private void Report(RecordingStage stage, Track? track = null, TimeSpan? elapsed = null, string? message = null)
    {
        if (_progress is null) return;

        // Stopped and idle mean the session is not listening, so nothing is playing as far as
        // this report is concerned — whatever the poller last saw is stale by definition.
        var live = stage is RecordingStage.Stopped or RecordingStage.Idle
            ? null
            : Named(CurrentTrack ?? _poller.CurrentTrack);

        var subject = Named(track);

        _progress.Report(new RecordingProgress(
            stage,
            subject?.ToString(),
            elapsed,
            message,
            live?.ToString(),
            subject is null || ReferenceEquals(subject, live)));
    }

    /// <summary>
    /// A track worth naming, or null for one that only stands in for the absence of a track.
    /// </summary>
    /// <remarks>
    /// <see cref="SpotifyPoller.Start"/> seeds an empty <see cref="Track"/> so that the song
    /// already playing counts as a change, and Spotify's own title when nothing is on parses to
    /// the same thing. Either renders as the bare word "Spotify", so without this the page said
    /// a song called Spotify was playing and the tray offered to be recording it, from the
    /// instant the user pressed start.
    /// </remarks>
    private static Track? Named(Track? track) =>
        track is not null && (!string.IsNullOrEmpty(track.Artist) || !string.IsNullOrEmpty(track.Title))
            ? track
            : null;
}
