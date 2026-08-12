using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Offstream.Core.Audio;

/// <summary>
/// Tracks the loudest sample seen since it was last read, for a level display.
/// </summary>
/// <remarks>
/// <para>
/// <b>Accumulate-and-drain, rather than an event per callback.</b> Capture fires roughly every
/// 10 ms; a meter that raised an event each time would push 100 dispatcher marshals a second at
/// the UI for a bar that can only redraw at 60 Hz. Instead the capture thread folds each buffer
/// into a running peak with no allocation and no lock, and whoever is drawing calls
/// <see cref="Read"/> at its own rate. The peak resets on read, so a slow reader sees the
/// loudest moment in its interval instead of whatever happened to be playing at the instant it
/// looked — which is what makes the display track transients rather than flicker.
/// </para>
/// <para>
/// <b>An unsupported format reads as silence rather than throwing.</b> This drives a decoration;
/// the recording is the part that matters, and a format this does not understand must not be
/// able to take a session down. <see cref="IsSupported"/> says so explicitly for anything that
/// wants to hide the meter instead.
/// </para>
/// <para>
/// The predecessor had no equivalent — it showed the Windows volume slider, which reports what
/// the user set, not what is being captured. The two differ in exactly the case that matters:
/// audio routed to a device that is not being recorded shows full volume and captures silence.
/// </para>
/// </remarks>
public sealed class AudioLevelMeter
{
    /// <summary>
    /// Decodes one sample. A named delegate rather than a <see cref="Func{T, TResult}"/> because
    /// <see cref="ReadOnlySpan{T}"/> is a ref struct and cannot be a generic argument.
    /// </summary>
    private delegate float SampleReader(ReadOnlySpan<byte> bytes);

    private readonly SampleReader? _read;

    private float _peak;

    public AudioLevelMeter(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        Format = format;
        _read = SampleReaderFor(format);
        IsSupported = _read is not null;
    }

    /// <summary>The format samples arrive in.</summary>
    public WaveFormat Format { get; }

    /// <summary>Whether this meter can read <see cref="Format"/>; a false reads as silence.</summary>
    public bool IsSupported { get; }

    /// <summary>
    /// Folds a captured buffer into the running peak. Called on the capture thread.
    /// </summary>
    /// <param name="data">Captured PCM, in <see cref="Format"/>.</param>
    public void Write(ReadOnlySpan<byte> data)
    {
        var peak = PeakOf(data);
        if (peak <= 0f) return;

        // Lock-free max. Contention is between one capture thread and one reader, and the
        // reader only ever writes zero, so this settles in a pass or two.
        var current = Volatile.Read(ref _peak);

        while (peak > current)
        {
            var observed = Interlocked.CompareExchange(ref _peak, peak, current);
            if (observed.Equals(current)) return;

            current = observed;
        }
    }

    /// <summary>
    /// Takes the loudest sample since the last read, as 0–1, and starts a new interval.
    /// </summary>
    public float Read() => Interlocked.Exchange(ref _peak, 0f);

    /// <summary>Drops the current interval, for the end of a session.</summary>
    public void Reset() => Interlocked.Exchange(ref _peak, 0f);

    /// <summary>The loudest absolute sample in <paramref name="data"/>, as 0–1.</summary>
    private float PeakOf(ReadOnlySpan<byte> data)
    {
        if (_read is null || data.IsEmpty) return 0f;

        var stride = Format.BitsPerSample / 8;
        if (stride <= 0) return 0f;

        var peak = 0f;

        // Whole samples only: a callback can end mid-sample, and a partial one read as a whole
        // is a spike the audio never contained.
        for (var offset = 0; offset + stride <= data.Length; offset += stride)
        {
            var sample = Math.Abs(_read(data.Slice(offset, stride)));
            if (sample > peak) peak = sample;
        }

        return Math.Min(peak, 1f);
    }

    /// <summary>
    /// Picks the decoder for a format, or null when there is none.
    /// </summary>
    /// <remarks>
    /// WASAPI's shared-mode mix format is 32-bit float in practice, which is the first case.
    /// The integer cases are here because an exclusive-mode or resampled source can be PCM, and
    /// because <see cref="IsSupported"/> is worth being honest about.
    /// </remarks>
    private static SampleReader? SampleReaderFor(WaveFormat format) =>
        (EffectiveEncoding(format), format.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => static bytes => MemoryMarshal.Read<float>(bytes),
            (WaveFormatEncoding.Pcm, 16) => static bytes =>
                BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
            (WaveFormatEncoding.Pcm, 24) => static bytes => Read24BitLittleEndian(bytes) / 8388608f,
            (WaveFormatEncoding.Pcm, 32) => static bytes =>
                BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
            _ => null,
        };

    /// <summary>
    /// The encoding to decode by, resolving <c>Extensible</c> to what it actually wraps.
    /// </summary>
    /// <remarks>
    /// A WASAPI mix format is usually <see cref="WaveFormatExtensible"/>, whose
    /// <see cref="WaveFormat.Encoding"/> reports <c>Extensible</c> rather than the sub-format
    /// underneath. Reading that value directly is how a perfectly ordinary float stream ends up
    /// classified as unsupported.
    /// </remarks>
    private static WaveFormatEncoding EffectiveEncoding(WaveFormat format) =>
        format is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat().Encoding
            : format.Encoding;

    /// <summary>Reads a 24-bit little-endian signed sample, sign-extended into an int.</summary>
    private static int Read24BitLittleEndian(ReadOnlySpan<byte> bytes) =>
        (bytes[2] << 24 | bytes[1] << 16 | bytes[0] << 8) >> 8;
}
