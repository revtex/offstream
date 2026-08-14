using System.Net.Http;
using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Providers;
using Xunit;

namespace Offstream.Core.Tests.Metadata;

/// <summary>
/// Genre from Last.fm, asked for on its own.
/// </summary>
/// <remarks>
/// The case these exist for is real and was found in the wild: ATB's "9Pm (Till I Come)" has no
/// track tags on Last.fm at all — an empty cloud, not an error — while the artist carries trance,
/// electronic and dance. That was first fixed by asking about the track and falling through to
/// the artist; it now asks about the artist only, which is the same answer for one request
/// instead of two, consistent across an album, and consistent with Spotify — where genre is an
/// attribute of the artist and there is no track-level answer to prefer.
/// </remarks>
public sealed class LastFmGenreFallbackTests
{
    private const string ApiKey = "key";

    private static string Tags(params string[] names) =>
        $"<lfm status=\"ok\"><toptags>{string.Concat(names.Select(n => $"<tag><name>{n}</name></tag>"))}</toptags></lfm>";

    private static readonly string NoTags = Tags();

    private static Track Detected() => new() { Artist = "ATB", Title = "9Pm (Till I Come)" };

    private static LastFmGenreFallback FallbackOver(HttpClient httpClient) =>
        new(new LastFmMetadataProvider(httpClient, ApiKey));

    /// <summary>
    /// The artist is the only thing asked about — one request, and never <c>track.getTopTags</c>.
    /// </summary>
    [Fact]
    public async Task GetGenresAsync_AsksAboutTheArtistOnly()
    {
        using var handler = StubHttpMessageHandler.Xml(Tags("trance", "electronic", "dance"));
        using var httpClient = new HttpClient(handler);

        Assert.Equal(
            ["trance", "electronic", "dance"],
            await FallbackOver(httpClient).GetGenresAsync(Detected()));

        var request = Assert.Single(handler.Requests);
        Assert.Contains("artist.getTopTags", request.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("track.getTopTags", request.Query, StringComparison.Ordinal);
    }

    /// <summary>An artist Last.fm has no tags for means an empty tag, not a second question.</summary>
    [Fact]
    public async Task GetGenresAsync_WhenTheArtistHasNoTags_ReturnsEmpty()
    {
        using var handler = StubHttpMessageHandler.Xml(NoTags);
        using var httpClient = new HttpClient(handler);

        Assert.Empty(await FallbackOver(httpClient).GetGenresAsync(Detected()));
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// An album is one artist repeated, so the per-artist cache is what keeps a fifteen-track
    /// album to a single request — misses included, since an untagged artist stays untagged.
    /// </summary>
    [Fact]
    public async Task GetGenresAsync_AsksAboutEachArtistOnce()
    {
        using var handler = StubHttpMessageHandler.Xml(Tags("trance"));
        using var httpClient = new HttpClient(handler);

        var fallback = FallbackOver(httpClient);

        Assert.Equal(["trance"], await fallback.GetGenresAsync(Detected()));
        Assert.Equal(["trance"], await fallback.GetGenresAsync(new Track { Artist = "ATB", Title = "Killer" }));

        Assert.Single(handler.Requests);
    }

    /// <summary>Last.fm's clouds run long; the tag takes the same few the rest of the app does.</summary>
    [Fact]
    public async Task GetGenresAsync_TakesAtMostThree()
    {
        using var handler = StubHttpMessageHandler.Xml(Tags("trance", "electronic", "dance", "techno", "german"));
        using var httpClient = new HttpClient(handler);

        Assert.Equal(["trance", "electronic", "dance"], await FallbackOver(httpClient).GetGenresAsync(Detected()));
    }

    /// <summary>The title is not part of the question, so a track without one is not a problem.</summary>
    [Fact]
    public async Task GetGenresAsync_WithNoTitle_StillAsksAboutTheArtist()
    {
        using var handler = StubHttpMessageHandler.Xml(Tags("trance"));
        using var httpClient = new HttpClient(handler);

        var track = new Track { Artist = "ATB" };

        Assert.Equal(["trance"], await FallbackOver(httpClient).GetGenresAsync(track));

        Assert.Contains("artist.getTopTags", Assert.Single(handler.Requests).Query, StringComparison.Ordinal);
    }

    /// <summary>Nothing to ask about at all, so no request is made.</summary>
    [Fact]
    public async Task GetGenresAsync_WithNoArtist_ReturnsEmptyWithoutAsking()
    {
        using var handler = StubHttpMessageHandler.Xml(Tags("trance"));
        using var httpClient = new HttpClient(handler);

        Assert.Empty(await FallbackOver(httpClient).GetGenresAsync(new Track { Title = "Title" }));
        Assert.Empty(handler.Requests);
    }

    /// <summary>The log names the source, so it has to be the one actually answering.</summary>
    [Fact]
    public void Kind_IsLastFm()
    {
        using var httpClient = new HttpClient(StubHttpMessageHandler.Xml(NoTags));

        Assert.Equal(MetadataProvider.LastFm, FallbackOver(httpClient).Kind);
    }

    [Fact]
    public void Constructor_RejectsNulls() =>
        Assert.Throws<ArgumentNullException>(() => new LastFmGenreFallback(null!));
}
