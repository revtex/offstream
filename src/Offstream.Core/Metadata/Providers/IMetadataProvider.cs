namespace Offstream.Core.Metadata.Providers;

/// <summary>
/// Fills in what the Spotify window title cannot say: album, track number, disc, year, genre,
/// album artists and the cover-art URL.
/// </summary>
/// <remarks>
/// <para>
/// The window title carries an artist and a title and nothing else, so without a provider every
/// recording is tagged with two fields and no art. This is the seam the recording pipeline calls
/// once per track, chosen from <see cref="MetadataProvider"/>.
/// </para>
/// <para>
/// This is the reference implementation's <c>IExternalAPI</c> with its authentication members
/// dropped. Those existed because the interface was also the app's sign-in surface: a static
/// <c>ExternalAPI.Instance</c> that the form prodded into authenticating on demand. Offstream
/// signs in on the Settings page and hands an already-authenticated client to whichever provider
/// needs one, so an enrichment call is only ever an enrichment call.
/// </para>
/// </remarks>
public interface IMetadataProvider
{
    /// <summary>Which provider this is, for logging and for asserting the right one was chosen.</summary>
    MetadataProvider Kind { get; }

    /// <summary>
    /// Enriches <paramref name="track"/> in place.
    /// </summary>
    /// <returns>
    /// Whether anything was written. False is an ordinary outcome — the track is not in the
    /// provider's catalogue, or what it returned does not match what is playing — and is not an
    /// error.
    /// </returns>
    Task<bool> EnrichAsync(Track track, CancellationToken cancellationToken = default);
}

/// <summary>The provider for <see cref="MetadataProvider.None"/>: writes nothing, fails never.</summary>
/// <remarks>
/// A real object rather than a null check at every call site, so "no provider" and "a provider
/// that found nothing" take the same path through the pipeline and are tested the same way.
/// </remarks>
public sealed class NoMetadataProvider : IMetadataProvider
{
    /// <inheritdoc />
    public MetadataProvider Kind => MetadataProvider.None;

    /// <inheritdoc />
    public Task<bool> EnrichAsync(Track track, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
