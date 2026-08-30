using System.Net;
using Moq;
using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Library;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using Xunit;

namespace Offstream.Core.Tests.Metadata.Library;

/// <summary>
/// Looking a file up by searching, and refusing to answer when the result is not the track.
/// </summary>
/// <remarks>
/// The recording path confirms a track it already knows; this one guesses from a filename, so
/// the interesting tests are the ones about *not* matching. A confident wrong answer written
/// into a file the user had correctly named is the one unrecoverable failure this page has.
/// </remarks>
public sealed class SpotifySearchMetadataProviderTests
{
    /// <summary>An exact artist and title match is taken, and the album is fetched for it.</summary>
    [Fact]
    public async Task Enrich_TakesAnExactMatchAndFillsInTheAlbum()
    {
        var harness = new Harness();
        harness.Returns(Found("The Mother We Share", "Chvrches"));
        harness.ReturnsAlbum("album-1", new FullAlbum
        {
            Id = "album-1",
            Name = "The Bones of What You Believe",
            ReleaseDate = "2013-09-20",
            Genres = ["synthpop"],
            Images = [],
        });

        var track = new Track { Artist = "Chvrches", Title = "The Mother We Share" };

        Assert.True(await harness.Provider.EnrichAsync(track));
        Assert.Equal("The Bones of What You Believe", track.Album);
        Assert.Equal(2013, track.Year);
    }

    /// <summary>
    /// A result whose artist does not agree is not a match.
    /// </summary>
    /// <remarks>
    /// Spotify always returns something. The top hit for a misparsed filename is routinely a
    /// different song, so returning nothing is the better answer.
    /// </remarks>
    [Fact]
    public async Task Enrich_RejectsAResultByADifferentArtist()
    {
        var harness = new Harness();
        harness.Returns(Found("The Mother We Share", "Someone Else"));

        var track = new Track { Artist = "Chvrches", Title = "The Mother We Share" };

        Assert.False(await harness.Provider.EnrichAsync(track));
        Assert.Null(track.Album);
    }

    /// <summary>An empty result set is an ordinary "no".</summary>
    [Fact]
    public async Task Enrich_ReturnsFalseWhenNothingIsFound()
    {
        var harness = new Harness();
        harness.Returns();

        Assert.False(await harness.Provider.EnrichAsync(
            new Track { Artist = "Nobody", Title = "Nothing" }));
    }

