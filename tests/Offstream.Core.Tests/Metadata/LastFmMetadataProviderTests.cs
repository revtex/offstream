using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Providers;
using Xunit;

namespace Offstream.Core.Tests.Metadata;

/// <summary>
/// The Last.fm lookup, against scripted responses.
/// </summary>
/// <remarks>
/// The two behaviours worth pinning are the ones the reference implementation had and that make
/// the common case work at all: retrying with the title stripped of its Spotify decoration, and
/// looking a single up on its own when the track response has no usable album. Both are easy to
/// drop in a rewrite and invisible until half the library comes back untagged.
/// </remarks>
public sealed class LastFmMetadataProviderTests
{
    private const string ApiKey = "test-key";

    private static string TrackResponse(
        string trackName = "Title",
        string artistName = "Artist",
        string? albumTitle = "Album",
        string albumArtist = "Artist",
        int durationMs = 214000) =>
        $"""
         <lfm status="ok">
           <track>
             <name>{trackName}</name>
             <duration>{durationMs}</duration>
             <artist><name>{artistName}</name></artist>
             {(albumTitle is null ? string.Empty : $"""
               <album position="4">
                 <artist>{albumArtist}</artist>
                 <title>{albumTitle}</title>
                 <image size="medium">https://example.invalid/300x300/cover.jpg</image>
                 <image size="extralarge">https://example.invalid/1200/cover.jpg</image>
               </album>
               """)}
           </track>
         </lfm>
         """;

    private static string AlbumResponse(string name = "Single", string artist = "Artist") =>
        $"""
         <lfm status="ok">
           <album>
             <name>{name}</name>
             <artist>{artist}</artist>
             <image size="medium">https://example.invalid/300x300/single.jpg</image>
           </album>
         </lfm>
         """;

    private static Track Detected(string title = "Title") => new() { Artist = "Artist", Title = title };

    [Fact]
    public async Task EnrichAsync_WritesTheAlbumAndItsPositionOntoTheTrack()
    {
        using var handler = StubHttpMessageHandler.Xml(TrackResponse());
        using var httpClient = handler.Client();

        var track = Detected();
        var enriched = await new LastFmMetadataProvider(httpClient, ApiKey).EnrichAsync(track);

        Assert.True(enriched);
        Assert.Equal("Album", track.Album);
        Assert.Equal(4, track.AlbumPosition);

        // Milliseconds in, seconds out.
        Assert.Equal(214, track.Length);
    }

    /// <summary>The 300px variant is preferred over the larger one; see the mapper's own note.</summary>
    [Fact]
    public async Task EnrichAsync_ChoosesTheCoverArtUrlTheMapperPrefers()
    {
        using var handler = StubHttpMessageHandler.Xml(TrackResponse());
        using var httpClient = handler.Client();

        var track = Detected();
        await new LastFmMetadataProvider(httpClient, ApiKey).EnrichAsync(track);

        Assert.Equal("https://example.invalid/300x300/cover.jpg", track.AlbumArtUrl);
    }

