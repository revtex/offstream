using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Library;
using Offstream.Core.Metadata.Providers;
using Xunit;

namespace Offstream.Core.Tests.Metadata.Library;

/// <summary>
/// Preferring Spotify, falling back to Last.fm, and telling a broken source apart from an
/// empty catalogue.
/// </summary>
public sealed class FallbackMetadataProviderTests
{
    /// <summary>The first provider to answer wins, and the rest are never asked.</summary>
    /// <remarks>
    /// Not merely an optimisation. Merging two catalogues' answers produces a track with one
    /// service's album and another's year, which is worse than either alone and impossible to
    /// explain when the user asks where a wrong tag came from.
    /// </remarks>
    [Fact]
    public async Task Enrich_StopsAtTheFirstProviderThatMatches()
    {
        var second = new StubProvider(MetadataProvider.LastFm) { Result = true };
        var chain = new FallbackMetadataProvider(
            new StubProvider(MetadataProvider.Spotify) { Result = true },
            second);

        Assert.True(await chain.EnrichAsync(new Track()));
        Assert.Equal(0, second.Calls);
    }

    /// <summary>A provider that finds nothing hands over to the next one.</summary>
    [Fact]
    public async Task Enrich_FallsBackWhenThePreferredProviderHasNoMatch()
    {
        var second = new StubProvider(MetadataProvider.LastFm) { Result = true };
        var chain = new FallbackMetadataProvider(
            new StubProvider(MetadataProvider.Spotify) { Result = false },
            second);

        Assert.True(await chain.EnrichAsync(new Track()));
        Assert.Equal(1, second.Calls);
    }

    /// <summary>
    /// A provider that throws does not stop the next one being asked.
    /// </summary>
    /// <remarks>
    /// An expired Spotify sign-in must not make a perfectly good Last.fm key useless — that
    /// would turn one broken credential into a page that can do nothing at all.
    /// </remarks>
    [Fact]
    public async Task Enrich_FallsBackWhenThePreferredProviderThrows()
    {
        var second = new StubProvider(MetadataProvider.LastFm) { Result = true };
        var chain = new FallbackMetadataProvider(
            new StubProvider(MetadataProvider.Spotify) { Throws = new MetadataLookupException("signed out") },
            second);

        Assert.True(await chain.EnrichAsync(new Track()));
        Assert.Equal(1, second.Calls);
    }

    /// <summary>
    /// When nothing matched and something was broken, the breakage is reported.
    /// </summary>
    /// <remarks>
    /// "Not in any catalogue" and "every source is down" look identical on the page and want
    /// opposite responses from the user, so the failure is raised rather than folded into a
    /// quiet <c>false</c>.
    /// </remarks>
    [Fact]
    public async Task Enrich_ThrowsWhenNothingMatchedAndAProviderFailed()
    {
        var chain = new FallbackMetadataProvider(
            new StubProvider(MetadataProvider.Spotify) { Throws = new MetadataLookupException("rate limited") },
            new StubProvider(MetadataProvider.LastFm) { Result = false });

        var ex = await Assert.ThrowsAsync<MetadataLookupException>(() => chain.EnrichAsync(new Track()));

        Assert.Equal("rate limited", ex.Message);
    }

    /// <summary>The reported failure is the most-preferred provider's, not the last one's.</summary>
    [Fact]
    public async Task Enrich_ReportsTheFirstFailureNotTheLast()
    {
        var chain = new FallbackMetadataProvider(
            new StubProvider(MetadataProvider.Spotify) { Throws = new MetadataLookupException("first") },
            new StubProvider(MetadataProvider.LastFm) { Throws = new MetadataLookupException("second") });

        var ex = await Assert.ThrowsAsync<MetadataLookupException>(() => chain.EnrichAsync(new Track()));

        Assert.Equal("first", ex.Message);
    }

    /// <summary>Nothing matched and nothing was broken is an ordinary "no".</summary>
    [Fact]
    public async Task Enrich_ReturnsFalseWhenEverySourceSimplyHasNoMatch()
    {
        var chain = new FallbackMetadataProvider(
            new StubProvider(MetadataProvider.Spotify) { Result = false },
            new StubProvider(MetadataProvider.LastFm) { Result = false });

        Assert.False(await chain.EnrichAsync(new Track()));
    }

    /// <summary>Cancellation is the user stopping the run, not a provider failing.</summary>
    /// <remarks>
    /// Falling back here would ask the next provider with a token that is already cancelled, so
    /// the only effect would be to turn one stop into several.
    /// </remarks>
    [Fact]
    public async Task Enrich_PropagatesCancellationRatherThanFallingBack()
    {
        var second = new StubProvider(MetadataProvider.LastFm) { Result = true };
        var chain = new FallbackMetadataProvider(
            new StubProvider(MetadataProvider.Spotify) { Throws = new OperationCanceledException() },
            second);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chain.EnrichAsync(new Track()));
        Assert.Equal(0, second.Calls);
    }

    /// <summary>Nothing configured is reported as empty, which the page words differently.</summary>
    [Fact]
    public void Chain_IsEmptyWhenNothingIsConfigured()
    {
        Assert.True(new FallbackMetadataProvider().IsEmpty);
        Assert.True(new FallbackMetadataProvider(new NoMetadataProvider()).IsEmpty);
        Assert.False(new FallbackMetadataProvider(new StubProvider(MetadataProvider.LastFm)).IsEmpty);
    }

    private sealed class StubProvider(MetadataProvider kind) : IMetadataProvider
    {
        public MetadataProvider Kind => kind;

        public bool Result { get; init; }

        public Exception? Throws { get; init; }

        public int Calls { get; private set; }

        public Task<bool> EnrichAsync(Track track, CancellationToken cancellationToken = default)
        {
            Calls++;

            return Throws is not null ? Task.FromException<bool>(Throws) : Task.FromResult(Result);
        }
    }
}