    /// <summary>
    /// Without both an artist and a title there is nothing worth searching for.
    /// </summary>
    /// <remarks>
    /// A title alone matches half the catalogue. The request is skipped entirely rather than sent
    /// and then discarded, because the user's quota is the thing being protected.
    /// </remarks>
    [Theory]
    [InlineData(null, "Title")]
    [InlineData("Artist", null)]
    [InlineData(null, null)]
    public async Task Enrich_DoesNotSearchWithoutBothFields(string? artist, string? title)
    {
        var harness = new Harness();

        Assert.False(await harness.Provider.EnrichAsync(new Track { Artist = artist, Title = title }));

        harness.Search.Verify(
            x => x.Item(It.IsAny<SearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The query uses Spotify's own field filters.
    /// </summary>
    /// <remarks>
    /// Free text scores a match on any field, so an artist whose name appears in someone else's
    /// album title outranks the actual song. This is the difference between a page that mostly
    /// works and one that mostly does not.
    /// </remarks>
    [Fact]
    public async Task Enrich_SearchesWithFieldFilters()
    {
        var harness = new Harness();
        harness.Returns();

        await harness.Provider.EnrichAsync(new Track { Artist = "Chvrches", Title = "Gun" });

        harness.Search.Verify(
            x => x.Item(
                It.Is<SearchRequest>(request =>
                    request.Query.Contains("track:\"Gun\"", StringComparison.Ordinal)
                    && request.Query.Contains("artist:\"Chvrches\"", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>A stray quote cannot break out of the filter it sits in.</summary>
    [Fact]
    public async Task Enrich_StripsQuotesFromTheQuery()
    {
        var harness = new Harness();
        harness.Returns();

        await harness.Provider.EnrichAsync(new Track { Artist = "A\"B", Title = "C\"D" });

        harness.Search.Verify(
            x => x.Item(
                It.Is<SearchRequest>(request =>
                    request.Query.Contains("track:\"CD\"", StringComparison.Ordinal)
                    && request.Query.Contains("artist:\"AB\"", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Spotify's refusals become sentences that keep Spotify's own words.
    /// </summary>
    /// <remarks>
    /// A 403 means either "this account is not on the dashboard app's list" or "the quota is
    /// spent", and only the body tells them apart — so the body has to survive into the message.
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Sign in again")]
    [InlineData(HttpStatusCode.Forbidden, "development mode")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    public async Task Enrich_ExplainsAnApiFailure(HttpStatusCode status, string expected)
    {
        var harness = new Harness();
        harness.Fails(status, "Spotify said so");

        var ex = await Assert.ThrowsAsync<MetadataLookupException>(() =>
            harness.Provider.EnrichAsync(new Track { Artist = "A", Title = "T" }));

        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The 403 body is quoted rather than replaced.</summary>
    [Fact]
    public async Task Enrich_QuotesSpotifysOwnReasonForA403()
    {
        var harness = new Harness();
        harness.Fails(HttpStatusCode.Forbidden, "User not registered in the Developer Dashboard");

        var ex = await Assert.ThrowsAsync<MetadataLookupException>(() =>
            harness.Provider.EnrichAsync(new Track { Artist = "A", Title = "T" }));

        Assert.Contains("Developer Dashboard", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Genre comes from the artist, because Spotify has none on a track.</summary>
    [Fact]
    public async Task Enrich_FallsBackToArtistGenresWhenTheAlbumHasNone()
    {
        var harness = new Harness();
        harness.Returns(Found("Gun", "Chvrches"));
        harness.ReturnsAlbum("album-1", new FullAlbum { Id = "album-1", Genres = [], Images = [] });
        harness.ReturnsArtist("artist-1", "synthpop", "indie pop", "scottish", "electropop");

        var track = new Track { Artist = "Chvrches", Title = "Gun" };

        Assert.True(await harness.Provider.EnrichAsync(track));
        Assert.Equal(["synthpop", "indie pop", "scottish"], track.Genres!);
    }

    /// <summary>A genre the file already carried survives a lookup that offers none.</summary>
    /// <remarks>
    /// Spotify returns an empty genre list for most of its catalogue, and the album mapping
    /// assigns it unconditionally — correct while recording, where the track starts empty, and
    /// destructive here, where it starts as the file's own tags. Nothing was ever written, since
    /// the writer skips empty values, but the row claimed a change it would not make and the
    /// before-and-after read as an offer to erase a curated genre.
    /// </remarks>
    [Fact]
    public async Task Enrich_KeepsTheGenreTheFileAlreadyHad()
    {
        var harness = new Harness();
        harness.Returns(Found("Mr. Wendal", "Arrested Development"));
        harness.ReturnsAlbum("album-1", new FullAlbum
        {
            Id = "album-1",
            Name = "3 Years, 5 Months and 2 Days in the Life Of...",
            ReleaseDate = "1992-03-24",
            Genres = [],
            Images = [],
        });

        var track = new Track
        {
            Artist = "Arrested Development",
            Title = "Mr. Wendal",
            Genres = ["Hip-Hop", "rap"],
        };

        Assert.True(await harness.Provider.EnrichAsync(track));
        Assert.Equal(["Hip-Hop", "rap"], track.Genres!);

        // The file's own genre is good enough, so the artist is never asked.
        harness.Artists.Verify(
            x => x.Get(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A year the file already carried survives an album with no release date.</summary>
    [Fact]
    public async Task Enrich_KeepsTheYearTheFileAlreadyHad()
    {
        var harness = new Harness();
        harness.Returns(Found("Mr. Wendal", "Arrested Development"));
        harness.ReturnsAlbum("album-1", new FullAlbum
        {
            Id = "album-1",
            Name = "3 Years, 5 Months and 2 Days in the Life Of...",
            ReleaseDate = string.Empty,
            Genres = [],
            Images = [],
        });
        harness.ReturnsArtist("artist-1", "hip hop");

        var track = new Track
        {
            Artist = "Arrested Development",
            Title = "Mr. Wendal",
            Year = 1992,
        };

        Assert.True(await harness.Provider.EnrichAsync(track));
        Assert.Equal(1992, track.Year);
    }

    /// <summary>A file with no genre still gets the artist's.</summary>
    /// <remarks>
    /// The other half of the rule above: putting the file's own value back must not stop the
    /// fallback that fills a genuinely empty field, which is the reason the lookup runs at all.
    /// </remarks>
    [Fact]
    public async Task Enrich_StillFallsBackToTheArtistWhenTheFileHasNoGenre()
    {
        var harness = new Harness();
        harness.Returns(Found("Mr. Wendal", "Arrested Development"));
        harness.ReturnsAlbum("album-1", new FullAlbum
        {
            Id = "album-1",
            Name = "3 Years, 5 Months and 2 Days in the Life Of...",
            ReleaseDate = "1992-03-24",
            Genres = [],
            Images = [],
        });
        harness.ReturnsArtist("artist-1", "hip hop", "conscious hip hop");

        var track = new Track { Artist = "Arrested Development", Title = "Mr. Wendal" };

        Assert.True(await harness.Provider.EnrichAsync(track));
        Assert.Equal(["hip hop", "conscious hip hop"], track.Genres!);
    }

    private static FullTrack Found(string name, string artist) => new()
    {
        Name = name,
        Album = new SimpleAlbum { Id = "album-1" },
        Artists = [new SimpleArtist { Id = "artist-1", Name = artist }],
        TrackNumber = 1,
        DiscNumber = 1,
    };

    private sealed class Harness
    {
        public Harness()
        {
            Client.SetupGet(x => x.Search).Returns(Search.Object);
            Client.SetupGet(x => x.Albums).Returns(Albums.Object);
            Client.SetupGet(x => x.Artists).Returns(Artists.Object);
        }

        public Mock<ISpotifyClient> Client { get; } = new();

        public Mock<ISearchClient> Search { get; } = new();

        public Mock<IAlbumsClient> Albums { get; } = new();

        public Mock<IArtistsClient> Artists { get; } = new();

        public SpotifySearchMetadataProvider Provider => new(Client.Object);

        public void Returns(params FullTrack[] tracks) =>
            Search
                .Setup(x => x.Item(It.IsAny<SearchRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SearchResponse { Tracks = new Paging<FullTrack, SearchResponse> { Items = [.. tracks] } });

        public void ReturnsAlbum(string albumId, FullAlbum album) =>
            Albums.Setup(x => x.Get(albumId, It.IsAny<CancellationToken>())).ReturnsAsync(album);

        public void ReturnsArtist(string artistId, params string[] genres) =>
            Artists
                .Setup(x => x.Get(artistId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FullArtist { Id = artistId, Genres = [.. genres] });

        public void Fails(HttpStatusCode status, string message)
        {
            var response = new Mock<IResponse>();
            response.SetupGet(x => x.StatusCode).Returns(status);

            Search
                .Setup(x => x.Item(It.IsAny<SearchRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new APIException(message) { Response = response.Object });
        }
    }
}