    [Fact]
    public async Task EnrichAsync_SendsTheArtistTitleAndKey()
    {
        using var handler = StubHttpMessageHandler.Xml(TrackResponse());
        using var httpClient = handler.Client();

        await new LastFmMetadataProvider(httpClient, ApiKey).EnrichAsync(
            new Track { Artist = "Sigur Rós", Title = "Hoppípolla" });

        // The first request, not the only one: this fixture carries no tag cloud, so the provider
        // goes on to ask about the artist as well. That second question is covered by
        // LastFmGenreFallbackTests; this one is about the shape of the lookup that opens it.
        var query = handler.Requests[0].Query;

        Assert.Contains("method=track.getInfo", query, StringComparison.Ordinal);
        Assert.Contains($"api_key={ApiKey}", query, StringComparison.Ordinal);
        Assert.Contains("artist=Sigur%20R%C3%B3s", query, StringComparison.Ordinal);
        Assert.Contains("track=Hopp%C3%ADpolla", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spotify decorates titles in ways Last.fm's catalogue does not carry. Asking again for the
    /// bare title is what makes remasters and live versions resolve at all.
    /// </summary>
    [Theory]
    [InlineData("Title (Remastered 2011)", "Title")]
    [InlineData("Title - Live at Wembley", "Title")]
    public async Task EnrichAsync_WhenTheDecoratedTitleHasNoAlbum_RetriesWithTheBareTitle(
        string detected, string expectedRetry)
    {
        using var handler = StubHttpMessageHandler.Xml(
            TrackResponse(albumTitle: null),

            // The single lookup that a track with no album triggers; it finds nothing either.
            """<lfm status="ok" />""",
            TrackResponse(albumTitle: "Album"));

        using var httpClient = handler.Client();

        var track = Detected(detected);
        var enriched = await new LastFmMetadataProvider(httpClient, ApiKey).EnrichAsync(track);

        Assert.True(enriched);
        Assert.Equal("Album", track.Album);

        var retry = Assert.Single(
            handler.Requests,
            uri => uri.Query.EndsWith($"track={expectedRetry}", StringComparison.Ordinal));

        Assert.Contains("method=track.getInfo", retry.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Retrying is bounded, and an undecorated title is not asked for twice.
    /// </summary>
    /// <remarks>
    /// The reference compared the stripped title against the one already forced — null on the
    /// first attempt — so every track whose title carried no decoration produced a second,
    /// byte-identical request that could not return anything new.
    /// </remarks>
    [Fact]
    public async Task EnrichAsync_WhenTheTitleHasNoDecorationToStrip_DoesNotAskTwice()
    {
        using var handler = StubHttpMessageHandler.Xml(TrackResponse(albumTitle: null));
        using var httpClient = handler.Client();

        var enriched = await new LastFmMetadataProvider(httpClient, ApiKey).EnrichAsync(Detected());

        Assert.False(enriched);

        // One track lookup, plus the single lookup a missing album triggers - and no repeat.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(handler.Requests, uri => uri.Query.Contains("track.getInfo", StringComparison.Ordinal));
    }

    /// <summary>
    /// A compilation appearance is attributed to "Various Artists", which is worse than the
    /// single's own name — so the single is looked up on its own.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_WhenTheAlbumIsVariousArtists_FallsBackToTheSingle()
    {
        using var handler = StubHttpMessageHandler.Xml(
            TrackResponse(albumTitle: "Now That's What I Call Music", albumArtist: "Various Artists"),
            AlbumResponse());

        using var httpClient = handler.Client();

        var track = Detected();
        var enriched = await new LastFmMetadataProvider(httpClient, ApiKey).EnrichAsync(track);

        Assert.True(enriched);
        Assert.Equal("Single", track.Album);
        Assert.Equal(1, track.AlbumPosition);
        Assert.Contains("method=album.getInfo", handler.Requests[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnrichAsync_WhenLastFmReportsAnError_WritesNothing()
    {
        using var handler = StubHttpMessageHandler.Xml(
            """<lfm status="failed"><error code="6">Track not found</error></lfm>""");

        using var httpClient = handler.Client();

        var track = Detected();

        Assert.False(await new LastFmMetadataProvider(httpClient, ApiKey).EnrichAsync(track));
        Assert.Null(track.Album);
    }

    /// <summary>An outage costs the tags, never the recording.</summary>
    [Fact]
    public async Task EnrichAsync_WhenTheRequestFails_ReturnsFalseRatherThanThrowing()
    {
        using var handler = StubHttpMessageHandler.Failing();
        using var httpClient = handler.Client();

        Assert.False(await new LastFmMetadataProvider(httpClient, ApiKey).EnrichAsync(Detected()));
    }

    [Fact]
    public async Task EnrichAsync_WhenTheResponseIsNotXml_ReturnsFalseRatherThanThrowing()
    {
        using var handler = StubHttpMessageHandler.Xml("not xml at all");
        using var httpClient = handler.Client();

        Assert.False(await new LastFmMetadataProvider(httpClient, ApiKey).EnrichAsync(Detected()));
    }

    /// <summary>Nothing to look a track up by means no request at all.</summary>
    [Theory]
    [InlineData(null, "Title")]
    [InlineData("Artist", null)]
    public async Task EnrichAsync_WithoutAnArtistAndTitle_MakesNoRequest(string? artist, string? title)
    {
        using var handler = StubHttpMessageHandler.Xml(TrackResponse());
        using var httpClient = handler.Client();

        var enriched = await new LastFmMetadataProvider(httpClient, ApiKey)
            .EnrichAsync(new Track { Artist = artist, Title = title });

        Assert.False(enriched);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Kind_IsLastFm()
    {
        using var handler = StubHttpMessageHandler.Xml(TrackResponse());
        using var httpClient = handler.Client();

        Assert.Equal(MetadataProvider.LastFm, new LastFmMetadataProvider(httpClient, ApiKey).Kind);
    }

    [Fact]
    public void Constructor_WithoutAnApiKey_IsRefused()
    {
        using var handler = StubHttpMessageHandler.Xml(TrackResponse());
        using var httpClient = handler.Client();

        Assert.Throws<ArgumentException>(() => new LastFmMetadataProvider(httpClient, " "));
    }

    /// <summary>
    /// As the chosen provider it has to come back with a genre, not just an album.
    /// </summary>
    /// <remarks>
    /// The mapper reads genres from the track's own tag cloud, which Last.fm leaves empty for a
    /// great many tracks — ATB's "9Pm (Till I Come)" among them, which is the case that found
    /// this. Without the artist's tags behind it, Last.fm-as-primary tagged album, position and
    /// artwork correctly and then handed back no genre at all.
    /// </remarks>
    [Fact]
    public async Task EnrichAsync_WhenTheTrackHasNoTags_TakesGenresFromTheArtist()
    {
        using var handler = StubHttpMessageHandler.Xml(
            TrackResponse(),
            """<lfm status="ok"><toptags><tag><name>trance</name></tag></toptags></lfm>""");

        using var httpClient = handler.Client();

        var track = Detected();
        await new LastFmMetadataProvider(httpClient, ApiKey).EnrichAsync(track);

        Assert.Equal(["trance"], track.Genres!);
        Assert.Contains("artist.getTopTags", handler.Requests[^1].Query, StringComparison.Ordinal);
    }

    /// <summary>An artist is asked about once a session, however many of its tracks are recorded.</summary>
    [Fact]
    public async Task EnrichAsync_AsksAboutAnArtistsTagsOncePerSession()
    {
        using var handler = StubHttpMessageHandler.Xml(
            TrackResponse(),
            """<lfm status="ok"><toptags><tag><name>trance</name></tag></toptags></lfm>""",
            TrackResponse());

        using var httpClient = handler.Client();
        var provider = new LastFmMetadataProvider(httpClient, ApiKey);

        await provider.EnrichAsync(Detected());
        var second = Detected();
        await provider.EnrichAsync(second);

        Assert.Equal(["trance"], second.Genres!);
        Assert.Single(handler.Requests, r => r.Query.Contains("artist.getTopTags", StringComparison.Ordinal));
    }
}
