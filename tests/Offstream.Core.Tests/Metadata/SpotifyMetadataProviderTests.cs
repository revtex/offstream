using System.Net;
using Moq;
using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Providers;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using Xunit;

namespace Offstream.Core.Tests.Metadata;

/// <summary>
/// The read half of the reference suite's <c>SpotifyAPITests.UpdateTrack</c> coverage — against
/// a mocked <see cref="ISpotifyClient"/>, so no network and no auth are involved.
/// </summary>
public sealed class SpotifyMetadataProviderTests
{
    private static Track DetectedTrack(string title = "Title") => new() { Artist = "Artist", Title = title };

    private sealed class Harness
    {
        public Mock<ISpotifyClient> Client { get; } = new();

        public Mock<IPlayerClient> Player { get; } = new();

        public Mock<IAlbumsClient> Albums { get; } = new();

        public Mock<IArtistsClient> Artists { get; } = new();

        public Harness()
        {
            Client.SetupGet(x => x.Player).Returns(Player.Object);
            Client.SetupGet(x => x.Albums).Returns(Albums.Object);
            Client.SetupGet(x => x.Artists).Returns(Artists.Object);
        }

        /// <summary>Real behaviour, test timings: the delays are the only thing shortened.</summary>
        public SpotifyMetadataProvider Provider =>
            new(Client.Object, new SpotifyPollingOptions(TimeSpan.Zero, TimeSpan.Zero, MaximumAttempts: 4));

