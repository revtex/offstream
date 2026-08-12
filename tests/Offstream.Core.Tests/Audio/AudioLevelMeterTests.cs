using NAudio.Wave;
using Offstream.Core.Audio;
using Xunit;

namespace Offstream.Core.Tests.Audio;

/// <summary>
/// Peak extraction across the sample formats capture can deliver.
/// </summary>
/// <remarks>
/// Worth testing rather than eyeballing: every case here is a different byte layout, and the
/// failure mode of getting one wrong is a meter that looks plausible — moving, roughly in time
/// with the music — while being wrong by a factor of two or stuck near zero.
/// </remarks>
public sealed class AudioLevelMeterTests
{
    private static WaveFormat Float32 => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private static WaveFormat Pcm16 => new(44100, 16, 2);

    [Fact]
    public void Read_WithNothingWritten_IsSilent() =>
        Assert.Equal(0f, new AudioLevelMeter(Float32).Read());

    [Fact]
    public void Write_Float32FullScale_ReadsOne()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write(BitConverter.GetBytes(1.0f));

        Assert.Equal(1f, meter.Read(), precision: 5);
    }

    [Fact]
    public void Write_Float32Negative_CountsMagnitude()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write(BitConverter.GetBytes(-0.75f));

        Assert.Equal(0.75f, meter.Read(), precision: 5);
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

        Assert.Equal(1f, meter.Read(), precision: 5);
    }

    [Fact]
    public void Write_Pcm16HalfScale_ReadsHalf()
    {
        var meter = new AudioLevelMeter(Pcm16);

        meter.Write(BitConverter.GetBytes((short)16384));

        Assert.Equal(0.5f, meter.Read(), precision: 4);
    }

    [Fact]
    public void Write_Pcm24HalfScale_ReadsHalf()
    {
        var meter = new AudioLevelMeter(new WaveFormat(44100, 24, 2));

        // 0x400000 is half of 24-bit full scale, little-endian.
        meter.Write([0x00, 0x00, 0x40]);

        Assert.Equal(0.5f, meter.Read(), precision: 4);
    }

    [Fact]
    public void Write_Pcm24Negative_CountsMagnitude()
    {
        var meter = new AudioLevelMeter(new WaveFormat(44100, 24, 2));

        // 0xC00000 is -half of 24-bit full scale: the sign extension is the part worth pinning.
        meter.Write([0x00, 0x00, 0xC0]);

        Assert.Equal(0.5f, meter.Read(), precision: 4);
    }

    [Fact]
    public void Write_Pcm32HalfScale_ReadsHalf()
    {
        var meter = new AudioLevelMeter(new WaveFormat(44100, 32, 2));

        meter.Write(BitConverter.GetBytes(1 << 30));

        Assert.Equal(0.5f, meter.Read(), precision: 4);
    }

    /// <summary>The point of the interval: a slow reader still sees the transient.</summary>
    [Fact]
    public void Read_ReturnsTheLoudestSampleSinceTheLastRead()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write(BitConverter.GetBytes(0.1f));
        meter.Write(BitConverter.GetBytes(0.9f));
        meter.Write(BitConverter.GetBytes(0.2f));

        Assert.Equal(0.9f, meter.Read(), precision: 5);
    }

    [Fact]
    public void Read_StartsANewInterval()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write(BitConverter.GetBytes(0.9f));
        meter.Read();

        // Without the reset the bar would never fall: every later read would return the loudest
        // moment of the whole session.
        Assert.Equal(0f, meter.Read());
    }

    [Fact]
    public void Reset_DropsTheCurrentInterval()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write(BitConverter.GetBytes(0.9f));
        meter.Reset();

        Assert.Equal(0f, meter.Read());
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

        Assert.Equal(0.8f, meter.Read(), precision: 5);
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

        Assert.Equal(0.25f, meter.Read(), precision: 5);
    }

    [Fact]
    public void Write_EmptyBuffer_IsSilent()
    {
        var meter = new AudioLevelMeter(Float32);

        meter.Write([]);

        Assert.Equal(0f, meter.Read());
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

        Assert.Equal(0.5f, meter.Read(), precision: 5);
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

        Assert.Equal(0f, meter.Read());
    }

    [Fact]
    public void Constructor_RejectsANullFormat() =>
        Assert.Throws<ArgumentNullException>(() => new AudioLevelMeter(null!));
}
