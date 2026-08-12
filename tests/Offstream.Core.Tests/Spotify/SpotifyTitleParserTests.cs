using Offstream.Core.Metadata;
using Offstream.Core.Spotify;
using Xunit;

namespace Offstream.Core.Tests.Spotify;

/// <summary>
/// Ported from the reference suite's <c>SpotifyStatusTests</c>, assertions unchanged.
/// </summary>
public sealed class SpotifyTitleParserTests
{
    [Fact]
    public void StandingBy_ReturnsIdleTrack()
    {
        var expected = new Track { Artist = SpotifyWindowTitles.Spotify };

        var track = SpotifyTitleParser.Parse(new SpotifyWindow(SpotifyWindowTitles.Spotify, IsPlaying: false));

        Assert.Equal(expected, track);
        Assert.Equal(SpotifyWindowTitles.Spotify, track.ToString());
    }

    [Theory]
    [InlineData("Artist Name - Song Title", false)]
    [InlineData("Artist Name - Song Title - Live", true)]
    public void TrackPlaying_ReturnsExpectedTrack(string windowTitle, bool isTitleExtended)
    {
        var expected = new Track
        {
            Artist = "Artist Name",
            Title = "Song Title",
            TitleExtended = isTitleExtended ? "Live" : "",
            Playing = true,
            Ad = false,
        };

        var track = SpotifyTitleParser.Parse(new SpotifyWindow(windowTitle, IsPlaying: true));

        Assert.Equal(expected, track);
        Assert.Equal(windowTitle, track.ToString());
    }

    [Theory]
    [InlineData(SpotifyWindowTitles.Spotify)]
    [InlineData("Spotify Sponsor")]
    [InlineData("#1337: DAILY NEWS")]
    public void PlayingAdOrUnknown_ReturnsAdTrack(string windowTitle)
    {
        var expected = new Track
        {
            Artist = windowTitle,
            Title = null,
            TitleExtended = null,
            Playing = true,
            Ad = true,
        };

        var track = SpotifyTitleParser.Parse(new SpotifyWindow(windowTitle, IsPlaying: true));

        Assert.Equal(expected, track);
        Assert.Equal(expected.ToString(), track.ToString());
    }

    [Fact]
    public void AdvertisementTitle_IsAnAdEvenWhenNotPlaying()
    {
        var track = SpotifyTitleParser.Parse(
            new SpotifyWindow(SpotifyWindowTitles.Advertisement, IsPlaying: false));

        Assert.True(track.Ad);
    }

    [Theory]
    [InlineData("Song (Remix)", "Song", "Remix", TitleSeparatorType.Parenthesis)]
    [InlineData("Song - Live", "Song", "Live", TitleSeparatorType.Dash)]
    [InlineData("Song", "Song", null, TitleSeparatorType.None)]
    public void SplitTitle_ReportsSeparator(
        string title, string expectedTitle, string? expectedExtended, TitleSeparatorType expectedSeparator)
    {
        var (tags, separator) = SpotifyTitleParser.SplitTitle(title);

        Assert.Equal(expectedSeparator, separator);
        Assert.Equal(expectedTitle, SpotifyTitleParser.TagAt(tags, 1));
        Assert.Equal(expectedExtended, SpotifyTitleParser.TagAt(tags, 2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SplitTitle_WithNothingToSplit_ReturnsNone(string? title)
    {
        var (tags, separator) = SpotifyTitleParser.SplitTitle(title!);

        Assert.Null(tags);
        Assert.Equal(TitleSeparatorType.None, separator);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(new[] { "a" }, 2)]
    public void TagAt_OutOfRange_ReturnsNull(string[]? tags, int position) =>
        Assert.Null(SpotifyTitleParser.TagAt(tags, position));

    [Fact]
    public void TagAt_ZeroPosition_ReturnsNull() =>
        Assert.Null(SpotifyTitleParser.TagAt(["a"], 0));

    /// <summary>
    /// The artist half must survive a title containing further dashes — only the first
    /// separator splits artist from title.
    /// </summary>
    [Fact]
    public void Parse_OnlySplitsArtistOnTheFirstDash()
    {
        var track = SpotifyTitleParser.Parse(new SpotifyWindow("A - B - C - D", IsPlaying: true));

        Assert.Equal("A", track.Artist);
        Assert.Equal("B", track.Title);
    }
}
