namespace Offstream.Core.Audio;

/// <summary>
/// A fixed-size circular byte buffer sitting between WASAPI capture and the per-track recorders.
/// </summary>
/// <remarks>
/// <para>
/// Capture runs on its own thread and must never block; recorders drain slices from behind
/// it. When the buffer is full, writes are truncated rather than blocking or growing — a
/// dropped sample is far better than stalling the capture callback, which would corrupt the
/// stream for every listener.
/// </para>
/// <para>
/// Ported from the reference implementation's <c>AudioCircularBuffer</c>, which had no tests
/// at all despite the wrap-around arithmetic. Two changes were made:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>A race in <c>Peek</c> is fixed.</b> It captured the read position and byte count
///     <em>before</em> taking the lock, so a concurrent write could change them underneath
///     it and produce a torn read. All state is now read inside the lock.
///   </item>
///   <item>
///     <b>Span-based reads.</b> The original allocated a fresh <c>byte[MaxLength]</c> on
///     every read — in a hot audio path running continuously for hours. Callers pass a
///     destination span instead.
///   </item>
/// </list>
/// </remarks>
public sealed class AudioRingBuffer(int capacity)
{
    private readonly byte[] _buffer = capacity > 0
        ? new byte[capacity]
        : throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");

    private readonly Lock _gate = new();

    private int _writePosition;
    private int _readPosition;
    private int _byteCount;

    /// <summary>Total capacity in bytes.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Bytes currently buffered.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _byteCount;
        }
    }

    /// <summary>Current read offset, for diagnostics.</summary>
    public int ReadPosition
    {
        get
        {
            lock (_gate) return _readPosition;
        }
    }

    /// <summary>Current write offset, for diagnostics.</summary>
    public int WritePosition
    {
        get
        {
            lock (_gate) return _writePosition;
        }
    }

    /// <summary>
    /// Writes as much of <paramref name="source"/> as fits, returning how many bytes were taken.
    /// </summary>
    /// <remarks>A short return means the buffer is full and audio was dropped.</remarks>
    public int Write(ReadOnlySpan<byte> source)
    {
        lock (_gate)
        {
            var count = Math.Min(source.Length, _buffer.Length - _byteCount);
            if (count == 0) return 0;

            var toEnd = Math.Min(_buffer.Length - _writePosition, count);
            source[..toEnd].CopyTo(_buffer.AsSpan(_writePosition));
            _writePosition = (_writePosition + toEnd) % _buffer.Length;

            if (toEnd < count)
            {
                // Wrapped: the remainder goes at the start.
                source.Slice(toEnd, count - toEnd).CopyTo(_buffer.AsSpan(_writePosition));
                _writePosition += count - toEnd;
            }

            _byteCount += count;
            return count;
        }
    }

    /// <inheritdoc cref="Write(ReadOnlySpan{byte})"/>
    public int Write(byte[] data, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Write(data.AsSpan(offset, count));
    }

    /// <summary>Copies up to <paramref name="destination"/>.Length bytes out and consumes them.</summary>
    public int Read(Span<byte> destination)
    {
        lock (_gate)
        {
            var count = CopyOut(destination, _readPosition);
            _readPosition = (_readPosition + count) % _buffer.Length;
            _byteCount -= count;
            return count;
        }
    }

    /// <summary>Copies bytes out <em>without</em> consuming them.</summary>
    public int Peek(Span<byte> destination)
    {
        lock (_gate) return CopyOut(destination, _readPosition);
    }

    /// <summary>Discards up to <paramref name="count"/> buffered bytes.</summary>
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        lock (_gate)
        {
            if (count >= _byteCount)
            {
                ResetCore();
                return;
            }

            _byteCount -= count;
            _readPosition = (_readPosition + count) % _buffer.Length;
        }
    }

    /// <summary>Empties the buffer.</summary>
    public void Reset()
    {
        lock (_gate) ResetCore();
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private int CopyOut(Span<byte> destination, int from)
    {
        var count = Math.Min(destination.Length, _byteCount);
        if (count == 0) return 0;

        var toEnd = Math.Min(_buffer.Length - from, count);
        _buffer.AsSpan(from, toEnd).CopyTo(destination);

        if (toEnd < count)
        {
            // Wrapped: the remainder comes from the start.
            _buffer.AsSpan(0, count - toEnd).CopyTo(destination[toEnd..]);
        }

        return count;
    }

    private void ResetCore()
    {
        _byteCount = 0;
        _readPosition = 0;
        _writePosition = 0;
    }
}
