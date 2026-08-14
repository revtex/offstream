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
    /// <summary>
    /// Which provider is behind this.
    /// </summary>
    /// <remarks>
    /// Only so the activity log can name the source on the line it already prints. A genre that
    /// came from somewhere other than the provider doing the tagging is the interesting case —
    /// it means the primary had nothing for that artist — and a bare list of genres cannot say so.
    /// </remarks>
    MetadataProvider Kind { get; }

    /// <summary>Genres for <paramref name="track"/>, or empty when this source has none either.</summary>
    /// <remarks>Never throws: a genre is not worth failing an enrichment over.</remarks>
    Task<string[]> GetGenresAsync(Track track, CancellationToken cancellationToken = default);
}

/// <summary>
/// Asks Last.fm for genres, and takes nothing else from it.
/// </summary>
/// <remarks>
/// <para>
/// It asks about the track first and the artist second, because Last.fm frequently has no tags
/// at all for a given recording while carrying a rich set for whoever made it — see
/// <see cref="LastFmMetadataProvider.GetGenresAsync"/>.
/// </para>
/// <para>
/// <b>This replaced a wrapper that ran the whole provider over a throwaway copy of the track.</b>
/// That worked, but it inherited a success condition written for a different question: the full
/// lookup only maps anything, genres included, when Last.fm also returns an album. A track whose
/// tags were sitting right there got none because its album was missing — and it fetched a
/// release, its artwork and its track listing to read three strings off the side.
/// </para>
/// </remarks>
public sealed class LastFmGenreFallback(LastFmMetadataProvider provider) : IGenreFallback
{
    private readonly LastFmMetadataProvider _provider =
        provider ?? throw new ArgumentNullException(nameof(provider));

    /// <inheritdoc />
    public MetadataProvider Kind => MetadataProvider.LastFm;

    /// <inheritdoc />
    public Task<string[]> GetGenresAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        return _provider.GetGenresAsync(track.Artist, track.Title, cancellationToken);
    }
}
