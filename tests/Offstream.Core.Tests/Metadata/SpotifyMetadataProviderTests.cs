using Moq;
using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Providers;
using SpotifyAPI.Web;
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

        public Harness()
        {
            Client.SetupGet(x => x.Player).Returns(Player.Object);
            Client.SetupGet(x => x.Albums).Returns(Albums.Object);
        }

        public SpotifyMetadataProvider Provider => new(Client.Object);

        public void ReturnsPlayback(CurrentlyPlaying? playback) =>
            Player
                .Setup(x => x.GetCurrentlyPlaying(It.IsAny<PlayerCurrentlyPlayingRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(playback!);

        public void ReturnsAlbum(string albumId, FullAlbum album) =>
            Albums.Setup(x => x.Get(albumId, It.IsAny<CancellationToken>())).ReturnsAsync(album);
    }

    private static FullTrack PlayingTrack(string name, string albumId = "album-1") => new()
    {
        Name = name,
        Album = new SimpleAlbum { Id = albumId },
        Artists = [new SimpleArtist { Name = "Artist" }],
        TrackNumber = 4,
    };

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

    /// <summary><see cref="CurrentlyPlaying.Item"/> can be an episode; only tracks are mapped.</summary>
    [Fact]
    public async Task EnrichAsync_WhenAPodcastEpisodeIsPlaying_ReturnsFalse()
    {
        var harness = new Harness();
        harness.ReturnsPlayback(new CurrentlyPlaying { IsPlaying = true, Item = new FullEpisode { Name = "Episode" } });

        var enriched = await harness.Provider.EnrichAsync(DetectedTrack());

        Assert.False(enriched);
    }

    /// <summary>
    /// Detection and this enrichment race independently: by the time this call returns, the
    /// window title may already have moved on. Mapping mismatched metadata onto the wrong
    /// track would be worse than mapping nothing.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_WhenSpotifyReportsADifferentTrack_ReturnsFalseAndLeavesTheTrackAlone()
    {
        var harness = new Harness();
        harness.ReturnsPlayback(new CurrentlyPlaying { IsPlaying = true, Item = PlayingTrack("A Different Song") });

        var track = DetectedTrack("Title");
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
}