        /// <summary>Answers with each playback in turn, repeating the last one thereafter.</summary>
        public void ReturnsPlaybackInTurn(params CurrentlyPlaying?[] answers)
        {
            var call = 0;

            Player
                .Setup(x => x.GetCurrentlyPlaying(It.IsAny<PlayerCurrentlyPlayingRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => answers[Math.Min(call++, answers.Length - 1)]!);
        }

        public void ReturnsPlayback(CurrentlyPlaying? playback) =>
            Player
                .Setup(x => x.GetCurrentlyPlaying(It.IsAny<PlayerCurrentlyPlayingRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(playback!);

        /// <summary>Fails the playback call the way the SDK surfaces an API error.</summary>
        public void Fails(HttpStatusCode status, string message)
        {
            var response = new Mock<IResponse>();
            response.SetupGet(x => x.StatusCode).Returns(status);

            Player
                .Setup(x => x.GetCurrentlyPlaying(It.IsAny<PlayerCurrentlyPlayingRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new APIException(message) { Response = response.Object });
        }

        public void ReturnsAlbum(string albumId, FullAlbum album) =>
            Albums.Setup(x => x.Get(albumId, It.IsAny<CancellationToken>())).ReturnsAsync(album);

        public void ReturnsArtist(string artistId, params string[] genres) =>
            Artists
                .Setup(x => x.Get(artistId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FullArtist { Id = artistId, Genres = [.. genres] });

        public void ArtistWasAskedFor(string artistId, Times times) =>
            Artists.Verify(x => x.Get(artistId, It.IsAny<CancellationToken>()), times);
    }

    private static FullTrack PlayingTrack(string name, string albumId = "album-1") => new()
    {
        Name = name,
        Album = new SimpleAlbum { Id = albumId },
        Artists = [new SimpleArtist { Name = "Artist" }],
        TrackNumber = 4,
    };

    /// <summary>Nothing playing at all — as opposed to the momentary gap covered further down.</summary>
    [Fact]
    public async Task EnrichAsync_WithNothingPlaying_ReturnsFalseAndLeavesTheTrackAlone()
    {
        var harness = new Harness();
        harness.ReturnsPlayback(null); // 204 No Content deserializes to null.

        var track = DetectedTrack();
        var enriched = await harness.Provider.EnrichAsync(track);

        Assert.False(enriched);
        Assert.Equal("Title", track.Title);
        Assert.Null(track.AlbumPosition);
    }

    [Fact]
    public async Task EnrichAsync_WithAMatchingTrackAndNoAlbumId_MapsTheTrackOnly()
    {
        var harness = new Harness();
        var spotifyTrack = PlayingTrack("Title", albumId: "");
        harness.ReturnsPlayback(new CurrentlyPlaying { IsPlaying = true, Item = spotifyTrack });

        var track = DetectedTrack();
        var enriched = await harness.Provider.EnrichAsync(track);

        Assert.True(enriched);
        Assert.Equal(4, track.AlbumPosition);
        Assert.Null(track.Album);
        harness.Albums.Verify(x => x.Get(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnrichAsync_WithAMatchingTrack_MapsBothTrackAndAlbum()
    {
        var harness = new Harness();
        harness.ReturnsPlayback(new CurrentlyPlaying { IsPlaying = true, Item = PlayingTrack("Title") });
        harness.ReturnsAlbum("album-1", new FullAlbum
        {
            Name = "Album Name",
            Artists = [new SimpleArtist { Name = "Album Artist" }],
            Genres = ["Rock"],
            Images = [],
            ReleaseDate = "2020-01-01",
        });

        var track = DetectedTrack();
        var enriched = await harness.Provider.EnrichAsync(track);

        Assert.True(enriched);
        Assert.Equal("Title", track.Title);
        Assert.Equal(4, track.AlbumPosition);
        Assert.Equal("Album Name", track.Album);
        Assert.Equal(2020, track.Year);
    }

    /// <summary>
    /// The bug this retry exists for: at a track boundary the window title has already advanced
    /// while <c>currently-playing</c> is still serving the previous track, so the first answer is
    /// the wrong one and the guard rejects it.
    /// </summary>
    /// <remarks>
    /// Asking once meant every track whose boundary landed inside that lag was saved bare — the
    /// reference retried a second later for exactly this reason and the port dropped it.
    /// </remarks>
    [Fact]
    public async Task EnrichAsync_WhenSpotifyIsStillOnThePreviousTrack_AsksAgainAndTags()
    {
        var harness = new Harness();

        harness.ReturnsPlaybackInTurn(
            new CurrentlyPlaying { IsPlaying = true, Item = PlayingTrack("The Previous Song") },
            new CurrentlyPlaying { IsPlaying = true, Item = PlayingTrack("Title") });

        harness.ReturnsAlbum("album-1", new FullAlbum
        {
            Name = "Album Name",
            Artists = [],
            Genres = [],
            Images = [],
            ReleaseDate = "2020",
        });

        var track = DetectedTrack();
        var enriched = await harness.Provider.EnrichAsync(track);

        Assert.True(enriched);
        Assert.Equal("Album Name", track.Album);
        Assert.Equal(4, track.AlbumPosition);
    }

    /// <summary>A 204 at the boundary is the same race, not an answer of "nothing is playing".</summary>
    [Fact]
    public async Task EnrichAsync_WhenPlaybackIsMomentarilyEmpty_AsksAgainAndTags()
    {
        var harness = new Harness();

        harness.ReturnsPlaybackInTurn(
            null,
            new CurrentlyPlaying { IsPlaying = true, Item = PlayingTrack("Title", albumId: "") });

        var enriched = await harness.Provider.EnrichAsync(DetectedTrack());

        Assert.True(enriched);
    }

    /// <summary>The chase is bounded — a genuinely different track must not be waited on forever.</summary>
    [Fact]
    public async Task EnrichAsync_WhenTheMismatchPersists_GivesUpAfterTheAttemptBudget()
    {
        var harness = new Harness();
        harness.ReturnsPlayback(new CurrentlyPlaying { IsPlaying = true, Item = PlayingTrack("A Different Song") });

        var track = DetectedTrack("Title");
        var enriched = await harness.Provider.EnrichAsync(track);

        Assert.False(enriched);
        Assert.Equal("Title", track.Title);
        Assert.Null(track.AlbumPosition);

        harness.Player.Verify(
            x => x.GetCurrentlyPlaying(It.IsAny<PlayerCurrentlyPlayingRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(4));
    }

    /// <summary>
    /// A podcast episode is not a track and never will be, so retrying only wastes the deadline.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_WhenAPodcastEpisodeIsPlaying_DoesNotRetry()
    {
        var harness = new Harness();
        harness.ReturnsPlayback(new CurrentlyPlaying { IsPlaying = true, Item = new FullEpisode { Name = "Episode" } });

        Assert.False(await harness.Provider.EnrichAsync(DetectedTrack()));

        harness.Player.Verify(
            x => x.GetCurrentlyPlaying(It.IsAny<PlayerCurrentlyPlayingRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EnrichAsync_PassesCancellationThrough()
    {
        var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        harness.Player
            .Setup(x => x.GetCurrentlyPlaying(It.IsAny<PlayerCurrentlyPlayingRequest>(), cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Provider.EnrichAsync(DetectedTrack(), cancellation.Token));
    }

    /// <summary>
    /// A dead refresh token is the one API failure with a remedy, and the remedy is the user's:
    /// nothing this process does will revive it, so the host is told to clear it and ask for a
    /// fresh sign-in rather than retrying it on every track forever.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_WhenTheStoredSignInIsRejected_RaisesAuthorizationExpired()
    {
        var harness = new Harness();
        harness.Fails(HttpStatusCode.Unauthorized, "The access token expired");

        var provider = harness.Provider;
        var raised = 0;
        provider.AuthorizationExpired += (_, _) => raised++;

        Assert.False(await provider.EnrichAsync(DetectedTrack()));
        Assert.Equal(1, raised);
    }

    /// <summary>
    /// Every other API fault is downgraded to "no metadata". Tags are worth having; they are
    /// never worth failing a recording over.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task EnrichAsync_WhenTheApiFails_RecordsUntaggedInsteadOfThrowing(HttpStatusCode status)
    {
        var harness = new Harness();
        harness.Fails(status, "Something went wrong");

        Assert.False(await harness.Provider.EnrichAsync(DetectedTrack()));
    }

    /// <summary>
    /// Only a 401 means the sign-in is gone. Treating a rate limit or an outage as an expired
    /// token would sign the user out over a transient fault.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task EnrichAsync_WhenTheApiFailsOtherwise_LeavesTheSignInAlone(HttpStatusCode status)
    {
        var harness = new Harness();
        harness.Fails(status, "Something went wrong");

        var provider = harness.Provider;
        var raised = 0;
        provider.AuthorizationExpired += (_, _) => raised++;

        await provider.EnrichAsync(DetectedTrack());

        Assert.Equal(0, raised);
    }

    // ---- genre, which Spotify hangs off the artist rather than the track ----

    /// <summary>
    /// The reason this exists: Spotify stopped populating album genres for most of the
    /// catalogue, so the tag was empty on every Spotify-tagged recording.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_WhenTheAlbumHasNoGenres_TakesThemFromTheArtist()
    {
        var harness = new Harness();
        var spotifyTrack = PlayingTrack("Title");
        spotifyTrack.Artists = [new SimpleArtist { Id = "artist-1", Name = "Artist" }];

        harness.ReturnsPlayback(new CurrentlyPlaying { IsPlaying = true, Item = spotifyTrack });
        harness.ReturnsAlbum("album-1", new FullAlbum { Name = "Album", Genres = [] });
        harness.ReturnsArtist("artist-1", "trance", "eurodance");

        var track = DetectedTrack();
        await harness.Provider.EnrichAsync(track);

        Assert.Equal(["trance", "eurodance"], track.Genres!);
    }

    /// <summary>An album that does have genres is the better answer, and is not second-guessed.</summary>
    [Fact]
    public async Task EnrichAsync_WhenTheAlbumHasGenres_DoesNotAskTheArtist()
    {
        var harness = new Harness();
        var spotifyTrack = PlayingTrack("Title");
        spotifyTrack.Artists = [new SimpleArtist { Id = "artist-1", Name = "Artist" }];

        harness.ReturnsPlayback(new CurrentlyPlaying { IsPlaying = true, Item = spotifyTrack });
        harness.ReturnsAlbum("album-1", new FullAlbum { Name = "Album", Genres = ["ambient"] });

        var track = DetectedTrack();
        await harness.Provider.EnrichAsync(track);

        Assert.Equal(["ambient"], track.Genres!);
        harness.ArtistWasAskedFor("artist-1", Times.Never());
    }

    /// <summary>
    /// The cache earning its place: an album is one artist over and over, and without this each
    /// track spends a request against the shared rate limit to be told the same thing.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_AsksAboutAnArtistOncePerSession()
    {
        var harness = new Harness();
        var spotifyTrack = PlayingTrack("Title");
        spotifyTrack.Artists = [new SimpleArtist { Id = "artist-1", Name = "Artist" }];

        harness.ReturnsPlayback(new CurrentlyPlaying { IsPlaying = true, Item = spotifyTrack });
        harness.ReturnsAlbum("album-1", new FullAlbum { Name = "Album", Genres = [] });
        harness.ReturnsArtist("artist-1", "trance");

        var provider = harness.Provider;

        foreach (var _ in Enumerable.Range(0, 5))
        {
            await provider.EnrichAsync(DetectedTrack());
        }

        harness.ArtistWasAskedFor("artist-1", Times.Once());
    }

    /// <summary>An artist with no genres is cached too, or the miss is paid for on every track.</summary>
    [Fact]
    public async Task EnrichAsync_CachesAnArtistThatHasNoGenres()
    {
        var harness = new Harness();
        var spotifyTrack = PlayingTrack("Title");
        spotifyTrack.Artists = [new SimpleArtist { Id = "artist-1", Name = "Artist" }];

        harness.ReturnsPlayback(new CurrentlyPlaying { IsPlaying = true, Item = spotifyTrack });
        harness.ReturnsAlbum("album-1", new FullAlbum { Name = "Album", Genres = [] });
        harness.ReturnsArtist("artist-1");

        var provider = harness.Provider;
        await provider.EnrichAsync(DetectedTrack());
        var track = DetectedTrack();
        await provider.EnrichAsync(track);

        Assert.Empty(track.Genres!);
        harness.ArtistWasAskedFor("artist-1", Times.Once());
    }

    /// <summary>Spotify lists many for a well-known artist; the tag takes the first few.</summary>
    [Fact]
    public async Task EnrichAsync_TakesAtMostThreeArtistGenres()
    {
        var harness = new Harness();
        var spotifyTrack = PlayingTrack("Title");
        spotifyTrack.Artists = [new SimpleArtist { Id = "artist-1", Name = "Artist" }];

        harness.ReturnsPlayback(new CurrentlyPlaying { IsPlaying = true, Item = spotifyTrack });
        harness.ReturnsAlbum("album-1", new FullAlbum { Name = "Album", Genres = [] });
        harness.ReturnsArtist("artist-1", "trance", "eurodance", "progressive trance", "german trance");

        var track = DetectedTrack();
        await harness.Provider.EnrichAsync(track);

        Assert.Equal(["trance", "eurodance", "progressive trance"], track.Genres!);
    }

    /// <summary>No id to ask about means no request, rather than a request for nothing.</summary>
    [Fact]
    public async Task EnrichAsync_WithNoArtistId_LeavesGenresEmptyWithoutAsking()
    {
        var harness = new Harness();
        var spotifyTrack = PlayingTrack("Title");

        harness.ReturnsPlayback(new CurrentlyPlaying { IsPlaying = true, Item = spotifyTrack });
        harness.ReturnsAlbum("album-1", new FullAlbum { Name = "Album", Genres = [] });

        var track = DetectedTrack();
        await harness.Provider.EnrichAsync(track);

        Assert.Empty(track.Genres!);
        harness.Artists.Verify(x => x.Get(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    // ---- the floor holds when the provider cannot ----

    /// <summary>
    /// The failure this is really about: the match guard rejecting every attempt used to leave a
    /// recording with nothing, even though the media session had already said — with certainty,
    /// because it is the client playing the track — what the album and position were.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_WhenNothingMatches_LeavesTheDetectedMetadataIntact()
    {
        var harness = new Harness();
        harness.ReturnsPlayback(new CurrentlyPlaying
        {
            IsPlaying = true,
            Item = PlayingTrack("Some Entirely Different Song"),
        });

        var track = new Track
        {
            Artist = "Artist",
            Title = "Title",
            Album = "Detected Album",
            AlbumArtists = ["Detected Album Artist"],
            AlbumPosition = 7,
        };

        var enriched = await harness.Provider.EnrichAsync(track);

        Assert.False(enriched);
        Assert.Equal("Detected Album", track.Album);
        Assert.Equal(["Detected Album Artist"], track.AlbumArtists!);
        Assert.Equal(7, track.AlbumPosition);
    }

    /// <summary>An API fault is the same story: tags degrade to what the client already knew.</summary>
    [Fact]
    public async Task EnrichAsync_WhenSpotifyFails_LeavesTheDetectedMetadataIntact()
    {
        var harness = new Harness();
        harness.Fails(HttpStatusCode.InternalServerError, "Spotify is having a moment.");

        var track = new Track
        {
            Artist = "Artist",
            Title = "Title",
            Album = "Detected Album",
            AlbumArtists = ["Detected Album Artist"],
            AlbumPosition = 7,
        };

        await harness.Provider.EnrichAsync(track);

        Assert.Equal("Detected Album", track.Album);
        Assert.Equal(["Detected Album Artist"], track.AlbumArtists!);
        Assert.Equal(7, track.AlbumPosition);
    }

    /// <summary>
    /// A partial answer fills its gaps from the floor rather than blanking them: Spotify knew the
    /// album but reported no position, so the media session's position stands.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_WithAnAnswerThatHasNoPosition_KeepsTheDetectedOne()
    {
        var harness = new Harness();
        var spotifyTrack = PlayingTrack("Title");
        spotifyTrack.TrackNumber = 0; // The SDK's "not populated".

        harness.ReturnsPlayback(new CurrentlyPlaying { IsPlaying = true, Item = spotifyTrack });
        harness.ReturnsAlbum("album-1", new FullAlbum { Name = "Real Album", Genres = [], Images = [] });

        var track = new Track { Artist = "Artist", Title = "Title", AlbumPosition = 7 };
        await harness.Provider.EnrichAsync(track);

        Assert.Equal("Real Album", track.Album);
        Assert.Equal(7, track.AlbumPosition);
    }
}
