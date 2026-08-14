using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Providers;
using Xunit;

namespace Offstream.Core.Tests.Metadata;

/// <summary>
/// The contract the recording pipeline depends on: enrichment never throws, and never outlasts
/// its deadline.
/// </summary>
/// <remarks>
/// A recording is on disk by the time this is joined. Everything here is about making sure that
/// recording still becomes a file when the provider misbehaves — an outage, a hang, or an
/// exception nobody anticipated.
/// </remarks>
public sealed class TrackEnricherTests
{
    private static Track Detected() => new() { Artist = "Artist", Title = "Title" };

    private sealed class FakeProvider(MetadataProvider kind = MetadataProvider.LastFm) : IMetadataProvider
    {
        public MetadataProvider Kind { get; } = kind;

        /// <summary>What <see cref="EnrichAsync"/> writes onto the track before returning.</summary>
        public Action<Track>? Apply { get; set; }

        public bool Result { get; set; } = true;

        public Exception? Failure { get; set; }

        /// <summary>Blocks until cancelled, for the deadline case.</summary>
        public bool Hangs { get; set; }

        public int Calls { get; private set; }

        public async Task<bool> EnrichAsync(Track track, CancellationToken cancellationToken = default)
        {
            Calls++;

            if (Failure is not null) throw Failure;

            if (Hangs) await Task.Delay(Timeout.Infinite, cancellationToken);

            Apply?.Invoke(track);

            return Result;
        }
    }

    private sealed class FakeGenreFallback : IGenreFallback
    {
        public string[] Result { get; set; } = ["fallback genre"];

        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public Task<string[]> GetGenresAsync(Track track, CancellationToken cancellationToken = default)
        {
            Calls++;

            return Failure is not null
                ? Task.FromException<string[]>(Failure)
                : Task.FromResult(Result);
        }
    }

    private sealed class FakeCoverArtFetcher : ICoverArtFetcher
    {
        public string? Result { get; set; } = @"C:\Temp\cover.jpg";

        public int Calls { get; private set; }

        public Task<string?> FetchAsync(Track track, CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task EnrichAsync_AppliesTheProviderAndFetchesTheArt()
    {
        var provider = new FakeProvider { Apply = track => track.Album = "Album" };
        var coverArt = new FakeCoverArtFetcher();

        var track = Detected();
        var result = await new TrackEnricher(provider, coverArt).EnrichAsync(track);

        Assert.True(result.Updated);
        Assert.Equal(@"C:\Temp\cover.jpg", result.CoverArtPath);
        Assert.Equal("Album", track.Album);
        Assert.True(track.MetadataUpdated);
    }

    /// <summary>No metadata means no art either; there is no URL to fetch from.</summary>
    [Fact]
    public async Task EnrichAsync_WhenTheProviderFindsNothing_DoesNotFetchArt()
    {
        var provider = new FakeProvider { Result = false };
        var coverArt = new FakeCoverArtFetcher();

        var result = await new TrackEnricher(provider, coverArt).EnrichAsync(Detected());

        Assert.False(result.Updated);
        Assert.Null(result.CoverArtPath);
        Assert.Equal(0, coverArt.Calls);
    }

    /// <summary>"No provider" costs nothing at all, not even a call.</summary>
    [Fact]
    public async Task EnrichAsync_WithTheNoneProvider_DoesNothing()
    {
        var provider = new FakeProvider(MetadataProvider.None);
        var coverArt = new FakeCoverArtFetcher();

        var result = await new TrackEnricher(provider, coverArt).EnrichAsync(Detected());

        Assert.False(result.Updated);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, coverArt.Calls);
    }

    [Fact]
    public async Task EnrichAsync_WhenTheProviderThrows_ReportsNothingRatherThanFailing()
    {
        var provider = new FakeProvider { Failure = new InvalidOperationException("provider is broken") };

        var result = await new TrackEnricher(provider, new FakeCoverArtFetcher()).EnrichAsync(Detected());

        Assert.False(result.Updated);
        Assert.Null(result.CoverArtPath);
    }

    /// <summary>
    /// A provider that has stopped answering must not hold a finished recording. The deadline is
    /// the whole reason enrichment can be joined synchronously before the encode is queued.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_WhenTheProviderHangs_GivesUpAtTheDeadline()
    {
        var provider = new FakeProvider { Hangs = true };

        var result = await new TrackEnricher(provider, new FakeCoverArtFetcher(), TimeSpan.FromMilliseconds(50))
            .EnrichAsync(Detected());

        Assert.False(result.Updated);
    }

    /// <summary>Session teardown is not a provider failure, and is not reported as one.</summary>
    [Fact]
    public async Task EnrichAsync_WhenTheSessionStops_ReportsNothing()
    {
        var provider = new FakeProvider { Hangs = true };
        using var stopping = new CancellationTokenSource();

        await stopping.CancelAsync();

        var result = await new TrackEnricher(provider, new FakeCoverArtFetcher())
            .EnrichAsync(Detected(), stopping.Token);

        Assert.False(result.Updated);
    }

    // ---- genre fallback: Spotify's artist genres first, then Last.fm, then nothing ----

