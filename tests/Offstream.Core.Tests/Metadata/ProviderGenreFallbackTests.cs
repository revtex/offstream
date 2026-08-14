using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Providers;
using Xunit;

namespace Offstream.Core.Tests.Metadata;

/// <summary>
/// Borrowing one field from a second provider, and only that field.
/// </summary>
/// <remarks>
/// The hazard this is written against: the second provider is a full metadata provider whose
/// mapper writes album, year, cover art and track number as readily as it writes genres. Letting
/// it near the real track would mix two catalogues' idea of the same release into one file —
/// Spotify's album with Last.fm's artwork, or a year from a different pressing.
/// </remarks>
public sealed class ProviderGenreFallbackTests
{
    private sealed class FakeProvider(MetadataProvider kind = MetadataProvider.LastFm) : IMetadataProvider
    {
        public MetadataProvider Kind { get; } = kind;

        public Action<Track>? Apply { get; set; }

        public Track? Seen { get; private set; }

        public int Calls { get; private set; }

        public Task<bool> EnrichAsync(Track track, CancellationToken cancellationToken = default)
        {
            Calls++;
            Seen = track;
            Apply?.Invoke(track);

            return Task.FromResult(true);
        }
    }

    private static Track Detected() => new()
    {
        Artist = "Artist",
        Title = "Title",
        Album = "The Right Album",
        Year = 1999,
        Playing = true,
    };

    [Fact]
    public async Task GetGenresAsync_ReturnsWhatTheSecondProviderFound()
    {
        var provider = new FakeProvider { Apply = track => track.Genres = ["downtempo", "trip hop"] };

        var genres = await new ProviderGenreFallback(provider).GetGenresAsync(Detected());

        Assert.Equal(["downtempo", "trip hop"], genres);
    }

    /// <summary>The whole point: everything the second provider writes except genre is discarded.</summary>
    [Fact]
    public async Task GetGenresAsync_LeavesTheRealTrackUntouched()
    {
        var provider = new FakeProvider
        {
            Apply = track =>
            {
                track.Album = "A Different Pressing";
                track.Year = 2011;
                track.AlbumArtUrl = "https://example.invalid/other.jpg";
                track.Genres = ["dub"];
            },
        };

        var track = Detected();
        await new ProviderGenreFallback(provider).GetGenresAsync(track);

        Assert.Equal("The Right Album", track.Album);
        Assert.Equal(1999, track.Year);
        Assert.Null(track.AlbumArtUrl);
        Assert.Null(track.Genres);
    }

    /// <summary>A lookup needs the artist and title, and nothing else is worth copying.</summary>
    [Fact]
    public async Task GetGenresAsync_AsksAboutTheSameRecording()
    {
        var provider = new FakeProvider();

        await new ProviderGenreFallback(provider).GetGenresAsync(Detected());

        Assert.NotNull(provider.Seen);
        Assert.Equal("Artist", provider.Seen!.Artist);
        Assert.Equal("Title", provider.Seen.Title);
    }

    [Fact]
    public async Task GetGenresAsync_WhenTheProviderFindsNoGenres_ReturnsEmpty()
    {
        var provider = new FakeProvider();

        var genres = await new ProviderGenreFallback(provider).GetGenresAsync(Detected());

        Assert.Empty(genres);
    }

    /// <summary>Nothing configured behind it means no request at all.</summary>
    [Fact]
    public async Task GetGenresAsync_WithNoProvider_ReturnsEmptyWithoutAsking()
    {
        var provider = new FakeProvider(MetadataProvider.None);

        var genres = await new ProviderGenreFallback(provider).GetGenresAsync(Detected());

        Assert.Empty(genres);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public void Constructor_RejectsNulls() =>
        Assert.Throws<ArgumentNullException>(() => new ProviderGenreFallback(null!));
}
