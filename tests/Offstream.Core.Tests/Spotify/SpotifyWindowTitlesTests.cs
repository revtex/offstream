using Offstream.Core.Spotify;
using Xunit;

namespace Offstream.Core.Tests.Spotify;

/// <summary>
/// Ported from the reference suite's idle/advertisement title checks.
/// </summary>
/// <remarks>
/// The original used the predecessor's own product-name constant as its negative case.
/// That identifier cannot exist here (plan §0), so the negative case is a real track title,
/// which tests the same thing more directly: an ordinary title is not an idle state.
/// </remarks>
public sealed class SpotifyWindowTitlesTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(SpotifyWindowTitles.Spotify, true)]
    [InlineData(SpotifyWindowTitles.SpotifyFree, true)]
    [InlineData(SpotifyWindowTitles.SpotifyPremium, true)]
    [InlineData(SpotifyWindowTitles.Advertisement, true)]
    [InlineData("Fleetwood Mac - Dreams", false)]
    public void IsNullOrAdOrIdle_ReturnsExpected(string? value, bool expected) =>
        Assert.Equal(expected, value.IsNullOrAdOrIdle());

    [Theory]
    [InlineData(SpotifyWindowTitles.Advertisement, true)]
    [InlineData("advertisement", true)]
    [InlineData("ADVERTISEMENT", true)]
    [InlineData(SpotifyWindowTitles.Spotify, false)]
    [InlineData("Fleetwood Mac - Dreams", false)]
    public void IsAdvertisement_IsCaseInsensitive(string value, bool expected) =>
        Assert.Equal(expected, value.IsAdvertisement());

    [Theory]
    [InlineData(SpotifyWindowTitles.Spotify, true)]
    [InlineData("spotify free", true)]
    [InlineData("SPOTIFY PREMIUM", true)]
    [InlineData(SpotifyWindowTitles.Advertisement, false)]
    [InlineData("Fleetwood Mac - Dreams", false)]
    public void IsIdle_IsCaseInsensitive(string value, bool expected) =>
        Assert.Equal(expected, value.IsIdle());

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(SpotifyWindowTitles.Spotify, true)]
    [InlineData(SpotifyWindowTitles.Advertisement, false)]
    public void IsNullOrIdle_TreatsAdvertisementAsNotIdle(string? value, bool expected) =>
        Assert.Equal(expected, value.IsNullOrIdle());
}
