using System.IO.Abstractions;
using NAudio.Wave;
using Offstream.Core.Audio;
using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Offstream.Core.Naming;
using Offstream.Core.Settings;

namespace Offstream.Core.Recording;

/// <summary>How a track's recording ended.</summary>
public enum RecordingOutcome
{
    /// <summary>Captured and ready to encode.</summary>
    Captured,

    /// <summary>Shorter than the configured minimum; discarded.</summary>
    TooShort,

    /// <summary>Nothing was captured at all — Spotify is playing to a different endpoint.</summary>
    Silent,

    /// <summary>The session was torn down mid-track.</summary>
    Cancelled,
}

/// <summary>What one track's recording produced.</summary>
/// <param name="Outcome">Why it ended.</param>
/// <param name="Track">The track recorded.</param>
/// <param name="Duration">Audio actually captured, measured from the bytes written.</param>
/// <param name="Encode">
/// The encode to queue, when <see cref="Outcome"/> is <see cref="RecordingOutcome.Captured"/>.
/// Its output path is a temp file: the final name is claimed at rename time, after encoding.
/// </param>
public sealed record TrackRecording(
    RecordingOutcome Outcome,
    Track Track,
    TimeSpan Duration,
    EncodeRequest? Encode = null);

/// <summary>
/// Records one track: drains captured audio into a temp WAV, then decides whether it is worth
/// keeping.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the reference implementation's <c>Recorder</c>, with its encode half removed —
/// ffmpeg owns conversion now (§4, §5) — and the form reference gone. What remains is the part
/// that was always worth keeping: the WAV writing, the minimum-length rule, and the
/// empty-capture case that means the user is playing to a device Offstream is not recording.
/// </para>
/// <para>
/// <b>Duration is measured from the bytes written, not from a clock.</b> The reference counted
/// seconds on the watcher's timer tick and passed the count into the recorder, so a stalled
/// capture still "recorded" for as long as the song played and a short file could pass the
/// minimum-length rule. Bytes over <see cref="WaveFormat.AverageBytesPerSecond"/> is the length
/// of the audio that actually exists.
/// </para>
/// </remarks>
public sealed class TrackRecorder : IDisposable
{
    private readonly AudioCaptureBuffer _buffer;
    private readonly RecordingSettings _settings;
    private readonly IFileSystem _fileSystem;
    private readonly OutputPaths _paths;
    private readonly Track _track;

    private readonly CancellationTokenSource _stopping = new();

    private long _bytesWritten;
    private bool _primed;

    public TrackRecorder(
        AudioCaptureBuffer buffer,
        RecordingSettings settings,
        Track track,
        OutputPaths paths,
        IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _buffer = buffer;
        _settings = settings;
        _track = track;
        _paths = paths;
        _fileSystem = fileSystem;
    }

    /// <summary>The track this recorder is capturing.</summary>
    public Track Track => _track;

