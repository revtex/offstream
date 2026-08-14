using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Providers;
using Offstream.Core.Spotify;
using SpotifyAPI.Web;
using Xunit;

namespace Offstream.Core.Tests.Metadata;

/// <summary>
/// Ported from the reference suite's <c>SpotifyAPITests</c> mapping cases.
/// </summary>
/// <remarks>
/// The reference constructed a live <c>SpotifyAPI</c> instance — auth dialog and all — just to
/// call its two mapping methods. Here the mapper is pure, so these run against SDK model
/// fixtures with nothing else in play.
/// </remarks>
public sealed class SpotifyTrackMapperTests
{
    private static Track WindowTitleTrack() => new() { Artist = "Artist", Title = "Title" };

    private static Image Cover(int size, string url) => new() { Width = size, Height = size, Url = url };

    [Fact]
    public void ApplyTrack_WithAnEmptyResponse_LeavesTheWindowTitleTrackReadable()
    {
        var track = WindowTitleTrack();

        SpotifyTrackMapper.Apply(track, new FullTrack());

        Assert.NotNull(track.Title);

        // TrackNumber/DiscNumber are non-nullable on the SDK model and default to 0 for a track
        // it could not populate; 0 is never a real Spotify position, so it maps to null.
        Assert.Null(track.AlbumPosition);
        Assert.Equal([], track.Performers!);
        Assert.Null(track.Disc);
    }

    [Fact]
    public void ApplyTrack_MapsNameArtistsTrackNumberAndDisc()
    {
        var fullTrack = new FullTrack
        {
            Name = "Title",
            TrackNumber = 3,
            Artists =
            [
                new SimpleArtist { Name = "Artist" },
                new SimpleArtist { Name = "Other Artist" },
            ],
            DiscNumber = 2,
        };

        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, fullTrack);

