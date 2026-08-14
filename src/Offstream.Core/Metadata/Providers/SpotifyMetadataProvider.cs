using System.Collections.Concurrent;
using System.Net;
using Offstream.Core.Spotify;
using Serilog;
using SpotifyAPI.Web;

namespace Offstream.Core.Metadata.Providers;

/// <summary>Fetches the currently-playing track and its album from Spotify, and maps them onto a <see cref="Track"/>.</summary>
/// <remarks>
/// <para>
/// Narrower than <see cref="IMetadataProvider"/> on purpose: it names the Spotify provider
/// specifically, for anything that needs that one rather than whichever one is configured.
/// </para>
/// <para>
/// <see cref="IMetadataProvider.EnrichAsync"/> returns false here for every case that is not an
/// error — nothing is playing, what is playing is a podcast episode rather than a track, or what
/// Spotify reports no longer matches the track that was detected from the window title.
/// </para>
/// </remarks>
public interface ISpotifyMetadataProvider : IMetadataProvider
{
    /// <summary>
    /// Raised when Spotify rejects the stored credentials outright, meaning the refresh token has
    /// expired or been revoked and only a fresh browser sign-in will fix it.
    /// </summary>
    /// <remarks>
    /// Distinct from an ordinary failure because the remedy is: nothing this process does will
    /// ever make the stored token work again, so the host clears it and puts the user back in
    /// front of the sign-in button rather than retrying it silently on every track for the rest of
    /// the install's life.
    /// </remarks>
    event EventHandler? AuthorizationExpired;
}

/// <summary>How hard to chase Spotify's player state at a track boundary.</summary>
/// <param name="SettleDelay">How long the backend gets before the first poll.</param>
/// <param name="RetryDelay">How long to wait before asking again after a mismatch.</param>
/// <param name="MaximumAttempts">Polls in total, counting the first, when a track is reported.</param>
/// <param name="MaximumEmptyAttempts">
/// Polls allowed while Spotify reports no track at all, which is a different failure with a
/// different cure — see the loop in <see cref="SpotifyMetadataProvider"/>.
/// </param>
/// <remarks>
/// Injectable so tests can exercise the retry without waiting out the real delays; the recording
/// pipeline always takes <see cref="Default"/>.
/// </remarks>
public sealed record SpotifyPollingOptions(
    TimeSpan SettleDelay,
    TimeSpan RetryDelay,
    int MaximumAttempts,
    int MaximumEmptyAttempts)
{
    /// <summary>
    /// The reference's timings, with a longer tail.
    /// </summary>
    /// <remarks>
    /// It waited 100ms before the first poll and retried once a second later. Four attempts here
    /// because the whole thing is bounded by <see cref="TrackEnricher.DefaultDeadline"/> and runs
    /// concurrently with a recording that lasts minutes — roughly three seconds of chasing costs
    /// nothing and covers a backend that is slower than usual to advance.
    /// </para>
    /// <para>
    /// Thirty for an answer carrying no track, because that is not a backend one poll behind: it
    /// is a free account's advertisement break, which runs far longer than three seconds and was
    /// costing every track played after one its tags. It stays well inside the enricher's
    /// deadline, so a track the API will never report delays that recording and no other.
    /// </remarks>
    public static SpotifyPollingOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromSeconds(1),
        MaximumAttempts: 4,
        MaximumEmptyAttempts: 30);
}