    /// <summary>Audio captured so far, from the bytes written.</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds(
        (double)Volatile.Read(ref _bytesWritten) / _buffer.Format.AverageBytesPerSecond);

    /// <summary>Whether the capture loop is still running.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>
    /// Claims the capture buffer for this track, dropping what the previous one left in it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RunAsync"/>, and idempotent, so the caller can do it at the
    /// instant the track changes rather than whenever the recording task happens to be
    /// scheduled. That distinction is the difference between dropping the previous track's tail
    /// — which is the point — and dropping the first milliseconds of the new one, which is what
    /// deferring it to the task does.
    /// </remarks>
    public void Prime()
    {
        if (_primed) return;

        _primed = true;
        _buffer.DiscardBuffered();
    }

    /// <summary>
    /// Asks the recorder to finish: it drains what is buffered and returns from
    /// <see cref="RunAsync"/>. Idempotent, and safe from any thread.
    /// </summary>
    public void Stop()
    {
        if (!_stopping.IsCancellationRequested) _stopping.Cancel();
    }

    /// <summary>
    /// Captures until <see cref="Stop"/> is called, then finalises the file.
    /// </summary>
    /// <param name="cancellationToken">Tears the session down; the partial file is discarded.</param>
    public async Task<TrackRecording> RunAsync(CancellationToken cancellationToken = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(
            _stopping.Token, cancellationToken);

        // Whatever is buffered belongs to the previous track, or to the gap before this one
        // started. Starting from it is how a recording opens with the tail of the song before
        // it. A caller that has already primed pays nothing here.
        Prime();

        var tempWavePath = _paths.GetTempFile();
        var chunk = new byte[_buffer.Capacity];

        IsRecording = true;

        try
        {
            await using (var stream = _fileSystem.FileStream.New(
                             tempWavePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            await using (var writer = new WaveFileWriter(stream, _buffer.Format))
            {
                await CaptureAsync(writer, chunk, stopping.Token);

                // The tail: everything captured between the last chunk and the stop request.
                var remaining = _buffer.Drain(chunk);
                if (remaining > 0) await WriteAsync(writer, chunk, remaining);

                await writer.FlushAsync(CancellationToken.None);
            }
        }
        finally
        {
            IsRecording = false;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _paths.DeleteFile(tempWavePath);
            return new TrackRecording(RecordingOutcome.Cancelled, _track, Elapsed);
        }

        return Finalise(tempWavePath);
    }

    /// <summary>Releases the stop signal. Recording itself ends with <see cref="Stop"/>.</summary>
    public void Dispose() => _stopping.Dispose();

    private async Task CaptureAsync(WaveFileWriter writer, byte[] chunk, CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await _buffer.WaitForChunkAsync(stopping);

                var read = _buffer.ReadChunk(chunk);
                if (read > 0) await WriteAsync(writer, chunk, read);
            }
        }
        catch (OperationCanceledException)
        {
            // The normal way a track ends: Stop() or session teardown.
        }
    }

    /// <summary>
    /// Writes a slice that has already been taken out of the capture buffer.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not cancellable.</b> A read removes the audio from the ring buffer, so a
    /// write cancelled between the two loses it with nowhere to recover it from — and the token
    /// is cancelled at exactly the moment a track ends, which is when the last chunk is in
    /// flight. Stopping is handled by the loop's own check, not by abandoning bytes already in
    /// hand.
    /// </remarks>
    private async Task WriteAsync(WaveFileWriter writer, byte[] data, int count)
    {
        await writer.WriteAsync(data.AsMemory(0, count), CancellationToken.None);

        Interlocked.Add(ref _bytesWritten, count);
    }

    /// <summary>
    /// Decides what the captured file is worth, and hands back an encode when it is worth
    /// anything.
    /// </summary>
    private TrackRecording Finalise(string tempWavePath)
    {
        var duration = Elapsed;

        if (_bytesWritten == 0)
        {
            // Not a failure of ours: Spotify is playing to an endpoint this session is not
            // capturing, which the shell reports as such rather than as an error.
            _paths.DeleteFile(tempWavePath);
            return new TrackRecording(RecordingOutcome.Silent, _track, duration);
        }

        if (duration.TotalSeconds < _settings.MinimumRecordedLengthSeconds)
        {
            _paths.DeleteFile(tempWavePath);
            return new TrackRecording(RecordingOutcome.TooShort, _track, duration);
        }

        // Encode to a temp file with the right extension. The destination name is claimed at
        // rename time, not now: encoding is queued behind other tracks, and a name reserved
        // minutes early can be taken by the recording that finishes first.
        var extension = EncodingProfiles.For(_settings.MediaFormat).Extension;
        var tempEncodePath = _fileSystem.Path.ChangeExtension(_paths.GetTempFile(), $".{extension}");

        var encode = new EncodeRequest(
            tempWavePath,
            tempEncodePath,
            _settings.MediaFormat,
            _settings.BitrateKbps,
            _track);

        return new TrackRecording(RecordingOutcome.Captured, _track, duration, encode);
    }
}