    /// <summary>The gap this fills — the provider tagged the track but had no genre for it.</summary>
    [Fact]
    public async Task EnrichAsync_WhenTheProviderLeavesGenresEmpty_TakesThemFromTheFallback()
    {
        var provider = new FakeProvider { Apply = track => track.Album = "Album" };
        var fallback = new FakeGenreFallback { Result = ["trip hop"] };

        var track = Detected();
        await new TrackEnricher(provider, new FakeCoverArtFetcher(), deadline: null, fallback)
            .EnrichAsync(track);

        Assert.Equal(["trip hop"], track.Genres!);
        Assert.Equal(1, fallback.Calls);
    }

    /// <summary>A provider that answered is not second-guessed, and costs no second request.</summary>
    [Fact]
    public async Task EnrichAsync_WhenTheProviderSuppliedGenres_DoesNotAskTheFallback()
    {
        var provider = new FakeProvider { Apply = track => track.Genres = ["shoegaze"] };
        var fallback = new FakeGenreFallback();

        var track = Detected();
        await new TrackEnricher(provider, new FakeCoverArtFetcher(), deadline: null, fallback)
            .EnrichAsync(track);

        Assert.Equal(["shoegaze"], track.Genres!);
        Assert.Equal(0, fallback.Calls);
    }

    /// <summary>
    /// A bare recording is not worth a second request for one tag, so the fallback is a
    /// success-path step only.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_WhenTheProviderFoundNothing_DoesNotAskTheFallback()
    {
        var provider = new FakeProvider { Result = false };
        var fallback = new FakeGenreFallback();

        await new TrackEnricher(provider, new FakeCoverArtFetcher(), deadline: null, fallback)
            .EnrichAsync(Detected());

        Assert.Equal(0, fallback.Calls);
    }

    /// <summary>The final rung: nobody has a genre, and the rest of the tags still stand.</summary>
    [Fact]
    public async Task EnrichAsync_WhenTheFallbackHasNoGenresEither_LeavesTheTagEmpty()
    {
        var provider = new FakeProvider { Apply = track => track.Album = "Album" };
        var fallback = new FakeGenreFallback { Result = [] };

        var track = Detected();
        var result = await new TrackEnricher(provider, new FakeCoverArtFetcher(), deadline: null, fallback)
            .EnrichAsync(track);

        Assert.True(result.Updated);
        Assert.Equal("Album", track.Album);
        Assert.True(track.Genres is null or { Length: 0 });
    }

    /// <summary>
    /// The tail must not wag the dog: a genre lookup that throws cannot cost the album, the year
    /// and the cover art that the primary provider already established.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_WhenTheFallbackFails_KeepsEverythingElse()
    {
        var provider = new FakeProvider { Apply = track => track.Album = "Album" };
        var fallback = new FakeGenreFallback { Failure = new InvalidOperationException("boom") };
        var coverArt = new FakeCoverArtFetcher();

        var track = Detected();
        var result = await new TrackEnricher(provider, coverArt, deadline: null, fallback)
            .EnrichAsync(track);

        Assert.True(result.Updated);
        Assert.Equal("Album", track.Album);
        Assert.Equal(@"C:\Temp\cover.jpg", result.CoverArtPath);
    }

    /// <summary>With nothing configured the chain is one rung, and nothing changes.</summary>
    [Fact]
    public async Task EnrichAsync_WithNoFallbackConfigured_StillEnriches()
    {
        var provider = new FakeProvider { Apply = track => track.Album = "Album" };

        var track = Detected();
        var result = await new TrackEnricher(provider, new FakeCoverArtFetcher()).EnrichAsync(track);

        Assert.True(result.Updated);
        Assert.Equal("Album", track.Album);
    }

    // ---- what the activity log says about genre ----

    /// <summary>
    /// The reason this is on the line at all: genre may have come from the provider named in the
    /// message or from the fallback, and before it was printed the only way to know it had been
    /// written was to run ffprobe over the finished file.
    /// </summary>
    [Fact]
    public void DescribeGenres_JoinsWhatWasWritten() =>
        Assert.Equal("trance, eurodance", TrackEnricher.DescribeGenres(["trance", "eurodance"]));

    [Fact]
    public void DescribeGenres_WithASingleGenre_PrintsItAlone() =>
        Assert.Equal("trance", TrackEnricher.DescribeGenres(["trance"]));

    /// <summary>
    /// "none" rather than the "unknown" the album and position use — it is a different answer.
    /// Those are missing from a reply that arrived; this means every source was asked and none
    /// had one, which is exactly the distinction someone debugging tagging needs.
    /// </summary>
    [Fact]
    public void DescribeGenres_WithNoGenresWritten_SaysNone() =>
        Assert.Equal("none", TrackEnricher.DescribeGenres([]));

    /// <inheritdoc cref="DescribeGenres_WithNoGenresWritten_SaysNone" />
    [Fact]
    public void DescribeGenres_WithNoGenreFieldAtAll_SaysNone() =>
        Assert.Equal("none", TrackEnricher.DescribeGenres(null));
}
