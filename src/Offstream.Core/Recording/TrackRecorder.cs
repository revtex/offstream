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
    private readonly Task<TrackEnrichment>? _enrichment;

    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _bufferDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _bytesWritten;
    private bool _primed;

    /// <param name="buffer">The shared capture buffer this recording drains.</param>
    /// <param name="settings">The session's settings.</param>
    /// <param name="track">The track being recorded. Enrichment writes onto this instance.</param>
    /// <param name="paths">Temp-file and destination-name resolution.</param>
    /// <param name="fileSystem">Injected so the WAV writing is testable.</param>
    /// <param name="enrichment">
    /// The metadata lookup for this track, already running. Started by the caller at the instant
    /// the track changed so it overlaps the recording instead of following it, and joined here
    /// just before the encode request is built — the last moment at which album, track number and
    /// cover art can still reach the file.
    /// </param>
    public TrackRecorder(
        AudioCaptureBuffer buffer,
        RecordingSettings settings,
        Track track,
        OutputPaths paths,
        IFileSystem fileSystem,
        Task<TrackEnrichment>? enrichment = null)
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
        _enrichment = enrichment;
    }

    /// <summary>The track this recorder is capturing.</summary>
    public Track Track => _track;

    /// <summary>Audio captured so far, from the bytes written.</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds(
        (double)Volatile.Read(ref _bytesWritten) / _buffer.Format.AverageBytesPerSecond);

    /// <summary>Whether the capture loop is still running.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>
    /// Completes the instant this recorder is done reading from the shared capture buffer —
    /// well before the file is flushed and closed.
    /// </summary>
    /// <remarks>
    /// This is what <see cref="RecordingSession"/> waits on before starting the next track's
    /// recorder. Two recorders never overlap in wall-clock terms, but they do share one
    /// <see cref="AudioCaptureBuffer"/>, and the next one's <see cref="Prime"/> discards
    /// whatever is sitting in it — including this recorder's own unread tail, if <see cref="Prime"/>
    /// runs before this recorder's background task gets around to draining it. Waiting on this
    /// buffer-only signal (fast, in-memory) closes that race without waiting on this recorder's
    /// disk I/O too (slow, and exactly what <see cref="RecordingSession"/> must not block on).
    /// </remarks>
    public Task BufferDrained => _bufferDrained.Task;

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
                try
                {
                    await CaptureAsync(writer, chunk, stopping.Token);

                    // The tail: everything captured between the last chunk and the stop request.
                    var remaining = _buffer.Drain(chunk);
                    if (remaining > 0) await WriteAsync(writer, chunk, remaining);
                }
                finally
                {
                    // Signalled here, not in a top-level finally: this must fire the moment the
                    // buffer is done with, before the flush below, or the wait it unblocks would
                    // itself be waiting on this recorder's disk I/O — precisely what it exists to
                    // avoid.
                    _bufferDrained.TrySetResult();
                }

                await writer.FlushAsync(CancellationToken.None);
            }
        }
        finally
        {
            IsRecording = false;

            // A safety net, not the primary signal: covers a throw before the buffer was ever
            // touched (e.g. the temp file could not be created), so a waiter is never left
            // hanging. TrySetResult is idempotent — the normal path above already set it.
            _bufferDrained.TrySetResult();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _paths.DeleteFile(tempWavePath);
            return new TrackRecording(RecordingOutcome.Cancelled, _track, Elapsed);
        }

        return await FinaliseAsync(tempWavePath);
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
    /// <remarks>
    /// The metadata lookup is joined here and nowhere earlier. Everything it writes — album,
    /// track number, disc, year, genre, album artists, and the cover art file — has to be on the
    /// track before the <see cref="EncodeRequest"/> is built, because that request is the whole
    /// of what ffmpeg is told about the recording. A recording that is discarded as too short or
    /// silent never waits on it.
    /// </remarks>
    private async Task<TrackRecording> FinaliseAsync(string tempWavePath)
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

        var enrichment = await AwaitEnrichmentAsync();

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
            _track,
            enrichment.CoverArtPath,
            _settings.OrderNumberAsTag);

        return new TrackRecording(RecordingOutcome.Captured, _track, duration, encode);
    }

    /// <summary>
    /// Joins the metadata lookup started when this track began.
    /// </summary>
    /// <remarks>
    /// <see cref="ITrackEnricher"/> promises never to throw, and this is the belt to that
    /// braces: a provider that breaks the promise must still not cost the user the recording
    /// that is already on disk.
    /// </remarks>
    private async Task<TrackEnrichment> AwaitEnrichmentAsync()
    {
        if (_enrichment is null) return TrackEnrichment.None;

        try
        {
            return await _enrichment;
        }
#pragma warning disable CA1031 // Deliberate: see the remarks above.
        catch (Exception)
#pragma warning restore CA1031
        {
            return TrackEnrichment.None;
        }
    }
}
