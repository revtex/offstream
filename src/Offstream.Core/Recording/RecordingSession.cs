using System.IO.Abstractions;
using Offstream.Core.Audio;
using Offstream.Core.Diagnostics;
using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Offstream.Core.Naming;
using Offstream.Core.Settings;
using Offstream.Core.Spotify;

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

    public RecordingSession(
        IAudioCaptureSource capture,
        SpotifyPoller poller,
        RecordingSettings settings,
        IAudioEncoder encoder,
        IFileSystem fileSystem,
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
        _progress = progress;
        _time = timeProvider ?? TimeProvider.System;

        _backlog = new EncodeBacklog(encoder);
        _backlog.Completed += OnEncodeCompleted;
        _backlog.Failed += OnEncodeFailed;
    }

    /// <summary>A track finished recording — including the ones that produced nothing.</summary>
    public event EventHandler<TrackRecordedEventArgs>? TrackRecorded;

    /// <summary>A finished file landed in the library.</summary>
    public event EventHandler<TrackSavedEventArgs>? TrackSaved;

    /// <summary>A recording was lost, or could not be encoded.</summary>
    public event EventHandler<RecordingFailedEventArgs>? Failed;

    /// <summary>Whether the session is running.</summary>
    public bool IsRunning { get; private set; }

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

        _backlog.Start();

        _poller.TrackChanged += OnTrackChanged;
        _poller.PlayStateChanged += OnPlayStateChanged;
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
        await _poller.DisposeAsync();

        await FinishCurrentTrackAsync();

        _capture.DataAvailable -= OnAudioAvailable;
        _capture.StopCapture();

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

        _stopping.Dispose();
    }

    private void OnAudioAvailable(object? sender, AudioDataEventArgs e) => _buffer?.Write(e.Data);

    private void OnPlayStateChanged(object? sender, PlayStateChangedEventArgs e) =>
        Report(
            e.Playing ? RecordingStage.Recording : RecordingStage.WaitingForTrack,
            message: e.Playing ? null : "Playback paused.");

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
            _ = Task.Run(() => StopAsync(), CancellationToken.None);

            return;
        }

        var track = e.NewTrack;

        if (!_policy.IsTypeAllowed(track))
        {
            Report(RecordingStage.WaitingForTrack, track.ToString(), message: DescribeSkipped(track));
            return;
        }

        if (_policy.IsMaxOrderNumberAsFileExceeded)
        {
            Report(
                RecordingStage.WaitingForTrack,
                track.ToString(),
                message: $"File counter reached its maximum ({_settings.OrderNumberMax}); not recording.");

            return;
        }

        var paths = PathsFor(track);

        if (_settings.ExistingFilePolicy == ExistingFilePolicy.Skip && AlreadyRecorded(paths, track))
        {
            Report(RecordingStage.WaitingForTrack, track.ToString(), message: "Already recorded; skipping.");
            return;
        }

        StartRecorder(track, paths);
    }

    private void StartRecorder(Track track, OutputPaths paths)
    {
        if (_buffer is null) return;

        var recorder = new TrackRecorder(_buffer, _settings, track, paths, _fileSystem);

        // Now, on the poll loop, not when the recording task gets scheduled: everything captured
        // after this instant belongs to the new track.
        recorder.Prime();

        lock (_gate)
        {
            _recorder = recorder;
            _recording = Task.Run(() => recorder.RunAsync(_stopping.Token), CancellationToken.None);
        }

        Report(RecordingStage.Recording, track.ToString(), message: $"Recording {track}.");
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
                    recording.Track.ToString(),
                    recording.Duration,
                    $"Encoding {recording.Track}.");

                break;

            case RecordingOutcome.TooShort:
                Report(
                    RecordingStage.WaitingForTrack,
                    recording.Track.ToString(),
                    recording.Duration,
                    $"Discarded {recording.Track}: shorter than "
                    + $"{_settings.MinimumRecordedLengthSeconds}s.");

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
            var outputFile = paths.GetOutputFileAndInitDirectories();
            var destination = outputFile.ToMediaFilePath()
                              ?? throw new UnrecognizedTrackException("Output path resolved to nothing.");

            paths.RenameFile(e.Outcome.OutputPath, destination);
            paths.DeleteFile(recording.Encode!.InputPath);

            if (_settings.HasOrderNumberEnabled) _settings.InternalOrderNumber++;

            if (e.Outcome.CoverArtFailure is not null)
            {
                Report(
                    RecordingStage.Tagging,
                    track.ToString(),
                    message: $"Cover art could not be embedded: {e.Outcome.CoverArtFailure.Message}");
            }

            TrackSaved?.Invoke(this, new TrackSavedEventArgs(track, destination, recording.Duration));

            Report(
                RecordingStage.WaitingForTrack,
                track.ToString(),
                recording.Duration,
                $"Saved {outputFile}.");
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

        RaiseFailed(
            track,
            $"Encoding {track} failed: {e.Exception.Message} The captured audio was kept at {e.Request.InputPath}.",
            e.Exception);
    }

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

    private void TryDelete(string path)
    {
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

    private void RaiseFailed(Track track, string message, Exception? exception = null)
    {
        Failed?.Invoke(this, new RecordingFailedEventArgs(track, message, exception));
        Report(RecordingStage.WaitingForTrack, track.ToString(), message: message);
    }

    private void Report(RecordingStage stage, string? track = null, TimeSpan? elapsed = null, string? message = null) =>
        _progress?.Report(new RecordingProgress(stage, track, elapsed, message));
}
