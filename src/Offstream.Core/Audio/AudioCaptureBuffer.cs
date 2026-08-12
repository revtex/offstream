using NAudio.Wave;

namespace Offstream.Core.Audio;

/// <summary>
/// The buffer between WASAPI capture and whichever recorder is running: capture writes into it
/// continuously, the recorder drains it a second at a time.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the reference implementation's <c>AudioThrottler</c>, keeping its sizing — four
/// seconds of capacity, read a second at a time. The slack matters: Spotify changes its window
/// title slightly before or after the audio actually changes, so a recorder that read exactly
/// in step with capture would clip one track into the next.
/// </para>
/// <para>
/// Three departures from the original:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>The capture policy is separated from the capture device.</b> The original combined
///     WASAPI, the ring buffer and read pacing in one class that could not be constructed
///     without an audio endpoint, so none of the pacing was testable. Everything here is a
///     function of a <see cref="WaveFormat"/> and some bytes.
///   </item>
///   <item>
///     <b><see cref="WaitForChunkAsync"/> replaces polling.</b> The original woke every 100 ms
///     to ask whether the buffer had filled. Waiting on a signal is both cheaper over an
///     overnight session and deterministic to test.
///   </item>
///   <item>
///     <b>The "trim silence" read mode is gone.</b> Its implementation was commented out in the
///     reference and what remained simply discarded the buffered audio at track start — which
///     is a real and necessary behaviour, so it is kept under the name it actually deserves:
///     <see cref="DiscardBuffered"/>.
///   </item>
/// </list>
/// </remarks>
public sealed class AudioCaptureBuffer
{
    /// <summary>Seconds of audio the buffer holds before it starts dropping the oldest.</summary>
    public const int DefaultCapacitySeconds = 4;

    private readonly AudioRingBuffer _ring;

    /// <summary>Completed while a full chunk is available; replaced once one is consumed.</summary>
    private TaskCompletionSource _chunkReady = NewGate();

    public AudioCaptureBuffer(WaveFormat format, int capacitySeconds = DefaultCapacitySeconds)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacitySeconds, 1);

        Format = format;
        ChunkSize = format.AverageBytesPerSecond;
        _ring = new AudioRingBuffer(ChunkSize * capacitySeconds);
    }

    /// <summary>The capture format; recorders must write WAV headers matching it.</summary>
    public WaveFormat Format { get; }

    /// <summary>One second of audio, the unit a recorder reads in.</summary>
    public int ChunkSize { get; }

    /// <summary>Total capacity in bytes — the largest a single drain can be.</summary>
    public int Capacity => _ring.Capacity;

    /// <summary>Bytes buffered and not yet read.</summary>
    public int Count => _ring.Count;

    /// <summary>Whether a full chunk is available to read.</summary>
    public bool HasChunk => _ring.Count >= ChunkSize;

    /// <summary>
    /// Whether enough has accumulated to start a recording without immediately starving.
    /// </summary>
    public bool IsReady => _ring.Count > _ring.Capacity / 2;

    /// <summary>Writes captured audio. Never blocks; a full buffer drops the oldest audio.</summary>
    public int Write(ReadOnlySpan<byte> data)
    {
        var written = _ring.Write(data);

        if (_ring.Count >= ChunkSize) _chunkReady.TrySetResult();

        return written;
    }

    /// <summary>Reads exactly one chunk, or nothing if a whole chunk is not yet available.</summary>
    /// <remarks>
    /// All-or-nothing on purpose: writing short reads into the WAV as fast as they arrive would
    /// spin the recorder loop against the capture callback for no gain.
    /// </remarks>
    /// <returns>Bytes written into <paramref name="destination"/>; 0 when no full chunk exists.</returns>
    public int ReadChunk(Span<byte> destination)
    {
        if (destination.Length < ChunkSize)
        {
            throw new ArgumentException(
                $"Destination must hold at least one chunk ({ChunkSize} bytes).", nameof(destination));
        }

        if (!HasChunk) return 0;

        var read = _ring.Read(destination[..ChunkSize]);

        if (!HasChunk) ResetGate();

        return read;
    }

    /// <summary>Reads everything buffered, for the end of a track.</summary>
    /// <returns>Bytes written into <paramref name="destination"/>.</returns>
    public int Drain(Span<byte> destination)
    {
        var available = Math.Min(_ring.Count, destination.Length);
        if (available == 0) return 0;

        var read = _ring.Read(destination[..available]);

        if (!HasChunk) ResetGate();

        return read;
    }

    /// <summary>
    /// Drops everything buffered, at the start of a track.
    /// </summary>
    /// <remarks>
    /// What is buffered at that moment is the tail of the <em>previous</em> track, plus whatever
    /// played while no recorder was running. Writing it into the new file is how a recording
    /// ends up starting with the last second of the song before it.
    /// </remarks>
    /// <returns>Bytes discarded.</returns>
    public int DiscardBuffered()
    {
        var dropped = _ring.Count;

        _ring.Advance(dropped);
        ResetGate();

        return dropped;
    }

    /// <summary>Waits until a full chunk can be read.</summary>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task WaitForChunkAsync(CancellationToken cancellationToken = default)
    {
        while (!HasChunk)
        {
            var gate = Volatile.Read(ref _chunkReady);

            // Re-check after capturing the gate: a write between the loop test and here would
            // otherwise complete a gate this iteration has already stopped watching.
            if (HasChunk) return;

            await gate.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Empties the buffer and forgets the read position, between sessions.</summary>
    public void Reset()
    {
        _ring.Reset();
        ResetGate();
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Re-arms the wait gate after the buffer has been drained below a chunk. Only swaps out
    /// the gate it observed, so a write completing one concurrently is never lost — the waiter
    /// re-checks <see cref="HasChunk"/> either way.
    /// </summary>
    private void ResetGate()
    {
        var gate = Volatile.Read(ref _chunkReady);

        if (gate.Task.IsCompleted) Interlocked.CompareExchange(ref _chunkReady, NewGate(), gate);
    }
}