        Assert.Equal("Title", track.Title);
        Assert.Equal("Artist", track.Artist);
        Assert.Equal(3, track.AlbumPosition);
        Assert.Equal(["Artist", "Other Artist"], track.Performers!);
        Assert.Equal(2, track.Disc);
    }

    [Fact]
    public void ApplyTrack_OverwritesTheWindowTitleTrack()
    {
        var fullTrack = new FullTrack
        {
            Name = "Updated Title",
            Artists =
            [
                new SimpleArtist { Name = "Updated Artist" },
                new SimpleArtist { Name = "Other Artist" },
            ],
        };

        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, fullTrack);

        Assert.Equal("Updated Title", track.Title);
        Assert.Equal("Updated Artist", track.Artist);
    }

    [Fact]
    public void ApplyTrack_KeepsTheWindowTitleTrackWhenTheResponseIsBlank()
    {
        var fullTrack = new FullTrack
        {
            Name = "",
            Artists = [new SimpleArtist { Name = "" }],
        };

        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, fullTrack);

        Assert.Equal("Title", track.Title);
        Assert.Equal("Artist", track.Artist);
    }

    [Theory]
    [InlineData("Title", TitleSeparatorType.None, "Title", null)]
    [InlineData("Title - Live", TitleSeparatorType.Dash, "Title", "Live")]
    [InlineData("Title (feat. Other Artist)", TitleSeparatorType.Parenthesis, "Title", "feat. Other Artist")]
    [InlineData(
        "Title (feat. Other Artist) - Live", TitleSeparatorType.Dash, "Title (feat. Other Artist)", "Live")]
    public void ApplyTrack_SplitsTheTitleExactlyAsTheWindowTitleParserDoes(
        string apiTitle, TitleSeparatorType expectedSeparator, string expectedTitle, string? expectedExtended)
    {
        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, new FullTrack { Name = apiTitle });

        Assert.Equal(expectedSeparator, track.TitleExtendedSeparatorType);
        Assert.Equal(expectedTitle, track.Title);
        Assert.Equal(expectedExtended, track.TitleExtended);
    }

    /// <summary>
    /// An empty album object leaves the album fields alone rather than blanking them.
    /// </summary>
    /// <remarks>
    /// This asserted the opposite until 2026-08-14 — that an empty response wrote <c>""</c> and
    /// an empty array over whatever was there. That was harmless while the only other source was
    /// a window title, which supplies no album at all; it stopped being harmless when the media
    /// session began supplying album, album artist and track number, because a provider that
    /// could not answer would erase what the client had already said for certain.
    /// </remarks>
    [Fact]
    public void ApplyAlbum_WithAnEmptyResponse_LeavesTheAlbumFieldsAlone()
    {
        var fullAlbum = new FullAlbum { Artists = [], Name = "", Genres = [], Images = [] };

        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, fullAlbum);

        Assert.Null(track.AlbumArtists);
        Assert.Null(track.Album);
        Assert.Equal([], track.Genres!);
        Assert.Null(track.Year);
        Assert.Null(track.AlbumArtUrl);
    }

    /// <summary>
    /// The floor: what the media session already established survives a provider that has
    /// nothing of its own to say about it.
    /// </summary>
    [Fact]
    public void ApplyAlbum_WithAnEmptyResponse_KeepsWhatTheMediaSessionSupplied()
    {
        var track = new Track
        {
            Artist = "Artist",
            Title = "Title",
            Album = "Detected Album",
            AlbumArtists = ["Detected Album Artist"],
            AlbumPosition = 7,
        };

        SpotifyTrackMapper.Apply(track, new FullTrack());
        SpotifyTrackMapper.Apply(track, new FullAlbum { Artists = [], Name = "", Genres = [], Images = [] });

        Assert.Equal("Detected Album", track.Album);
        Assert.Equal(["Detected Album Artist"], track.AlbumArtists!);
        Assert.Equal(7, track.AlbumPosition);
    }

    /// <summary>A provider that does know better still wins, which is the whole precedence.</summary>
    [Fact]
    public void ApplyAlbum_WhenTheProviderKnowsBetter_OverwritesWhatWasDetected()
    {
        var track = new Track
        {
            Artist = "Artist",
            Title = "Title",
            Album = "Detected Album",
            AlbumArtists = ["Detected Album Artist"],
            AlbumPosition = 7,
        };

        SpotifyTrackMapper.Apply(track, new FullTrack { Name = "Title", TrackNumber = 3 });
        SpotifyTrackMapper.Apply(
            track,
            new FullAlbum
            {
                Artists = [new SimpleArtist { Name = "Real Album Artist" }],
                Name = "Real Album",
                Genres = [],
                Images = [],
            });

        Assert.Equal("Real Album", track.Album);
        Assert.Equal(["Real Album Artist"], track.AlbumArtists!);
        Assert.Equal(3, track.AlbumPosition);
    }

    [Fact]
    public void ApplyAlbum_WithNoImages_LeavesCoverArtUrlNull()
    {
        var fullAlbum = new FullAlbum
        {
            Artists = [new SimpleArtist { Name = "Artist" }, new SimpleArtist { Name = "Other Artist" }],
            Name = "Album Name",
            Genres = ["Reggae", "Rock", "Jazz"],
            ReleaseDate = "2010-10-10",
            Images = [],
        };

        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, fullAlbum);

        Assert.Equal(["Artist", "Other Artist"], track.AlbumArtists!);
        Assert.Equal("Album Name", track.Album);
        Assert.Equal(["Reggae", "Rock", "Jazz"], track.Genres!);
        Assert.Equal(2010, track.Year);
        Assert.Null(track.AlbumArtUrl);
    }

    [Fact]
    public void ApplyAlbum_PrefersTheLargestCoverAtOrUnder300px()
    {
        var fullAlbum = new FullAlbum
        {
            Artists = [new SimpleArtist { Name = "Artist" }],
            Name = "Album Name",
            Genres = [],
            ReleaseDate = "2010-10-10",
            Images = [Cover(64, "http://64.img"), Cover(256, "http://256.img"), Cover(512, "http://512.img")],
        };

        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, fullAlbum);

        Assert.Equal("http://256.img", track.AlbumArtUrl);
    }

    [Fact]
    public void ApplyAlbum_WithManyCoversPicksThe300pxOneWhenPresent()
    {
        var fullAlbum = new FullAlbum
        {
            Artists = [new SimpleArtist { Name = "Artist" }],
            Name = "Album Name",
            Genres = [],
            ReleaseDate = "2010-10-10",
            Images =
            [
                Cover(128, "http://128.img"),
                Cover(32, "http://32.img"),
                Cover(16, "http://16.img"),
                Cover(512, "http://512.img"),
                Cover(64, "http://64.img"),
                Cover(300, "http://300.img"),
            ],
        };

        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, fullAlbum);

        Assert.Equal("http://300.img", track.AlbumArtUrl);
    }

    /// <summary>
    /// <c>DateTime.TryParse</c> rejects a bare four-digit year outright, which would otherwise
    /// drop the year for every album whose Spotify precision is year-only rather than a full date.
    /// </summary>
    [Theory]
    [InlineData("1987", 1987)]
    [InlineData("2010-10", 2010)]
    [InlineData("2010-10-10", 2010)]
    public void ApplyAlbum_ParsesEveryReleaseDatePrecisionSpotifySends(string releaseDate, int expectedYear)
    {
        var fullAlbum = new FullAlbum { Artists = [], Name = "Album", Genres = [], Images = [], ReleaseDate = releaseDate };

        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, fullAlbum);

        Assert.Equal(expectedYear, track.Year);
    }

    [Fact]
    public void ApplyAlbum_WithNoReleaseDate_LeavesYearNull()
    {
        var fullAlbum = new FullAlbum { Artists = [], Name = "Album", Genres = [], Images = [], ReleaseDate = null! };

        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, fullAlbum);

        Assert.Null(track.Year);
    }

    /// <summary>
    /// The tag keeps Spotify's own precision even though <see cref="Track.Year"/> flattens it.
    /// </summary>
    [Theory]
    [InlineData("1987", "1987")]
    [InlineData("2010-10", "2010-10")]
    [InlineData("2010-10-10", "2010-10-10")]
    public void ApplyAlbum_KeepsTheReleaseDateAtFullPrecision(string releaseDate, string expected)
    {
        var fullAlbum = new FullAlbum { Artists = [], Name = "Album", Genres = [], Images = [], ReleaseDate = releaseDate };

        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, fullAlbum);

        Assert.Equal(expected, track.ReleaseDate);
    }

    /// <summary>A malformed date is worse in a tag than no date, so it is dropped.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("2010-13-40")]
    [InlineData("10/10/2010")]
    public void ApplyAlbum_DropsAReleaseDateItDoesNotRecognise(string releaseDate)
    {
        var fullAlbum = new FullAlbum { Artists = [], Name = "Album", Genres = [], Images = [], ReleaseDate = releaseDate };

        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, fullAlbum);

        Assert.Null(track.ReleaseDate);
    }

    [Theory]
    [InlineData(12, 12)]
    [InlineData(1, 1)]

    // Non-nullable on the SDK model, so an unpopulated album reads as zero tracks.
    [InlineData(0, null)]
    public void ApplyAlbum_MapsTheTrackTotal(int totalTracks, int? expected)
    {
        var fullAlbum = new FullAlbum
        {
            Artists = [],
            Name = "Album",
            Genres = [],
            Images = [],
            ReleaseDate = "2010",
            TotalTracks = totalTracks,
        };

        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, fullAlbum);

        Assert.Equal(expected, track.AlbumTrackCount);
    }

    /// <summary>
    /// The media session reports a track count of its own, so an album object without one leaves
    /// it standing rather than blanking it — the same rule the album and its artists follow.
    /// </summary>
    [Fact]
    public void ApplyAlbum_WithNoTrackTotal_KeepsTheOneTheMediaSessionSupplied()
    {
        var fullAlbum = new FullAlbum
        {
            Artists = [],
            Name = "Album",
            Genres = [],
            Images = [],
            ReleaseDate = "2010",
            TotalTracks = 0,
        };

        var track = WindowTitleTrack();
        track.AlbumTrackCount = 12;

        SpotifyTrackMapper.Apply(track, fullAlbum);

        Assert.Equal(12, track.AlbumTrackCount);
    }

    /// <summary>
    /// Offstream records audio, so the phonogram line describes what is actually in the file.
    /// </summary>
    [Fact]
    public void ApplyAlbum_PrefersThePhonogramCopyrightOverTheComposition()
    {
        var fullAlbum = Album([
            new Copyright { Type = "C", Text = "1997 Composition Ltd" },
            new Copyright { Type = "P", Text = "1997 Recording Ltd" },
        ]);

        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, fullAlbum);

        Assert.Equal("1997 Recording Ltd", track.Copyright);
    }

    [Fact]
    public void ApplyAlbum_FallsBackToWhicheverCopyrightLineExists()
    {
        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, Album([new Copyright { Type = "C", Text = "1997 Composition Ltd" }]));

        Assert.Equal("1997 Composition Ltd", track.Copyright);
    }

    [Fact]
    public void ApplyAlbum_IgnoresBlankCopyrightLines()
    {
        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, Album([new Copyright { Type = "P", Text = "   " }]));

        Assert.Null(track.Copyright);
    }

    [Fact]
    public void ApplyAlbum_WithNoCopyrights_LeavesItNull()
    {
        var track = WindowTitleTrack();
        SpotifyTrackMapper.Apply(track, Album([]));

        Assert.Null(track.Copyright);
    }

    private static FullAlbum Album(List<Copyright> copyrights) => new()
    {
        Artists = [],
        Name = "Album",
        Genres = [],
        Images = [],
        ReleaseDate = "1997",
        Copyrights = copyrights,
    };

    [Fact]
    public void ChooseCoverUrl_WithNoImageAtOrUnder300px_ReturnsNull() =>
        Assert.Null(SpotifyTrackMapper.ChooseCoverUrl([Cover(640, "http://640.img"), Cover(1024, "http://1024.img")]));

    [Fact]
    public void ChooseCoverUrl_WithNullImages_ReturnsNull() =>
        Assert.Null(SpotifyTrackMapper.ChooseCoverUrl(null));
}