/// <summary>
/// The read half of the reference implementation's <c>SpotifyAPI.UpdateTrack</c>: given an
/// authenticated client, fetch what Spotify says is playing and map it onto a track.
/// </summary>
/// <remarks>
/// <para>
/// Narrower than the original, which also fell back to Last.fm and reopened the auth dialog after
/// repeated failures — orchestration that belongs with the caller, not with the fetch-and-map step.
/// </para>
/// <para>
/// <b>The title-match guard is kept, and so is the retry that makes it usable.</b> Detection and
/// this enrichment race independently, and they race against Spotify's own backend: the window
/// title changes the instant the desktop client advances, while
/// <c>/v1/me/player/currently-playing</c> is served from player state that trails it by a second
/// or more. Asking once at the track boundary therefore gets the *previous* track back, the guard
/// correctly refuses to tag the new recording with it, and the track is saved bare. The reference
/// handled this with a settle delay before the first poll and one retry a second later; both are
/// reproduced here, with the retry allowed a few attempts because
/// <see cref="TrackEnricher.DefaultDeadline"/> gives it far more room than the reference had.
/// </para>
/// <para>
/// The same race produces a 204 with no body when the boundary is caught mid-swap, so that answer
/// is retried too rather than taken as "nothing is playing".
/// </para>
/// </remarks>
public sealed class SpotifyMetadataProvider(ISpotifyClient client, SpotifyPollingOptions? polling = null)
    : ISpotifyMetadataProvider
{
    /// <summary>
    /// How many artist genres reach the tag.
    /// </summary>
    /// <remarks>
    /// Spotify lists as many as a dozen for a well-known artist, shading from the useful
    /// ("trance") into the hyper-specific ("german progressive trance"). Three matches what
    /// <see cref="LastFmTrackMapper"/> takes, so a library tagged from both providers does not
    /// have two different ideas of how long a genre tag is.
    /// </remarks>
    private const int MaximumGenres = 3;

    private readonly SpotifyPollingOptions _polling = polling ?? SpotifyPollingOptions.Default;

    /// <summary>
    /// Artist id to genres, for the life of this provider — which is the life of one session.
    /// </summary>
    /// <remarks>
    /// An artist's genres do not change while a recording session runs, and recording an album
    /// means asking about the same artist once per track. Without this, a fifteen-track album is
    /// fifteen identical requests against a rate limit that is shared with every other call the
    /// session makes. Keyed by id rather than name because ids are what Spotify guarantees
    /// unique — two artists genuinely share a name often enough to matter.
    /// </remarks>
    private readonly ConcurrentDictionary<string, string[]> _artistGenres = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public event EventHandler? AuthorizationExpired;

    /// <inheritdoc />
    public MetadataProvider Kind => MetadataProvider.Spotify;

    /// <inheritdoc />
    public async Task<bool> EnrichAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        try
        {
            return await LookUpAsync(track, cancellationToken);
        }
        catch (APIException ex)
        {
            return Explain(ex, track);
        }
    }

    /// <summary>
    /// Turns an API fault into a log line the user can act on, and false.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The activity log on the Record page is where these land, so the message has to be the
    /// user's answer rather than a stack trace: Spotify sends a reason in the error body and the
    /// SDK surfaces it as <see cref="Exception.Message"/>, which beats anything that could be
    /// written here from the status code alone.
    /// </para>
    /// <para>
    /// Every one of them is downgraded to "no metadata" — a tagging fault must never reach the
    /// recording. The status code decides what the user is told to do about it, not whether the
    /// track survives.
    /// </para>
    /// </remarks>
    private bool Explain(APIException ex, Track track)
    {
        var status = ex.Response?.StatusCode;

        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                // Renewal already had its chance: the SDK redeems the refresh token before the
                // call, so a 401 arriving here means the refresh token itself is dead.
                Log.Warning(
                    "Spotify rejected the stored sign-in ({Message}). Sign in again on the Settings page.",
                    ex.Message);

                AuthorizationExpired?.Invoke(this, EventArgs.Empty);
                return false;

            case HttpStatusCode.Forbidden:
                // Spotify returns 403 both for a missing permission and for an app that has run
                // past the user quota its dashboard mode allows, and the body is the only thing
                // that tells them apart — which is why the message is quoted rather than replaced.
                Log.Warning(
                    "Spotify refused the request for {Track} ({Message}). This usually means the "
                    + "signed-in account is not on the dashboard app's allowlist, or the app has "
                    + "reached its user quota.",
                    track,
                    ex.Message);

                return false;

            case HttpStatusCode.TooManyRequests:
                // Only reachable once SpotifyRetryHandler has waited out every Retry-After it was
                // given, so the throttle has outlasted the enrichment deadline. The handler has
                // already said how long it waited; this adds the consequence.
                Log.Warning(
                    "Spotify's rate limit is still in force, so {Track} is recorded untagged. "
                    + "Recording itself is unaffected.",
                    track);

                return false;

            case HttpStatusCode.NotFound:
                Log.Information("Spotify has no entry for {Track} ({Message}).", track, ex.Message);
                return false;

            default:
                Log.Warning(
                    "Spotify answered {Status} for {Track} ({Message}); recording it untagged.",
                    status is { } code ? (int)code : 0,
                    track,
                    ex.Message);

                return false;
        }
    }

    private async Task<bool> LookUpAsync(Track track, CancellationToken cancellationToken)
    {
        await Task.Delay(_polling.SettleDelay, cancellationToken);

        var attempts = 0;

        while (true)
        {
            var playback = await client.Player.GetCurrentlyPlaying(
                new PlayerCurrentlyPlayingRequest(), cancellationToken);

            // A 204 with no body — nothing is playing — deserializes to null rather than throwing.
            var reported = playback?.Item as FullTrack;

            if (reported is not null && MatchesDetectedTrack(track, reported))
                return await ApplyAsync(track, reported, cancellationToken);

            // A podcast episode is not a track and never will be, whatever we ask again.
            if (playback?.Item is not null && reported is null) return false;

            // An answer with no track at all is not a mismatch. Something *is* playing — a
            // recording is running, which is why this lookup exists — so Spotify is either
            // between tracks, serving a free account's advertisement break, or answering 204
            // while its player state catches up. All three resolve on their own, and all three
            // used to spend the mismatch budget: four attempts, three seconds, then an untagged
            // recording. They get a budget of their own, long enough to outlast a break.
            //
            // Not unbounded. If the API never reports this track — a restricted account, a
            // device the player endpoint cannot see — waiting to the enricher's deadline would
            // add that delay to every recording rather than to one.
            var budget = reported is null ? _polling.MaximumEmptyAttempts : _polling.MaximumAttempts;

            if (++attempts >= budget)
            {
                Log.Debug(
                    "Spotify still reported {Reported} for {Track} after {Attempts} attempts.",
                    Describe(playback),
                    track,
                    attempts);

                return false;
            }

            Log.Debug(
                "Spotify reported {Reported} while {Track} was detected; asking again.",
                Describe(playback),
                track);

            await Task.Delay(_polling.RetryDelay, cancellationToken);
        }
    }


    private async Task<bool> ApplyAsync(Track track, FullTrack spotifyTrack, CancellationToken cancellationToken)
    {
        SpotifyTrackMapper.Apply(track, spotifyTrack);

        var albumId = spotifyTrack.Album?.Id;

        if (!string.IsNullOrEmpty(albumId))
        {
            var album = await client.Albums.Get(albumId, cancellationToken);
            SpotifyTrackMapper.Apply(track, album);
        }

        // Only asked for when the album came back without genres, which since Spotify's late-2024
        // catalogue changes is very nearly always. See ArtistGenresAsync.
        if (track.Genres is null or { Length: 0 })
        {
            track.Genres = await ArtistGenresAsync(track, spotifyTrack, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// The lead artist's genres, from cache when this session has already asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Genre is an artist attribute in Spotify's model, not a track one.</b> There is no genre
    /// field on a track at any endpoint; album objects have one that Spotify stopped populating
    /// for most of the catalogue in late 2024, which left every Spotify-tagged recording with an
    /// empty genre tag. <c>/v1/artists/{id}</c> is the one place the data still lives.
    /// </para>
    /// <para>
    /// It needs no user scope — artist data is public — so this costs a request and nothing on
    /// the consent screen. The id is already in hand from the track that was just matched.
    /// </para>
    /// <para>
    /// <b>The honest limitation:</b> these describe the artist's body of work rather than this
    /// recording, so a ballad by a metal band is tagged as metal. That is Spotify's model and not
    /// something this can correct; a track-level answer has to come from somewhere else, which is
    /// what the enricher's genre fallback is for.
    /// </para>
    /// </remarks>
    private async Task<string[]> ArtistGenresAsync(
        Track track,
        FullTrack spotifyTrack,
        CancellationToken cancellationToken)
    {
        var artistId = spotifyTrack.Artists?
            .FirstOrDefault(artist => !string.IsNullOrEmpty(artist.Id))?.Id;

        if (string.IsNullOrEmpty(artistId))
        {
            Log.Debug("Spotify reported no artist id for {Track}, so it has no genres to give.", track);
            return [];
        }

        if (_artistGenres.TryGetValue(artistId, out var cached))
        {
            Log.Debug(
                "Reusing this session's genres for artist {ArtistId}: {Genres}.",
                artistId,
                cached.Length == 0 ? "none" : string.Join(", ", cached));

            return cached;
        }

        Log.Debug("Asking Spotify for artist {ArtistId}'s genres.", artistId);

        var artist = await client.Artists.Get(artistId, cancellationToken);

        var genres = (artist?.Genres ?? [])
            .Where(genre => !string.IsNullOrWhiteSpace(genre))
            .Select(genre => genre.Trim())
            .Take(MaximumGenres)
            .ToArray();

        // Cached even when empty: an artist Spotify has no genres for has none on the next track
        // either, and re-asking every track is the exact cost this exists to avoid.
        _artistGenres[artistId] = genres;

        Log.Debug(
            "Spotify gave artist {ArtistId} the genres: {Genres}.",
            artistId,
            genres.Length == 0 ? "none" : string.Join(", ", genres));

        return genres;
    }

    /// <summary>
    /// Whether the track Spotify says is playing is the one that was detected.
    /// </summary>
    /// <remarks>
    /// Spotify's name and artists both go into the comparison, because the detected string may
    /// carry the artist and Spotify's name never does. See <see cref="DetectedTrackMatch"/> for
    /// why comparing the two directly did not work.
    /// </remarks>
    private static bool MatchesDetectedTrack(Track track, FullTrack spotifyTrack) =>
        DetectedTrackMatch.Matches(
            track,
            spotifyTrack.Name,
            spotifyTrack.Artists?.Select(artist => artist.Name));

    /// <summary>What Spotify answered, for a log line that can actually be diagnosed.</summary>
    /// <summary>What Spotify answered, in the terms that distinguish the reasons for a miss.</summary>
    /// <remarks>
    /// This used to print <c>"nothing playing"</c> for every answer without a track, which
    /// collapsed a 204 and an advertisement into one indistinguishable line — and those are the
    /// two cases whose handling differs, so the log said nothing about the only question being
    /// asked of it. Naming the shape is what makes the difference readable at Debug.
    /// </remarks>
    private static string Describe(CurrentlyPlaying? playback) =>
        playback switch
        {
            null => "nothing at all (204 No Content)",
            { Item: FullTrack track } => track.Name ?? "an unnamed track",
            { CurrentlyPlayingType: { Length: > 0 } type } => $"a {type} and no track",
            _ => "no track and no type",
        };
}
