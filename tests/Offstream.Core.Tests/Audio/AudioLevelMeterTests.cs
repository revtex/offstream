using NAudio.Wave;
using Offstream.Core.Audio;
using Xunit;

namespace Offstream.Core.Tests.Audio;

/// <summary>
/// Loudness extraction across the sample formats capture can deliver.
/// </summary>
/// <remarks>
/// <para>
/// Worth testing rather than eyeballing: every case here is a different byte layout, and the
/// failure mode of getting one wrong is a meter that looks plausible — moving, roughly in time
/// with the music — while being wrong by a factor of two or stuck near zero.
/// </para>
/// <para>
/// Expectations run through <see cref="ExpectedLevel"/> rather than being written out, so each
/// case still says what amplitude the bytes decode to. The meter reports RMS on a decibel
/// scale, because a peak over a display interval is at full scale for almost all music.
/// </para>
/// </remarks>
public sealed class AudioLevelMeterTests
{
    private static WaveFormat Float32 => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private static WaveFormat Pcm16 => new(44100, 16, 2);

    /// <summary>What the meter should report for a buffer of these sample amplitudes.</summary>
    private static float ExpectedLevel(params double[] samples)
    {
        var rms = Math.Sqrt(samples.Sum(sample => sample * sample) / samples.Length);

        return (float)Math.Clamp((20 * Math.Log10(rms) + 60) / 60, 0, 1);
    }

    [Fact]
    public void Read_WithNothingWritten_IsSilent() =>
        Assert.Equal(0f, new AudioLevelMeter(Float32).Read().Level);

    [Fact]
    public void Write_Float32FullScale_ReadsOne()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write(BitConverter.GetBytes(1.0f));

