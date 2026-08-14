using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
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
    /// <para>
    /// It waited 100ms before the first poll and retried once a second later. Four attempts here
    /// because the whole thing is bounded by <see cref="TrackEnricher.DefaultDeadline"/> and runs
    /// concurrently with a recording that lasts minutes — roughly three seconds of chasing costs
    /// nothing and covers a backend that is slower than usual to advance.
    /// </para>
    /// <para>
    /// Thirty for an answer carrying no track, because that is not a backend one poll behind: it
    /// is a free account's advertisement break, which runs far longer than three seconds and was
    /// costing every track played after one its tags. It stays well inside the enricher's
    /// deadline, so a track the API will never report delays that recording and no other — and
    /// <see cref="SpotifyMetadataProvider"/> stands the long budget down entirely once it is clear
    /// the API is never going to answer for this session, so a misconfigured install pays it twice
    /// rather than on every track.
    /// </para>
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

    /// <summary>
    /// How many tracks in a row have run the no-track budget out without ever seeing one.
    /// </summary>
    /// <remarks>
    /// Reset by any successful match, so a session that starts badly and is then fixed — the user
    /// signs in as the account that is actually playing — goes straight back to waiting out
    /// advertisement breaks without restarting anything.
    /// </remarks>
    private int _emptyGiveUps;

    /// <summary>
    /// How many tracks in a row may exhaust the no-track budget before it is abandoned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The long budget exists for a condition that ends by itself: an advertisement break, or a
    /// backend mid-swap. A setup the API will never answer for — the signed-in account is not the
    /// one playing, or playback is in a private session — looks identical at the first track and
    /// nothing like it by the third, and paying thirty seconds per recording forever is a far
    /// worse outcome than the untagged file it buys nothing towards.
    /// </para>
    /// <para>
    /// Two rather than one, because a single unlucky track — recording started during a genuine
    /// advertisement break — should not cost the rest of the session its ad handling.
    /// </para>
    /// </remarks>
    private const int EmptyGiveUpsBeforeStandingDown = 2;

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
    /// user's answer rather than a stack trace. Spotify sends a reason in the error body, and that
    /// reason beats anything that could be written here from the status code alone — see
    /// <see cref="ReasonFor"/> for why it is read off the response rather than off
    /// <see cref="Exception.Message"/>.
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
        var reason = ReasonFor(ex) ?? "no reason given";

        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                // Renewal already had its chance: the SDK redeems the refresh token before the
                // call, so a 401 arriving here means the refresh token itself is dead.
                Log.Warning(
                    "Spotify rejected the stored sign-in: {Reason}. Sign in again on the Settings page.",
                    reason);

                AuthorizationExpired?.Invoke(this, EventArgs.Empty);
                return false;

            case HttpStatusCode.Forbidden:
                // Spotify returns 403 both for an account the app is not allowed to answer for and
                // for an app that has run past the user quota its dashboard mode allows. Only the
                // body tells them apart, so it leads the line and the hint below it is just the
                // likelier of the two spelled out — never a replacement for what Spotify said.
                Log.Warning(
                    "Spotify refused the request for {Track}: {Reason}. An app still in development "
                    + "mode only answers for accounts added to its user list in the Spotify "
                    + "developer dashboard.",
                    track,
                    reason);

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
                Log.Information("Spotify has no entry for {Track}: {Reason}.", track, reason);
                return false;

            default:
                Log.Warning(
                    "Spotify answered {Status} for {Track}: {Reason}. Recording it untagged.",
                    status is { } code ? (int)code : 0,
                    track,
                    reason);

                return false;
        }
    }

    /// <summary>How much of Spotify's reason reaches the activity log.</summary>
    /// <remarks>
    /// Long enough for every error message Spotify actually sends, short enough that a proxy's
    /// HTML error page cannot flood the log it lands in.
    /// </remarks>
    private const int MaximumReasonLength = 200;

    /// <summary>
    /// Spotify's own explanation for a failure, or null when it did not send one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read off the response body, not off <see cref="Exception.Message"/>.</b> The SDK tries to
    /// parse the body into the message and returns null when it does not recognise the shape, which
    /// leaves .NET to fill the message in with <c>"Exception of type
    /// 'SpotifyAPI.Web.APIException' was thrown."</c> — a string that occupies the slot the user's
    /// actual answer belongs in. Logging it turned a 403 that said which account was rejected into
    /// a line naming two possible causes and confirming neither.
    /// </para>
    /// <para>
    /// Two body shapes, because two services answer: the Web API sends
    /// <c>{"error":{"status":403,"message":"…"}}</c> and the accounts service sends
    /// <c>{"error":"invalid_grant","error_description":"…"}</c>. Anything else — an HTML page from
    /// something in the middle, most likely — is quoted raw and truncated, because unrecognised
    /// text still says more than a bare status code does.
    /// </para>
    /// </remarks>
    internal static string? ReasonFor(APIException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ReasonFromBody(ex.Response?.Body) ?? Usable(ex.Message);
    }

    private static string? ReasonFromBody(object? body)
    {
        if (body is not string json) return Shorten(body?.ToString());
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && Shorten(message.GetString()) is { } text)
                {
                    return text;
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    var described = document.RootElement.TryGetProperty("error_description", out var description)
                        ? Shorten(description.GetString())
                        : null;

                    return described ?? Shorten(error.GetString());
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON after all. The raw text below is still the best answer available.
        }

        return Shorten(json);
    }

    /// <summary>
    /// <paramref name="message"/> unless it is .NET's stand-in for a message that was never set.
    /// </summary>
    /// <remarks>
    /// Matched on the type name rather than the sentence around it: that default is localised, and
    /// the full type name is the one part of it that is the same in every language.
    /// </remarks>
    private static string? Usable(string? message) =>
        message is not null && message.Contains(typeof(APIException).FullName!, StringComparison.Ordinal)
            ? null
            : Shorten(message);

    private static string? Shorten(string? text)
    {
        var trimmed = text?.Trim();

        if (string.IsNullOrEmpty(trimmed)) return null;

        return trimmed.Length <= MaximumReasonLength
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, MaximumReasonLength), "…");
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
            {
                // Whatever was wrong with this session is not wrong any more.
                _emptyGiveUps = 0;

                return await ApplyAsync(track, reported, cancellationToken);
            }

            // A podcast episode is not a track and never will be, whatever we ask again.
            if (playback?.Item is not null && reported is null) return false;

            // An answer with no track at all is not a mismatch. Something *is* playing — a
            // recording is running, which is why this lookup exists — so Spotify is either
            // between tracks, serving a free account's advertisement break, or answering 204
            // while its player state catches up. All three resolve on their own, and all three
            // used to spend the mismatch budget: four attempts, three seconds, then an untagged
            // recording. They get a budget of their own, long enough to outlast a break.
            //
            // Only while it is still plausible that they will resolve. Once a run of tracks has
            // each waited the long budget out and seen nothing, the cause is not a break — it is
            // a setup the player endpoint will never answer for — and continuing to spend thirty
            // seconds per recording on it buys nothing.
            var chasing = reported is null && _emptyGiveUps < EmptyGiveUpsBeforeStandingDown;
            var budget = chasing ? _polling.MaximumEmptyAttempts : _polling.MaximumAttempts;

            if (++attempts >= budget)
            {
                Log.Debug(
                    "Spotify still reported {Reported} for {Track} after {Attempts} attempts.",
                    Describe(playback),
                    track,
                    attempts);

                if (reported is null) NoteEmptyGiveUp();

                return false;
            }

            Log.Debug(
                "Spotify reported {Reported} while {Track} was detected; asking again.",
                Describe(playback),
                track);

            await Task.Delay(_polling.RetryDelay, cancellationToken);
        }
    }


    /// <summary>
    /// Counts a track that waited the no-track budget out, and says something once the run of them
    /// is long enough to mean the setup is wrong rather than the timing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A warning, and only one.</b> Everything the lookup says on its way to giving up is at
    /// Debug, which the Record page's activity log does not show — so the symptom the user sees is
    /// every recording arriving untagged with nothing anywhere saying why, and the two causes are
    /// both things only they can fix. That is worth one line above the fold. Repeating it per
    /// track afterwards would bury the log it is trying to be read in, so it fires on the
    /// transition and then goes quiet until a successful match resets the run.
    /// </para>
    /// <para>
    /// Both causes are named because the log cannot tell them apart: the player endpoint reports
    /// on the account that authorised Offstream, so music playing on a different account is
    /// invisible to it, and a private session is invisible to it by design. Both answer 204,
    /// which carries no body to distinguish them with.
    /// </para>
    /// </remarks>
    private void NoteEmptyGiveUp()
    {
        if (++_emptyGiveUps != EmptyGiveUpsBeforeStandingDown) return;

        Log.Warning(
            "Spotify has reported nothing playing for {Count} tracks in a row, so they are being "
            + "recorded untagged. The usual cause is that the account signed in to Offstream on the "
            + "Settings page is not the account the music is playing on; a private session hides "
            + "playback the same way. Offstream will stop waiting on each track until a lookup "
            + "succeeds again.",
            _emptyGiveUps);
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
