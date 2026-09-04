using Offstream.Core.Metadata.Library;
using Xunit;

namespace Offstream.Core.Tests.Metadata.Library;

/// <summary>The tier a badge would show, from nothing but the numbers a container reports.</summary>
public sealed class AudioQualityTests
{
    /// <summary>No sample rate at all means the file said nothing worth showing.</summary>
    [Fact]
    public void Tier_OfTheUnknownValueIsUnknown() =>
        Assert.Equal(AudioQualityTier.Unknown, AudioQuality.Unknown.Tier);

    /// <summary>A bit depth is what marks a codec lossless, whatever its bitrate happens to be.</summary>
    [Fact]
    public void Tier_OfAnythingWithABitDepthIsLossless() =>
        Assert.Equal(AudioQualityTier.Lossless, new AudioQuality(BitrateKbps: 1411, SampleRateHz: 44100, BitsPerSample: 16).Tier);

    [Theory]
    [InlineData(96, AudioQualityTier.Low)]
    [InlineData(127, AudioQualityTier.Low)]
    [InlineData(128, AudioQualityTier.Medium)]
    [InlineData(255, AudioQualityTier.Medium)]
    [InlineData(256, AudioQualityTier.High)]
    [InlineData(320, AudioQualityTier.High)]
    public void Tier_OfALossyBitrateFollowsTheThresholds(int bitrateKbps, AudioQualityTier expected) =>
        Assert.Equal(expected, new AudioQuality(bitrateKbps, SampleRateHz: 44100, BitsPerSample: 0).Tier);

    /// <summary>A known sample rate with no bitrate at all still resolves rather than throwing.</summary>
    [Fact]
    public void Tier_OfAKnownFileWithNoBitrateIsLow() =>
        Assert.Equal(AudioQualityTier.Low, new AudioQuality(BitrateKbps: null, SampleRateHz: 44100, BitsPerSample: 0).Tier);
}