        Assert.Equal(1f, meter.Read().Level, precision: 5);
    }

    [Fact]
    public void Write_Float32Negative_CountsMagnitude()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write(BitConverter.GetBytes(-0.75f));

        Assert.Equal(ExpectedLevel(0.75), meter.Read().Level, precision: 5);
    }

    /// <summary>
    /// Float samples can exceed unity — a mix bus is allowed to clip internally — and a meter
    /// that passes that through draws outside its own bar.
    /// </summary>
    [Fact]
    public void Write_Float32AboveUnity_ClampsToOne()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write(BitConverter.GetBytes(2.5f));

        Assert.Equal(1f, meter.Read().Level, precision: 5);
    }

    [Fact]
    public void Write_Pcm16HalfScale_ReadsHalf()
    {
        var meter = new AudioLevelMeter(Pcm16);

        meter.Write(BitConverter.GetBytes((short)16384));

        Assert.Equal(ExpectedLevel(0.5), meter.Read().Level, precision: 4);
    }

    [Fact]
    public void Write_Pcm24HalfScale_ReadsHalf()
    {
        var meter = new AudioLevelMeter(new WaveFormat(44100, 24, 2));

        // 0x400000 is half of 24-bit full scale, little-endian.
        meter.Write([0x00, 0x00, 0x40]);

        Assert.Equal(ExpectedLevel(0.5), meter.Read().Level, precision: 4);
    }

    [Fact]
    public void Write_Pcm24Negative_CountsMagnitude()
    {
        var meter = new AudioLevelMeter(new WaveFormat(44100, 24, 2));

        // 0xC00000 is -half of 24-bit full scale: the sign extension is the part worth pinning.
        meter.Write([0x00, 0x00, 0xC0]);

        Assert.Equal(ExpectedLevel(0.5), meter.Read().Level, precision: 4);
    }

    [Fact]
    public void Write_Pcm32HalfScale_ReadsHalf()
    {
        var meter = new AudioLevelMeter(new WaveFormat(44100, 32, 2));

        meter.Write(BitConverter.GetBytes(1 << 30));

        Assert.Equal(ExpectedLevel(0.5), meter.Read().Level, precision: 4);
    }

    /// <summary>The point of the interval: one read covers everything written since the last.</summary>
    [Fact]
    public void Read_CoversEverySampleWrittenSinceTheLastRead()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write(BitConverter.GetBytes(0.1f));
        meter.Write(BitConverter.GetBytes(0.9f));
        meter.Write(BitConverter.GetBytes(0.2f));

        Assert.Equal(ExpectedLevel(0.1, 0.9, 0.2), meter.Read().Level, precision: 5);
    }

    [Fact]
    public void Read_StartsANewInterval()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write(BitConverter.GetBytes(0.9f));
        meter.Read();

        // Without the reset the bar would never fall: every later read would return the loudest
        // moment of the whole session.
        Assert.Equal(0f, meter.Read().Level);
    }

    [Fact]
    public void Reset_DropsTheCurrentInterval()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write(BitConverter.GetBytes(0.9f));
        meter.Reset();

        Assert.Equal(0f, meter.Read().Level);
    }

    [Fact]
    public void Write_ScansEverySampleInABuffer()
    {
        var meter = new AudioLevelMeter(Float32);
        var buffer = new byte[4 * 4];

        BitConverter.GetBytes(0.2f).CopyTo(buffer, 0);
        BitConverter.GetBytes(0.4f).CopyTo(buffer, 4);
        BitConverter.GetBytes(0.8f).CopyTo(buffer, 8);
        BitConverter.GetBytes(0.3f).CopyTo(buffer, 12);

        meter.Write(buffer);

        Assert.Equal(ExpectedLevel(0.2, 0.4, 0.8, 0.3), meter.Read().Level, precision: 5);
    }

    /// <summary>
    /// A capture callback can end mid-sample. Reading the leftover bytes as a whole sample
    /// invents a value the audio never contained — usually a loud one.
    /// </summary>
    [Fact]
    public void Write_IgnoresATrailingPartialSample()
    {
        var meter = new AudioLevelMeter(Float32);
        var buffer = new byte[6];

        BitConverter.GetBytes(0.25f).CopyTo(buffer, 0);
        buffer[4] = 0xFF;
        buffer[5] = 0xFF;

        meter.Write(buffer);

        // Only the whole sample counts; the two stray bytes are not a second one.
        Assert.Equal(ExpectedLevel(0.25), meter.Read().Level, precision: 5);
    }

    [Fact]
    public void Write_EmptyBuffer_IsSilent()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write([]);

        Assert.Equal(0f, meter.Read().Level);
    }

    /// <summary>
    /// A WASAPI mix format arrives as <see cref="WaveFormatExtensible"/>, whose reported encoding
    /// is <c>Extensible</c> rather than the float it actually wraps. Reading that value directly
    /// classifies an ordinary capture stream as unsupported and the meter never moves.
    /// </summary>
    [Fact]
    public void Extensible_ResolvesToTheFormatItWraps()
    {
        var meter = new AudioLevelMeter(new WaveFormatExtensible(48000, 32, 2));

        Assert.True(meter.IsSupported);

        meter.Write(BitConverter.GetBytes(0.5f));

        Assert.Equal(ExpectedLevel(0.5), meter.Read().Level, precision: 5);
    }

    /// <summary>
    /// The meter drives a decoration. A format it cannot read must cost the display, never the
    /// recording, so it reads as silence instead of throwing.
    /// </summary>
    [Fact]
    public void UnsupportedFormat_ReadsAsSilenceRatherThanThrowing()
    {
        var meter = new AudioLevelMeter(new WaveFormat(8000, 8, 1));

        Assert.False(meter.IsSupported);

        meter.Write([0xFF, 0x7F, 0x00, 0x80]);

        Assert.Equal(0f, meter.Read().Level);
    }

    /// <summary>
    /// Below the floor the bar sits at nothing rather than at a sliver that never settles.
    /// </summary>
    [Fact]
    public void Write_BelowTheDecibelFloor_ReadsAsSilence()
    {
        var meter = new AudioLevelMeter(Float32);

        // -80 dBFS, two decades below the floor the scale starts at.
        meter.Write(BitConverter.GetBytes(0.0001f));

        Assert.Equal(0f, meter.Read().Level);
    }

    /// <summary>
    /// The change that made the meter worth looking at: a loud passage and a very loud one have
    /// to be different heights. On the old peak scale both pinned to the top of the control.
    /// </summary>
    [Fact]
    public void Read_SeparatesLoudFromVeryLoud()
    {
        var quieter = new AudioLevelMeter(Float32);
        var louder = new AudioLevelMeter(Float32);

        quieter.Write(BitConverter.GetBytes(0.25f));
        louder.Write(BitConverter.GetBytes(0.9f));

        var quietLevel = quieter.Read().Level;
        var loudLevel = louder.Read().Level;

        Assert.True(
            loudLevel - quietLevel > 0.15f,
            $"levels {quietLevel} and {loudLevel} to be visibly apart");
    }

    /// <summary>
    /// Interleaved samples have to land in the channel they belong to. Getting the stride wrong
    /// gives two bars that move together and mean nothing — the failure looks like a working
    /// meter, which is why it is pinned rather than eyeballed.
    /// </summary>
    [Fact]
    public void Read_SplitsInterleavedSamplesByChannel()
    {
        var meter = new AudioLevelMeter(Float32);
        var buffer = new byte[4 * 4];

        // L R L R: left is loud, right is quiet.
        BitConverter.GetBytes(0.8f).CopyTo(buffer, 0);
        BitConverter.GetBytes(0.1f).CopyTo(buffer, 4);
        BitConverter.GetBytes(0.8f).CopyTo(buffer, 8);
        BitConverter.GetBytes(0.1f).CopyTo(buffer, 12);

        meter.Write(buffer);

        Span<LevelReading> channels = stackalloc LevelReading[2];
        var combined = meter.Read(channels);

        Assert.Equal(ExpectedLevel(0.8), channels[0].Level, precision: 5);
        Assert.Equal(ExpectedLevel(0.1), channels[1].Level, precision: 5);
        Assert.Equal(ExpectedLevel(0.8, 0.1, 0.8, 0.1), combined.Level, precision: 5);
    }

    /// <summary>
    /// Buffers arrive frame-aligned, so every write starts on the left. Carrying a channel index
    /// across writes would swap the two bars over on any buffer with an odd sample count.
    /// </summary>
    [Fact]
    public void Read_KeepsChannelsInPlaceAcrossWrites()
    {
        var meter = new AudioLevelMeter(Float32);

        for (var frame = 0; frame < 3; frame++)
        {
            var buffer = new byte[8];
            BitConverter.GetBytes(0.8f).CopyTo(buffer, 0);
            BitConverter.GetBytes(0.1f).CopyTo(buffer, 4);
            meter.Write(buffer);
        }

        Span<LevelReading> channels = stackalloc LevelReading[2];
        meter.Read(channels);

        Assert.Equal(ExpectedLevel(0.8), channels[0].Level, precision: 5);
        Assert.Equal(ExpectedLevel(0.1), channels[1].Level, precision: 5);
    }

    /// <summary>
    /// A ruler printed in decibels needs the decibel figure the reading was built from, not one
    /// recovered from a clamped 0–1 level.
    /// </summary>
    [Fact]
    public void Read_ReportsDecibelsAlongsideTheLevel()
    {
        var meter = new AudioLevelMeter(Float32);

        // Half scale is -6.02 dBFS.
        meter.Write(BitConverter.GetBytes(0.5f));

        Assert.Equal(-6.0206, meter.Read().Decibels, precision: 3);
    }

    [Fact]
    public void Read_WithNothingWritten_ReportsSilence()
    {
        var reading = new AudioLevelMeter(Float32).Read();

        Assert.Equal(LevelReading.Silent, reading);
        Assert.Equal(double.NegativeInfinity, reading.Decibels);
    }

    /// <summary>A span shorter than the format's channel count fills what it can.</summary>
    [Fact]
    public void Read_WithAShortSpan_FillsWhatFits()
    {
        var meter = new AudioLevelMeter(Float32);
        var buffer = new byte[8];

        BitConverter.GetBytes(0.8f).CopyTo(buffer, 0);
        BitConverter.GetBytes(0.1f).CopyTo(buffer, 4);
        meter.Write(buffer);

        Span<LevelReading> channels = stackalloc LevelReading[1];
        meter.Read(channels);

        Assert.Equal(ExpectedLevel(0.8), channels[0].Level, precision: 5);
    }

    [Fact]
    public void ChannelCount_FollowsTheFormat()
    {
        Assert.Equal(2, new AudioLevelMeter(Float32).ChannelCount);
        Assert.Equal(1, new AudioLevelMeter(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)).ChannelCount);
    }

    [Fact]
    public void Constructor_RejectsANullFormat() =>
        Assert.Throws<ArgumentNullException>(() => new AudioLevelMeter(null!));

    /// <summary>
    /// Drives <see cref="AudioLevelMeter.SilentFor"/> without waiting. Frequency of 1,000 makes
    /// one tick a millisecond, so the arithmetic in the tests reads as time.
    /// </summary>
    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => 1_000;

        public void Advance(TimeSpan amount) => _timestamp += (long)(amount.TotalSeconds * TimestampFrequency);
    }

    /// <summary>
    /// The clip flag has to come off the peak sample. Every reading this meter publishes is RMS,
    /// which for real music sits well under the peak — so a lamp driven from a reading would stay
    /// dark through a recording that is audibly clipped.
    /// </summary>
    [Fact]
    public void HasClipped_IsSetByAPeakTheRmsReadingNeverShows()
    {
        var meter = new AudioLevelMeter(Float32);

        // One sample at full scale among a run of quiet ones: clipping as it actually arrives.
        meter.Write(BitConverter.GetBytes(1.0f));
        for (var i = 0; i < 200; i++) meter.Write(BitConverter.GetBytes(0.02f));

        Assert.True(meter.HasClipped);
        Assert.True(meter.Read().Decibels < -20, "the RMS reading stays far below full scale");
    }

    /// <summary>
    /// A converter out of headroom pins to the largest value the format holds, which for 16-bit
    /// is 32767/32768 — never exactly full scale once normalised.
    /// </summary>
    [Fact]
    public void HasClipped_CatchesAPinnedIntegerSample()
    {
        var meter = new AudioLevelMeter(Pcm16);

        meter.Write(BitConverter.GetBytes(short.MaxValue));

        Assert.True(meter.HasClipped);
    }

    [Fact]
    public void HasClipped_IsNotSetByLoudAudioShortOfFullScale()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write(BitConverter.GetBytes(0.9f));

        Assert.False(meter.HasClipped);
    }

    /// <summary>Reading the meter must not clear the latch — the two are independent.</summary>
    [Fact]
    public void HasClipped_SurvivesARead()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write(BitConverter.GetBytes(1.0f));
        meter.Read();

        Assert.True(meter.HasClipped);

        meter.ResetClip();

        Assert.False(meter.HasClipped);
    }

    [Fact]
    public void SilentFor_GrowsWhileNothingArrives()
    {
        var clock = new TestClock();
        var meter = new AudioLevelMeter(Float32, clock);

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), meter.SilentFor);
    }

    [Fact]
    public void SilentFor_IsResetByAudio()
    {
        var clock = new TestClock();
        var meter = new AudioLevelMeter(Float32, clock);

        clock.Advance(TimeSpan.FromSeconds(30));
        meter.Write(BitConverter.GetBytes(0.5f));

        Assert.Equal(TimeSpan.Zero, meter.SilentFor);
    }

    /// <summary>
    /// A dither floor is not audio. The threshold sits at −80 dBFS so that a capture graph idling
    /// on noise still counts as silent — which is the case the warning exists for.
    /// </summary>
    [Fact]
    public void SilentFor_TreatsADitherFloorAsSilence()
    {
        var clock = new TestClock();
        var meter = new AudioLevelMeter(Float32, clock);

        clock.Advance(TimeSpan.FromSeconds(10));
        meter.Write(BitConverter.GetBytes(0.00001f));

        Assert.Equal(TimeSpan.FromSeconds(10), meter.SilentFor);
    }
}
