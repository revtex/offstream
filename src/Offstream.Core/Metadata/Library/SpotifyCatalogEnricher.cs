using Offstream.Core.Metadata.Providers;
using SpotifyAPI.Web;

namespace Offstream.Core.Metadata.Library;

/// <summary>Turns one Spotify track into a full set of tags.</summary>
/// <remarks>
/// <para>
/// Shared by the automatic lookup and the manual one, which is the point of it existing. The two
/// differ entirely in how they arrive at a track — one guesses from the file and refuses a result
/// whose artist disagrees, the other takes whatever the user picked — and not at all in what to
/// do once they have one. Keeping the second half in one place matters more than the saving,
/// because the rule about putting back what Spotify does not offer is the kind that goes wrong
/// quietly and would drift the moment there were two copies.
/// </para>
/// </remarks>
internal static class SpotifyCatalogEnricher
{
    /// <summary>
    /// How many artist genres reach the tag — the same three the recording path takes, so a
    /// library tagged by both does not end up with two ideas of how long a genre tag is.
    /// </summary>
    private const int MaximumGenres = 3;

    /// <summary>Copies the match onto the track, then fills in what a track object cannot say.</summary>
    /// <remarks>
    /// <para>
    /// The same three-step shape the recording path uses: the track carries title, artist and
    /// position; the album carries year and cover art; and genre lives on the artist, because
    /// Spotify has no genre field on a track and stopped populating the album's for most of the
    /// catalogue in late 2024.
    /// </para>
    /// <para>
    /// <b>What the track already had is put back when Spotify offers nothing.</b> The album
    /// mapping assigns genre and year unconditionally — right for a recording, where the track
    /// starts empty and Spotify is the only source there is, and wrong on the Metadata page,
    /// where it starts as the file's own tags. Spotify returns an empty genre list for most of
    /// its catalogue, so without this a lookup silently blanks a genre the user curated. Nothing
    /// was ever written — the writer skips empty values — but the row reported a change it would
    /// not make, and once the page started showing before-and-after it read as an offer to erase.
    /// </para>
    /// </remarks>
    public static async Task ApplyAsync(
        ISpotifyClient client,
        Track track,
        FullTrack match,
        CancellationToken cancellationToken)
    {
        var seededGenres = track.Genres;
        var seededYear = track.Year;

        SpotifyTrackMapper.Apply(track, match);

        var albumId = match.Album?.Id;

        if (!string.IsNullOrEmpty(albumId))
        {
            SpotifyTrackMapper.Apply(track, await client.Albums.Get(albumId, cancellationToken));
        }

        track.Year ??= seededYear;

        if (track.Genres is { Length: > 0 }) return;

        track.Genres = seededGenres;

        if (track.Genres is { Length: > 0 }) return;

        var artistId = match.Artists?.FirstOrDefault(artist => !string.IsNullOrEmpty(artist.Id))?.Id;

        if (string.IsNullOrEmpty(artistId)) return;

        var artistDetail = await client.Artists.Get(artistId, cancellationToken);

        if (artistDetail?.Genres is { Count: > 0 } genres)
        {
            track.Genres = [.. genres.Take(MaximumGenres)];
        }
    }
}
