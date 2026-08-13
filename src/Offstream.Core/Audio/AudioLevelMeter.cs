using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Offstream.Core.Audio;

/// <summary>
/// Tracks how loud the audio has been since it was last read, for a level display.
/// </summary>
/// <remarks>
/// <para>
/// <b>Accumulate-and-drain, rather than an event per callback.</b> Capture fires roughly every
/// 10 ms; a meter that raised an event each time would push 100 dispatcher marshals a second at
/// the UI for a bar that can only redraw at 60 Hz. Instead the capture thread folds each buffer
/// into a running energy total with no allocation, and whoever is drawing calls
/// <see cref="Read()"/> at its own rate. The total resets on read, so a slow reader sees the
/// loudness of its whole interval rather than whatever happened to be playing at the instant it
/// looked.
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

    /// <summary>
    /// Quietest level the display distinguishes, in dBFS. Below this reads as silence.
    /// </summary>
    /// <remarks>
    /// 60&#160;dB is the usual span for a music meter: it puts a quiet passage near the bottom
    /// and a loud one near the top, which is the range the eye can actually read.
    /// </remarks>
    private const double FloorDecibels = -60;

    private readonly SampleReader? _read;
    private readonly Lock _gate = new();

    private readonly double[] _sumOfSquares;
    private readonly long[] _samples;

    public AudioLevelMeter(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        Format = format;
        _read = SampleReaderFor(format);
        IsSupported = _read is not null;

        ChannelCount = Math.Max(format.Channels, 1);
        _sumOfSquares = new double[ChannelCount];
        _samples = new long[ChannelCount];
    }

    /// <summary>The format samples arrive in.</summary>
    public WaveFormat Format { get; }

    /// <summary>How many channels <see cref="Read(Span{LevelReading})"/> can fill.</summary>
    public int ChannelCount { get; }

    /// <summary>Whether this meter can read <see cref="Format"/>; a false reads as silence.</summary>
    public bool IsSupported { get; }

    /// <summary>
    /// Folds a captured buffer into the running per-channel totals. Called on the capture thread.
    /// </summary>
    /// <param name="data">Captured PCM, in <see cref="Format"/>.</param>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (_read is null || data.IsEmpty) return;

        var stride = Format.BitsPerSample / 8;
        if (stride <= 0) return;

        // One capture thread and one reader, and the critical section is a handful of additions —
        // the hundred acquisitions a second this takes are far cheaper than the lock-free dance a
        // running sum would otherwise need.
        lock (_gate)
        {
            var channel = 0;

            // Whole samples only: a callback can end mid-sample, and a partial one read as a
            // whole is a spike the audio never contained. Buffers arrive frame-aligned, so
            // starting at channel zero each time keeps left and right from swapping over.
            for (var offset = 0; offset + stride <= data.Length; offset += stride)
            {
                double sample = _read(data.Slice(offset, stride));

                _sumOfSquares[channel] += sample * sample;
                _samples[channel]++;

                if (++channel == ChannelCount) channel = 0;
            }
        }
    }

    /// <summary>
    /// Takes the loudness of the interval just ended, across all channels, and starts a new one.
    /// </summary>
    /// <remarks>
    /// <b>RMS on a decibel scale, not the peak sample.</b> The peak over an interval this short
    /// is at or near full scale for essentially all mastered music, so a peak meter drew a solid
    /// block that said only "audio is arriving" — a display with no information in it. RMS is
    /// what loudness actually tracks, and mapping it through <see cref="FloorDecibels"/> spreads
    /// the range a listener cares about across the height available instead of crowding it into
    /// the top tenth, which is what a linear amplitude scale does.
    /// </remarks>
    public LevelReading Read() => Read(default);

    /// <summary>
    /// Takes the loudness of the interval just ended and starts a new one, filling
    /// <paramref name="channels"/> with the per-channel readings.
    /// </summary>
    /// <param name="channels">
    /// Receives one reading per channel, up to its own length and
    /// <see cref="ChannelCount"/>. Pass an empty span for the combined figure alone — the
    /// interval is drained either way, so this must not be called twice for one reading.
    /// </param>
    /// <returns>The reading across all channels together.</returns>
    public LevelReading Read(Span<LevelReading> channels)
    {
        Span<double> sums = stackalloc double[ChannelCount];
        Span<long> counts = stackalloc long[ChannelCount];

        lock (_gate)
        {
            _sumOfSquares.CopyTo(sums);
            _samples.CopyTo(counts);

            Array.Clear(_sumOfSquares);
            Array.Clear(_samples);
        }

        var totalSum = 0d;
        var totalCount = 0L;

        for (var channel = 0; channel < ChannelCount; channel++)
        {
            totalSum += sums[channel];
            totalCount += counts[channel];

            if (channel < channels.Length) channels[channel] = ReadingFor(sums[channel], counts[channel]);
        }

        return ReadingFor(totalSum, totalCount);
    }

    /// <summary>Drops the current interval, for the end of a session.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_sumOfSquares);
            Array.Clear(_samples);
        }
    }

    /// <summary>Turns accumulated energy into a reading.</summary>
    private static LevelReading ReadingFor(double sumOfSquares, long samples)
    {
        if (samples == 0) return LevelReading.Silent;

        var rms = Math.Sqrt(sumOfSquares / samples);
        if (rms <= 0) return LevelReading.Silent;

        var decibels = 20 * Math.Log10(rms);

        return new LevelReading(
            (float)Math.Clamp((decibels - FloorDecibels) / -FloorDecibels, 0, 1),
            decibels);
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
