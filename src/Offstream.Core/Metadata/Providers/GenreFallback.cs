namespace Offstream.Core.Metadata.Providers;

/// <summary>
/// A second opinion on genre, for when the chosen provider tagged a track but had no genre for it.
/// </summary>
/// <remarks>
/// Genre is the one field the two providers disagree about structurally rather than
/// occasionally. Spotify models it as an attribute of an *artist*, so the best it can offer any
/// track is what its performer is generally known for. Last.fm models it as tags applied to the
/// track itself, which is the question actually being asked. Neither is a superset of the other,
/// so this exists to let the answer come from one provider while the rest of the tags come from
/// the other — rather than making the user choose between correct albums and correct genres.
/// </remarks>
public interface IGenreFallback
{
    /// <summary>Genres for <paramref name="track"/>, or empty when this source has none either.</summary>
    /// <remarks>Never throws: a genre is not worth failing an enrichment over.</remarks>
    Task<string[]> GetGenresAsync(Track track, CancellationToken cancellationToken = default);
}

/// <summary>
/// Asks a second <see cref="IMetadataProvider"/> for genres, and takes nothing else from it.
/// </summary>
/// <remarks>
/// <para>
/// Wired with Last.fm behind it, which is where a track-level genre can actually be had. It runs
/// the provider over a throwaway copy of the track rather than the real one, which is what keeps
/// this to its stated job: Last.fm's mapper would otherwise overwrite the album, year, cover art
/// and track number that the primary provider just established, mixing two catalogues' idea of
/// the same release into one file.
/// </para>
/// <para>
/// The copy carries artist and title only, because those are the whole of what a lookup needs and
/// anything else would just be discarded with the copy.
/// </para>
/// </remarks>
public sealed class ProviderGenreFallback(IMetadataProvider provider) : IGenreFallback
{
    private readonly IMetadataProvider _provider =
        provider ?? throw new ArgumentNullException(nameof(provider));

    /// <inheritdoc />
    public async Task<string[]> GetGenresAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (_provider.Kind == MetadataProvider.None) return [];

        var probe = new Track
        {
            Artist = track.Artist,
            Title = track.Title,
            Playing = track.Playing,
        };

        await _provider.EnrichAsync(probe, cancellationToken);

        return probe.Genres ?? [];
    }
}
