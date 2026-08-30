using System.Net;
using Moq;
using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Library;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using Xunit;

namespace Offstream.Core.Tests.Metadata.Library;

/// <summary>
/// Searching the catalogue by hand, for the match the automatic path got wrong.
/// </summary>
/// <remarks>
/// The interesting difference from <see cref="SpotifySearchMetadataProviderTests"/> is that this
/// one has no opinion about the results. The automatic lookup exists to refuse a wrong answer;
/// this exists because the user has already seen the wrong answer and wants the list.
/// </remarks>
public sealed class SpotifyMatchSearchTests
{
    /// <summary>Every result comes back, in Spotify's order.</summary>
    /// <remarks>
    /// Deliberately unfiltered. A search for a song with a live version and a remaster returns
    /// three rows that a person can tell apart at a glance and a matcher cannot — and a filter
    /// that could hide the right answer is worse here than a list with wrong ones in it.
    /// </remarks>
    [Fact]
    public async Task Search_ReturnsEveryResultWithoutJudgingThem()
    {
        var harness = new Harness();
        harness.Returns(
            Result("1", "Mr. Wendal", "Arrested Development", "3 Years", "1992-03-24"),
            Result("2", "Mr. Wendal - Live", "Someone Else Entirely", "Unplugged", "1993"));

        var results = await harness.Search.SearchAsync("mr wendal");

        Assert.Equal(2, results.Count);
        Assert.Equal("Mr. Wendal", results[0].Title);
        Assert.Equal("Someone Else Entirely", results[1].Artist);
    }

    /// <summary>The year is carried, because it is what separates two versions of one song.</summary>
    [Fact]
    public async Task Search_CarriesTheReleaseYear()
    {
        var harness = new Harness();
        harness.Returns(Result("1", "Mr. Wendal", "Arrested Development", "3 Years", "1992-03-24"));

        var results = await harness.Search.SearchAsync("mr wendal");

        Assert.Equal(1992, results[0].Year);
    }

    /// <summary>Several artists are joined rather than truncated to the first.</summary>
    [Fact]
    public async Task Search_JoinsEveryArtistOnAResult()
    {
        var harness = new Harness();

        var result = Result("1", "Thank Me", "Able Heart", "Thank Me", "2020");
        result.Artists = [new SimpleArtist { Name = "Able Heart" }, new SimpleArtist { Name = "Qveen Herby" }];

        harness.Returns(result);

        var results = await harness.Search.SearchAsync("thank me");

        Assert.Equal("Able Heart, Qveen Herby", results[0].Artist);
    }

