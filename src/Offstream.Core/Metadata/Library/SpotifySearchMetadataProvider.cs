using System.Net;
using Offstream.Core.Metadata.Providers;
using Serilog;
using SpotifyAPI.Web;

namespace Offstream.Core.Metadata.Library;

/// <summary>A lookup failed for a reason worth telling the user about.</summary>
/// <remarks>
/// Distinct from "found nothing", which is an ordinary answer and is reported as <c>false</c>.
/// This is for the cases where the user can do something — sign in again, wait out a throttle,
/// add their account to the dashboard app — and the message carries the provider's own words.
/// </remarks>
public sealed class MetadataLookupException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Looks a track up in Spotify's catalogue **by searching for it**, for files that are not
/// playing and never will be.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists beside <see cref="SpotifyMetadataProvider"/>.</b> That one asks
/// <c>/me/player/currently-playing</c> — it answers "what is this account listening to right
/// now", which is exactly right while a recording is in progress and useless for a file sitting
/// on disk. Searching is a different question with a different failure mode: the recording path
/// knows what the track is and is confirming it, whereas this path is guessing from a filename
/// and can confidently return the wrong song. That is why nothing here writes to a file — every
/// answer is a suggestion the user sees before it is committed.
/// </para>
/// <para>
/// <b>Cost.</b> <c>/search</c> takes an access token and no user scope, so it adds nothing to the
/// consent screen and <see cref="Spotify.Auth.SpotifyAuthOptions.DefaultScopes"/> is unchanged.
/// It does spend the user's quota, and a folder of two hundred files is two hundred searches —
/// which is why the page skips files that are already tagged and runs the rest one at a time.
/// </para>
/// </remarks>
public sealed class SpotifySearchMetadataProvider(ISpotifyClient client) : IMetadataProvider
{
    /// <summary>
    /// How many candidates to ask for.
    /// </summary>
    /// <remarks>
    /// Enough to get past a karaoke version or a compilation reissue sitting at the top, few
    /// enough that the response stays small. Nothing beyond the first few is ever looked at,
    /// because a match that far down is not one to trust from a filename.
    /// </remarks>
    private const int SearchLimit = 5;

    /// <summary>
    /// How many artist genres reach the tag — the same three the recording path takes, so a
    /// library tagged by both does not end up with two ideas of how long a genre tag is.
    /// </summary>
    private const int MaximumGenres = 3;

    /// <inheritdoc />
    public MetadataProvider Kind => MetadataProvider.Spotify;

    /// <inheritdoc />
    public async Task<bool> EnrichAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        // Searching for a title with no artist matches half the catalogue, and searching for
        // neither matches all of it. Either way the answer would be a confident wrong one.
        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title))
        {
            return false;
        }

        try
        {
            var request = new SearchRequest(SearchRequest.Types.Track, BuildQuery(track))
            {
                Limit = SearchLimit,
            };

            var response = await client.Search.Item(request, cancellationToken);
            var match = ChooseMatch(response?.Tracks?.Items, track);

            if (match is null)
            {
                Log.Information("Spotify has no match for {Artist} - {Title}.", track.Artist, track.Title);

                return false;
            }

            await ApplyAsync(track, match, cancellationToken);

            return true;
        }
        catch (APIException ex)
        {
            throw new MetadataLookupException(Describe(ex), ex);
        }
    }

    /// <summary>Builds a field-filtered query rather than pasting the filename in whole.</summary>
    /// <remarks>
    /// <c>track:</c> and <c>artist:</c> are Spotify's own filters, and they matter here because
    /// the free-text form scores a match on any field — an artist whose name appears in someone
    /// else's album title outranks the actual song surprisingly often. Double quotes are stripped
    /// rather than escaped: they are the filter syntax's own delimiter, so a stray one from a
    /// filename would end the term early and change which field the rest of it lands in.
    /// </remarks>
    private static string BuildQuery(Track track) =>
        $"track:\"{Clean(track.Title)}\" artist:\"{Clean(track.Artist)}\"";

    private static string Clean(string? value) =>
        (value ?? string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>Picks the result that actually is the track, or nothing.</summary>
    /// <remarks>
    /// An exact artist-and-title match is taken; otherwise the first result is used only when its
    /// artist agrees, because Spotify always returns *something* and the top hit for a misparsed
    /// filename is routinely a different song entirely. Returning nothing is a much better outcome
    /// than tagging a file with a confident wrong answer — the user came here to fix metadata, and
    /// silently corrupting good filenames into wrong tags is the one unrecoverable failure.
    /// </remarks>
    private static FullTrack? ChooseMatch(IEnumerable<FullTrack>? candidates, Track track)
    {
        var results = candidates?.Where(candidate => candidate is not null).ToList();

        if (results is null or { Count: 0 }) return null;

        var exact = results.FirstOrDefault(candidate =>
            Same(candidate.Name, track.Title) && HasArtist(candidate, track.Artist));

        return exact ?? results.FirstOrDefault(candidate => HasArtist(candidate, track.Artist));
    }

    private static bool HasArtist(FullTrack candidate, string? artist) =>
        candidate.Artists?.Any(performer => Same(performer?.Name, artist)) == true;

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies the match onto the track, then fills in what a track object cannot say.</summary>
    /// <remarks>
    /// <para>
    /// The same three-step shape the recording path uses: the track carries title, artist and
    /// position; the album carries year and cover art; and genre lives on the artist, because
    /// Spotify has no genre field on a track and stopped populating the album's for most of the
    /// catalogue in late 2024.
    /// </para>
    /// <para>
    /// <b>What the file already had is put back when Spotify offers nothing.</b> The album
    /// mapping assigns genre and year unconditionally — right for a recording, where the track
    /// starts empty and Spotify is the only source there is, and wrong here, where the track
    /// starts as the file's own tags. Spotify returns an empty genre list for most of its
    /// catalogue, so without this a lookup silently blanks a genre the user curated. Nothing was
    /// ever written — the writer skips empty values — but the row reported a change it would not
    /// make, and once the page started showing before-and-after it read as an offer to erase.
    /// </para>
    /// </remarks>
    private async Task ApplyAsync(Track track, FullTrack match, CancellationToken cancellationToken)
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

    /// <summary>Turns Spotify's refusal into a sentence, keeping Spotify's own words in it.</summary>
    /// <remarks>
    /// The status decides what to add, never what to replace. A 403 in particular means either
    /// "this account is not on the dashboard app's list" or "the app is past its quota", and only
    /// the body tells them apart — so the body leads and the hint follows.
    /// </remarks>
    private static string Describe(APIException ex)
    {
        var reason = SpotifyMetadataProvider.ReasonFor(ex) ?? "no reason given";

        return ex.Response?.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $"Spotify rejected the stored sign-in ({reason}). Sign in again on the Settings page.",
            HttpStatusCode.Forbidden =>
                $"Spotify refused the request: {reason}. An app in development mode only answers "
                + "for accounts added to its user list in the Spotify developer dashboard, and a "
                + "quota that has been passed reports the same status.",
            HttpStatusCode.TooManyRequests =>
                "Spotify's rate limit is still in force after waiting as long as it asked. "
                + "Try the remaining files again in a few minutes.",
            _ => $"Spotify could not be reached: {reason}.",
        };
    }
}
