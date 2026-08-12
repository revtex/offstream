using NAudio.Wave;
using Offstream.Core.Audio;
using Offstream.Core.Recording;
using Xunit;

namespace Offstream.Core.Tests.Audio;

/// <summary>Ported from the reference suite's <c>WaveFormatExtensionsTest</c>.</summary>
public sealed class WaveFormatExtensionsTests
{
    [Fact]
    public void ReportsChannelRestrictionAboveStereo()
    {
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(WaveFormatExtensions.Mp3MaxSampleRate, 6);

        Assert.Contains(Mp3Restriction.Channel, waveFormat.GetMp3Restrictions());
    }

    [Fact]
    public void ReportsSampleRateRestrictionAbove48k()
    {
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(96000, WaveFormatExtensions.Mp3MaxChannels);

        Assert.Contains(Mp3Restriction.SampleRate, waveFormat.GetMp3Restrictions());
    }

    [Fact]
    public void ReportsBothRestrictionsWhenBothExceeded()
    {
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(96000, 6);

        Assert.Equal([Mp3Restriction.Channel, Mp3Restriction.SampleRate], waveFormat.GetMp3Restrictions());
    }

    [Fact]
    public void ReportsNoRestrictionsWithinLimits()
    {
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            WaveFormatExtensions.Mp3MaxSampleRate, WaveFormatExtensions.Mp3MaxChannels);

        Assert.Empty(waveFormat.GetMp3Restrictions());
    }
}
