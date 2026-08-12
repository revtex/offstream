using Offstream.Core.Metadata;
using Offstream.Core.Naming;
using Offstream.Core.Spotify;
using Xunit;

namespace Offstream.Core.Tests.Metadata;

/// <summary>Ported from the reference suite's <c>TrackTests</c>, assertions unchanged.</summary>
public sealed class TrackTests
{
    [Fact]
    public void DefaultTrack_ReturnsEmptyTrack()
    {
        var track = new Track();

        Assert.False(track.IsNormalPlaying);
        Assert.Equal(SpotifyWindowTitles.Spotify, track.ToString());
    }

    [Fact]
    public void MinimalTrack_ReturnsBasicInfo()
    {
        var track = new Track
        {
            Title = "Song Title",
            Artist = "Artist Name",
            Ad = false,
            Playing = true,
            TitleExtended = "Live",
            TitleExtendedSeparatorType = TitleSeparatorType.Dash,
        };

        Assert.True(track.IsNormalPlaying);
        Assert.Equal("Artist Name - Song Title - Live", track.ToString());
        Assert.NotEqual(track, new Track());
    }

    [Theory]
    [InlineData("A", "B", false, true, true)]
    [InlineData("A", "", false, true, false)]
    [InlineData("", "B", false, true, false)]
    [InlineData("", "", false, true, false)]
    [InlineData(null, null, false, true, false)]
    [InlineData("A", "B", true, true, false)]
    [InlineData("A", "B", false, false, false)]
    public void IsNormalPlaying_ReturnsExpectedResults(
        string? artist, string? title, bool ad, bool playing, bool expected)
    {
        var track = new Track { Artist = artist, Title = title, Ad = ad, Playing = playing };

        Assert.Equal(expected, track.IsNormalPlaying);
    }

    [Fact]
    public void CopyConstructor_CarriesIdentityButNotLaterEdits()
    {
        var initial = new Track
        {
            Title = "Song Title",
            Artist = "Artist Name",
            Ad = false,
            Playing = true,
            TitleExtended = "Live",
            TitleExtendedSeparatorType = TitleSeparatorType.Dash,
        };

        var copy = new Track(initial)
        {
            Album = "Album",
            AlbumPosition = 1,
            AlbumArtUrl = "http://logo.png",
        };

        Assert.Equal(initial.ToString(), copy.ToString());
        Assert.NotEqual(initial.Album, copy.Album);
        Assert.NotEqual(initial.AlbumArtUrl, copy.AlbumArtUrl);
        Assert.NotEqual(copy, new Track());
    }

    [Fact]
    public void ToTitleString_WithEmptyTrack_ReturnsEmpty() =>
        Assert.Equal(string.Empty, new Track().ToTitleString());

    [Theory]
    [InlineData(TitleSeparatorType.None, "Must Not Return", "Title")]
    [InlineData(TitleSeparatorType.Dash, "Remastered", "Title - Remastered")]
    [InlineData(TitleSeparatorType.Parenthesis, "Featuring Other", "Title (Featuring Other)")]
    public void ToTitleString_AppliesTheSeparator(
        TitleSeparatorType separator, string extended, string expected)
    {
        var track = new Track
        {
            Title = "Title",
            Artist = "Artist",
            TitleExtended = extended,
            TitleExtendedSeparatorType = separator,
        };

        Assert.Equal(expected, track.ToTitleString());
    }

    [Fact]
    public void ToTitleString_KeepsParentheticalAlreadyInTheTitle()
    {
        var track = new Track
        {
            Title = "Title (Featuring Other)",
            Artist = "Artist",
            TitleExtended = "Remastered",
            TitleExtendedSeparatorType = TitleSeparatorType.Dash,
        };

        Assert.Equal("Title (Featuring Other) - Remastered", track.ToTitleString());
    }

    [Fact]
    public void Equals_ComparesIdentityFieldsOnly()
    {
        var empty = new Track();
        var detailed = new Track
        {
            Title = "Title",
            Artist = "Artist",
            TitleExtended = "",
            Ad = false,
        };

        Assert.True(empty.Equals(new Track()));
        Assert.True(detailed.Equals(new Track { Title = "Title", Artist = "Artist" }));

        Assert.False(empty.Equals(null));
        Assert.False(empty.Equals(new OutputFile()));
        Assert.False(empty.Equals(new Track { Title = "Title" }));
    }

    /// <summary>
    /// Values fetched from a metadata provider override what the window title supplied, and
    /// keep overriding it — the setter must not win back after the API has spoken.
    /// </summary>
    [Fact]
    public void ApiValues_OverrideWindowTitleValues()
    {
        var track = new Track { Artist = "Window Artist", Title = "Window Title" };

        track.SetArtistFromApi("Api Artist");
        track.SetTitleFromApi("Api Title");

        Assert.Equal("Api Artist", track.Artist);
        Assert.Equal("Api Title", track.Title);

        track.Artist = "Later Window Artist";

        Assert.Equal("Api Artist", track.Artist);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ApiValues_IgnoreBlanks(string? value)
    {
        var track = new Track { Artist = "Window Artist" };

        track.SetArtistFromApi(value);

        Assert.Equal("Window Artist", track.Artist);
    }

    [Fact]
    public void GetHashCode_MatchesForEqualTracks()
    {
        var left = new Track { Artist = "Artist", Title = "Title" };
        var right = new Track { Artist = "Artist", Title = "Title" };

        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
}
