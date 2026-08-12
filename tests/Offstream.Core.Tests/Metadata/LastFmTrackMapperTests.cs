using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Providers;
using Xunit;

namespace Offstream.Core.Tests.Metadata;

/// <summary>
/// Ported from the reference suite's <c>LastFMAPITests</c> mapping cases.
/// </summary>
/// <remarks>
/// The original also had a <c>TestAPIKeys_ReturnsOk</c> case that called the live Last.fm
/// API and therefore failed offline. Plan §9.3 requires no network in unit tests, so it is
/// replaced by fixture-driven mapping tests — the mapping is what could actually regress,
/// and a liveness check belongs in a manual or integration pass, not the safety net.
/// </remarks>
public sealed class LastFmTrackMapperTests
{
    private static Track WindowTitleTrack() => new() { Artist = "Artist", Title = "Title" };

    private static LastFmImage Image(string size, string url) => new() { Size = size, Url = url };

    [Fact]
    public void Apply_WithEmptyResponse_LeavesTitleAndArtistAlone()
    {
        var track = WindowTitleTrack();

        LastFmTrackMapper.Apply(track, new LastFmTrack());

        Assert.Equal("Artist", track.Artist);
        Assert.Equal("Title", track.Title);
        Assert.Null(track.Album);
        Assert.Null(track.AlbumPosition);
        Assert.Null(track.Length);
        Assert.Null(track.AlbumArtUrl);
        Assert.Empty(track.Genres!);
    }

    [Fact]
    public void Apply_MapsAlbumAndPosition()
    {
        var track = WindowTitleTrack();
        var response = new LastFmTrack
        {
            Album = new LastFmAlbum { Title = "Album", Position = "4" },
        };

        LastFmTrackMapper.Apply(track, response);

        Assert.Equal("Album", track.Album);
        Assert.Equal(4, track.AlbumPosition);
    }

    [Fact]
    public void Apply_ConvertsDurationFromMillisecondsToSeconds()
    {
        var track = WindowTitleTrack();

        LastFmTrackMapper.Apply(track, new LastFmTrack { Duration = 215_000 });

        Assert.Equal(215, track.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Apply_WithNoUsableDuration_LeavesLengthNull(int? duration)
    {
        var track = WindowTitleTrack();

        LastFmTrackMapper.Apply(track, new LastFmTrack { Duration = duration });

        Assert.Null(track.Length);
    }

    [Fact]
    public void Apply_SetsAlbumArtistsFromTheWindowTitleArtist()
    {
        var track = WindowTitleTrack();

        LastFmTrackMapper.Apply(track, new LastFmTrack());

        Assert.NotNull(track.AlbumArtists);
        Assert.Equal(["Artist"], track.AlbumArtists);
    }

    [Fact]
    public void Apply_AddsFeaturedPerformersFromTheTitle()
    {
        var track = new Track { Artist = "Artist", Title = "Title (feat. Guest)" };

        LastFmTrackMapper.Apply(track, new LastFmTrack());

        Assert.Contains("Artist", track.Performers!);
        Assert.Contains("Guest", track.Performers!);
    }

    [Fact]
    public void ChooseCoverUrl_PrefersThe300PixelVariant()
    {
        var album = new LastFmAlbum
        {
            Images =
            [
                Image("extralarge", "https://lastfm/i/u/1200x1200/cover.png"),
                Image("large", "https://lastfm/i/u/300x300/cover.png"),
            ],
        };

        Assert.Equal("https://lastfm/i/u/300x300/cover.png", LastFmTrackMapper.ChooseCoverUrl(album));
    }

    [Fact]
    public void ChooseCoverUrl_AlsoMatchesThe300sShape()
    {
        var album = new LastFmAlbum
        {
            Images =
            [
                Image("extralarge", "https://lastfm/i/u/1200x1200/cover.png"),
                Image("medium", "https://lastfm/i/u/300s/cover.png"),
            ],
        };

        Assert.Equal("https://lastfm/i/u/300s/cover.png", LastFmTrackMapper.ChooseCoverUrl(album));
    }

    [Fact]
    public void ChooseCoverUrl_FallsBackToLargestWhenNo300Exists()
    {
        var album = new LastFmAlbum
        {
            Images =
            [
                Image("extralarge", "https://lastfm/i/u/1200x1200/cover.png"),
                Image("small", "https://lastfm/i/u/64s/cover.png"),
            ],
        };

        Assert.Equal("https://lastfm/i/u/1200x1200/cover.png", LastFmTrackMapper.ChooseCoverUrl(album));
    }

    [Fact]
    public void ChooseCoverUrl_WithNoImages_ReturnsNull() =>
        Assert.Null(LastFmTrackMapper.ChooseCoverUrl(new LastFmAlbum()));

    [Fact]
    public void ChooseCoverUrl_WithNoAlbum_ReturnsNull() =>
        Assert.Null(LastFmTrackMapper.ChooseCoverUrl(null));

    /// <summary>An unrecognised size string must not throw or match a known size.</summary>
    [Fact]
    public void CoverSize_WithUnknownSize_IsNull() =>
        Assert.Null(Image("gigantic", "https://lastfm/cover.png").CoverSize);

    [Theory]
    [InlineData("small", AlbumCoverSize.Small)]
    [InlineData("MEDIUM", AlbumCoverSize.Medium)]
    [InlineData("extralarge", AlbumCoverSize.ExtraLarge)]
    public void CoverSize_ParsesKnownSizes(string size, AlbumCoverSize expected) =>
        Assert.Equal(expected, Image(size, "https://lastfm/cover.png").CoverSize);

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("not-a-number", null)]
    [InlineData("7", 7)]
    public void TrackPosition_ParsesLeniently(string? position, int? expected) =>
        Assert.Equal(expected, new LastFmAlbum { Position = position }.TrackPosition);
}
