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

    /// <summary>
    /// Sample magnitude counted as clipping, as a fraction of full scale.
    /// </summary>
    /// <remarks>
    /// Just under 1 rather than at it: a converter that has run out of headroom pins samples to
    /// the largest value the format can hold, and for 16-bit that is 32767/32768 — never quite
    /// 1.0 once normalised. Testing for equality with full scale would miss every clipped
    /// integer recording, which is most of them.
    /// </remarks>
    private const float ClipMagnitude = 0.999f;

    /// <summary>
    /// Sample magnitude below which an interval counts as silent.
    /// </summary>
    /// <remarks>
    /// −80&#160;dBFS, two decades below the meter's own floor. Digital silence is exact zeroes
    /// and would justify testing for zero, but a capture graph with any analogue stage in it
    /// idles at a dither floor instead, and calling that "not silent" would make the warning
    /// this feeds useless on exactly the hardware that needs it.
    /// </remarks>
    private const float SilenceMagnitude = 0.0001f;

    private readonly SampleReader? _read;
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;

    private readonly double[] _sumOfSquares;
    private readonly long[] _samples;

    private long _lastSoundAt;
    private bool _clipped;

    public AudioLevelMeter(WaveFormat format, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(format);

        Format = format;
        _time = time ?? TimeProvider.System;
        _lastSoundAt = _time.GetTimestamp();
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
        var peak = 0f;

        lock (_gate)
        {
            var channel = 0;

            // Whole samples only: a callback can end mid-sample, and a partial one read as a
            // whole is a spike the audio never contained. Buffers arrive frame-aligned, so
            // starting at channel zero each time keeps left and right from swapping over.
            for (var offset = 0; offset + stride <= data.Length; offset += stride)
            {
                float sample = _read(data.Slice(offset, stride));

                _sumOfSquares[channel] += (double)sample * sample;
                _samples[channel]++;

                var magnitude = Math.Abs(sample);
                if (magnitude > peak) peak = magnitude;

                if (++channel == ChannelCount) channel = 0;
            }

            // Both flags are folded in here, on the capture thread, and neither is disturbed by
            // Read. They have to be: Read drains the interval it reports, so a second consumer
            // calling it to look for silence or clipping would take samples away from the meter
            // and leave the bars reporting a fraction of the audio.
            if (peak >= ClipMagnitude) _clipped = true;

            // One clock read per buffer rather than per sample. Write runs about a hundred times
            // a second with a few thousand samples in each call.
            if (peak > SilenceMagnitude) _lastSoundAt = _time.GetTimestamp();
        }
    }

    /// <summary>
    /// Whether any sample since the last <see cref="ResetClip"/> reached full scale.
    /// </summary>
    /// <remarks>
    /// <b>Latched, and taken from the peak sample rather than from a reading.</b> The readings
    /// this meter publishes are RMS, which for real music sits ten to twenty decibels under the
    /// peak and so never reaches full scale even while the converter is clipping — a clip lamp
    /// driven from <see cref="LevelReading.Decibels"/> would simply never light. Latching is the
    /// point as well: clipping is a handful of samples, gone long before anyone looks at the
    /// screen, and the damage it does is permanent once encoded.
    /// </remarks>
    public bool HasClipped
    {
        get { lock (_gate) return _clipped; }
    }

    /// <summary>Clears <see cref="HasClipped"/>, for the start of a new track.</summary>
    public void ResetClip()
    {
        lock (_gate) _clipped = false;
    }

    /// <summary>
    /// How long it has been since audio above <see cref="SilenceMagnitude"/> arrived.
    /// </summary>
    /// <remarks>
    /// This is the meter's answer to the failure named in the class remarks — audio routed to a
    /// device that is not being captured shows full volume in Windows and records nothing. The
    /// bars already report it, by sitting flat; this reports it in a form that can be counted and
    /// put into words, which is what makes it survive the user looking away.
    /// </remarks>
    public TimeSpan SilentFor
    {
        get
        {
            long last;
            lock (_gate) last = _lastSoundAt;

            return _time.GetElapsedTime(last);
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

            _clipped = false;
            _lastSoundAt = _time.GetTimestamp();
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