    /// <summary>An empty query is not sent.</summary>
    /// <remarks>
    /// It would match the catalogue and cost the user a request to learn nothing.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_SkipsAnEmptyQuery(string query)
    {
        var harness = new Harness();

        Assert.Empty(await harness.Search.SearchAsync(query));

        harness.SearchClient.Verify(
            x => x.Item(It.IsAny<SearchRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Spotify's own explanation reaches the caller.</summary>
    [Fact]
    public async Task Search_ThrowsWithSpotifysMessage()
    {
        var harness = new Harness();
        harness.Fails(HttpStatusCode.Forbidden, "User not registered in the Developer Dashboard");

        var problem = await Assert.ThrowsAsync<MetadataLookupException>(
            () => harness.Search.SearchAsync("anything"));

        Assert.Contains("User not registered in the Developer Dashboard", problem.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Applying a choice looks the track up properly rather than tagging from the search result.
    /// </summary>
    /// <remarks>
    /// A search result is not a full track: the release date lives on the album and the genre on
    /// the artist, so a hand-picked match tagged from the list alone would come out thinner than
    /// an automatic one — the same tags, fetched a different way, arriving incomplete.
    /// </remarks>
    [Fact]
    public async Task Apply_FetchesTheChosenTrackAndItsAlbum()
    {
        var harness = new Harness();
        harness.ReturnsTrack("1", Result("1", "Mr. Wendal", "Arrested Development", "3 Years", "1992-03-24"));
        harness.ReturnsAlbum("album-1", new FullAlbum
        {
            Id = "album-1",
            Name = "3 Years, 5 Months and 2 Days in the Life Of...",
            ReleaseDate = "1992-03-24",
            Genres = [],
            Images = [],
        });
        harness.ReturnsArtist("artist-1", "hip hop");

        var track = new Track { Artist = "Wrong Artist", Title = "Mr Wendal" };

        await harness.Search.ApplyAsync(
            track,
            new LibraryMatchCandidate("1", "Mr. Wendal", "Arrested Development", "3 Years", 1992, null));

        Assert.Equal("Mr. Wendal", track.Title);
        Assert.Equal("Arrested Development", track.Artist);
        Assert.Equal("3 Years, 5 Months and 2 Days in the Life Of...", track.Album);
        Assert.Equal(1992, track.Year);
        Assert.Equal(["hip hop"], track.Genres!);
    }

    /// <summary>A chosen match overrules the file, which is the whole point of choosing it.</summary>
    /// <remarks>
    /// The automatic path refuses a result whose artist disagrees with the file. This one must do
    /// the opposite: the user picked it *because* the file is wrong, so the artist has to be
    /// written through <see cref="Track"/>'s API tier, which outranks the scraped value.
    /// </remarks>
    [Fact]
    public async Task Apply_OverwritesAWrongArtistOnTheFile()
    {
        var harness = new Harness();
        harness.ReturnsTrack("1", Result("1", "Mr. Wendal", "Arrested Development", "3 Years", "1992"));
        harness.ReturnsAlbum("album-1", new FullAlbum { Id = "album-1", Name = "3 Years", Genres = [], Images = [] });
        harness.ReturnsArtist("artist-1", "hip hop");

        var track = new Track { Artist = "Completely Wrong", Title = "Mr Wendal" };

        await harness.Search.ApplyAsync(
            track,
            new LibraryMatchCandidate("1", "Mr. Wendal", "Arrested Development", "3 Years", 1992, null));

        Assert.Equal("Arrested Development", track.Artist);
    }

    /// <summary>A chosen match drops the artwork belonging to the track it replaced.</summary>
    /// <remarks>
    /// The one place the "an embedded picture beats a provider's URL" rule is wrong. That rule
    /// protects artwork while a lookup confirms what the file already says; picking a different
    /// song out of a list says the opposite, and keeping the old cover would write correct tags
    /// and the previous artist's sleeve into the same file.
    /// </remarks>
    [Fact]
    public async Task Apply_DropsTheArtworkOfTheTrackItReplaced()
    {
        var harness = new Harness();
        harness.ReturnsTrack("1", Result("1", "One More Time", "Daft Punk", "Discovery", "2001"));
        harness.ReturnsAlbum("album-1", new FullAlbum
        {
            Id = "album-1",
            Name = "Discovery",
            ReleaseDate = "2001-03-12",
            Genres = [],
            // 300 and not larger: the mapper takes the biggest cover at or under 300px.
            Images = [new SpotifyAPI.Web.Image { Url = "https://example.invalid/discovery.jpg", Width = 300 }],
        });
        harness.ReturnsArtist("artist-1", "french house");

        var track = new Track
        {
            Artist = "AC",
            Title = "Who Made Who",
            AlbumArtImage = [1, 2, 3, 4],
        };

        await harness.Search.ApplyAsync(
            track,
            new LibraryMatchCandidate("1", "One More Time", "Daft Punk", "Discovery", 2001, null));

        Assert.Null(track.AlbumArtImage);
        Assert.Equal("https://example.invalid/discovery.jpg", track.AlbumArtUrl);
    }

    private static FullTrack Result(string id, string name, string artist, string album, string released) => new()
    {
        Id = id,
        Name = name,
        Album = new SimpleAlbum { Id = "album-1", Name = album, ReleaseDate = released, Images = [] },
        Artists = [new SimpleArtist { Id = "artist-1", Name = artist }],
    };

    private sealed class Harness
    {
        public Harness()
        {
            Client.SetupGet(x => x.Search).Returns(SearchClient.Object);
            Client.SetupGet(x => x.Albums).Returns(Albums.Object);
            Client.SetupGet(x => x.Artists).Returns(Artists.Object);
            Client.SetupGet(x => x.Tracks).Returns(Tracks.Object);
        }

        public Mock<ISpotifyClient> Client { get; } = new();

        public Mock<ISearchClient> SearchClient { get; } = new();

        public Mock<IAlbumsClient> Albums { get; } = new();

        public Mock<IArtistsClient> Artists { get; } = new();

        public Mock<ITracksClient> Tracks { get; } = new();

        public SpotifyMatchSearch Search => new(Client.Object);

        public void Returns(params FullTrack[] results) =>
            SearchClient
                .Setup(x => x.Item(It.IsAny<SearchRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SearchResponse
                {
                    Tracks = new Paging<FullTrack, SearchResponse> { Items = [.. results] },
                });

        public void ReturnsTrack(string id, FullTrack track) =>
            Tracks.Setup(x => x.Get(id, It.IsAny<CancellationToken>())).ReturnsAsync(track);

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

            SearchClient
                .Setup(x => x.Item(It.IsAny<SearchRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new APIException(message) { Response = response.Object });
        }
    }
}
