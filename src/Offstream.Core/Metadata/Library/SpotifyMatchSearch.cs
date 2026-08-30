using Offstream.Core.Metadata.Providers;
using Serilog;
using SpotifyAPI.Web;

namespace Offstream.Core.Metadata.Library;

/// <summary>Searching Spotify's catalogue with the user's own words.</summary>
/// <remarks>
/// <para>
/// The answer to a wrong automatic match. <see cref="SpotifySearchMetadataProvider"/> builds its
/// query from the file's own fields and then rejects any result whose artist disagrees with them,
/// which is right when the file is roughly correct and useless when it is not: a file whose
/// artist is wrong cannot be corrected by a search that requires the wrong artist to match. Asking
/// again returns the same answer, however many times it is asked.
/// </para>
/// <para>
/// So the query is free text, taken verbatim, and every result comes back for the user to choose
/// from. Nothing is filtered on the way out — the person reading the list can tell a live version
/// from a studio one, and a filter that could hide the right answer is worse here than a list with
/// a few wrong ones in it.
/// </para>
/// </remarks>
public sealed class SpotifyMatchSearch(ISpotifyClient client) : ILibraryMatchSearch
{
    /// <summary>
    /// How many results to show.
    /// </summary>
    /// <remarks>
    /// Enough to cover the remaster, the live take and the compilation appearance of one song,
    /// short enough to read without scrolling the row off the screen. A longer list is not more
    /// useful: past this the results stop being versions of what was asked for.
    /// </remarks>
    private const int SearchLimit = 5;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryMatchCandidate>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        try
        {
            var request = new SearchRequest(SearchRequest.Types.Track, query.Trim())
            {
                Limit = SearchLimit,
            };

            var response = await client.Search.Item(request, cancellationToken);
            var results = response?.Tracks?.Items;

            if (results is null or { Count: 0 }) return [];

            return [.. results.Where(result => result is not null).Select(Describe)];
        }
        catch (APIException ex)
        {
            Log.Warning(ex, "Searching Spotify for {Query} failed.", query);

            throw new MetadataLookupException(SpotifySearchMetadataProvider.Describe(ex), ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The chosen track is fetched again rather than tagged from the search result, because a
    /// search result is not a full track: the release date lives on the album and the genre on the
    /// artist. Going through the same enrichment the automatic path uses is also what keeps a
    /// hand-picked match from behaving differently to a found one.
    /// </remarks>
    public async Task ApplyAsync(
        Track track,
        LibraryMatchCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(candidate);

        try
        {
            var match = await client.Tracks.Get(candidate.Id, cancellationToken);

            if (match is null) return;

            // The file's own artwork is dropped, and only on this path. Everywhere else the rule
            // is that an embedded picture wins over a provider's URL, which is right when a
            // lookup is confirming what the file already says. Here the user has just declared
            // the file to be a different song, so its cover belongs to the wrong track — keeping
            // it would write correct tags and the previous artist's sleeve into the same file.
            track.AlbumArtImage = null;

            await SpotifyCatalogEnricher.ApplyAsync(client, track, match, cancellationToken);
        }
        catch (APIException ex)
        {
            Log.Warning(ex, "Applying the chosen Spotify match {Id} failed.", candidate.Id);

            throw new MetadataLookupException(SpotifySearchMetadataProvider.Describe(ex), ex);
        }
    }

    /// <summary>Flattens one result into the four things that tell two versions apart.</summary>
    private static LibraryMatchCandidate Describe(FullTrack result) => new(
        result.Id ?? string.Empty,
        result.Name ?? string.Empty,
        string.Join(", ", result.Artists?.Select(artist => artist.Name) ?? []),
        result.Album?.Name ?? string.Empty,
        ReleaseYear(result.Album?.ReleaseDate),
        SmallestImage(result.Album?.Images));

    private static int? ReleaseYear(string? releaseDate) =>
        releaseDate is { Length: >= 4 } && int.TryParse(releaseDate[..4], out var year) ? year : null;

    /// <summary>
    /// The smallest image Spotify offers, because this is a thumbnail in a list.
    /// </summary>
    /// <remarks>
    /// Eight results at full album-art resolution is several megabytes downloaded to draw eight
    /// squares of forty pixels. Spotify orders its images widest first, so the last is the one to
    /// take.
    /// </remarks>
    private static string? SmallestImage(List<Image>? images) =>
        images is { Count: > 0 } ? images[^1].Url : null;
}
