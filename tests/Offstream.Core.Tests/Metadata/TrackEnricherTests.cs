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
}
