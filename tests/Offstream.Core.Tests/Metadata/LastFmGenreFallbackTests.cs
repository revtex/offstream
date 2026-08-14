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
/// electronic and dance. Asking only about the track threw away a perfectly good answer sitting
/// one request behind it.
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

    /// <summary>Track tags are the better answer, so they are asked for first and used when present.</summary>
    [Fact]
    public async Task GetGenresAsync_PrefersTheTracksOwnTags()
    {
        using var handler = StubHttpMessageHandler.Xml(Tags("big beat", "breakbeat"));
        using var httpClient = new HttpClient(handler);

        Assert.Equal(["big beat", "breakbeat"], await FallbackOver(httpClient).GetGenresAsync(Detected()));
    }

    /// <summary>The regression: an empty track cloud falls through to the artist's tags.</summary>
    [Fact]
    public async Task GetGenresAsync_WhenTheTrackHasNoTags_FallsBackToTheArtists()
    {
        using var handler = StubHttpMessageHandler.Xml(NoTags, Tags("trance", "electronic", "dance"));
        using var httpClient = new HttpClient(handler);

        Assert.Equal(
            ["trance", "electronic", "dance"],
            await FallbackOver(httpClient).GetGenresAsync(Detected()));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("track.getTopTags", handler.Requests[0].Query, StringComparison.Ordinal);
        Assert.Contains("artist.getTopTags", handler.Requests[1].Query, StringComparison.Ordinal);
    }

    /// <summary>The last rung really is the last one: nothing anywhere means an empty tag.</summary>
    [Fact]
    public async Task GetGenresAsync_WhenNeitherHasTags_ReturnsEmpty()
    {
        using var handler = StubHttpMessageHandler.Xml(NoTags, NoTags);
        using var httpClient = new HttpClient(handler);

        Assert.Empty(await FallbackOver(httpClient).GetGenresAsync(Detected()));
    }

    /// <summary>Last.fm's clouds run long; the tag takes the same few the rest of the app does.</summary>
    [Fact]
    public async Task GetGenresAsync_TakesAtMostThree()
    {
        using var handler = StubHttpMessageHandler.Xml(Tags("trance", "electronic", "dance", "techno", "german"));
        using var httpClient = new HttpClient(handler);

        Assert.Equal(["trance", "electronic", "dance"], await FallbackOver(httpClient).GetGenresAsync(Detected()));
    }

    /// <summary>
    /// With no title there is no track to ask about, but the artist alone is still a question
    /// worth asking — so it goes straight to the second rung rather than giving up.
    /// </summary>
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
