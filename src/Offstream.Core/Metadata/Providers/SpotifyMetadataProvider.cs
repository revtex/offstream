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
/// <param name="MaximumAttempts">Polls in total, counting the first.</param>
/// <remarks>
/// Injectable so tests can exercise the retry without waiting out the real delays; the recording
/// pipeline always takes <see cref="Default"/>.
/// </remarks>
public sealed record SpotifyPollingOptions(TimeSpan SettleDelay, TimeSpan RetryDelay, int MaximumAttempts)
{
    /// <summary>
    /// The reference's timings, with a longer tail.
    /// </summary>
    /// <remarks>
    /// It waited 100ms before the first poll and retried once a second later. Four attempts here
    /// because the whole thing is bounded by <see cref="TrackEnricher.DefaultDeadline"/> and runs
    /// concurrently with a recording that lasts minutes — roughly three seconds of chasing costs
    /// nothing and covers a backend that is slower than usual to advance.
    /// </remarks>
    public static SpotifyPollingOptions Default { get; } =
        new(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), MaximumAttempts: 4);
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
    private readonly SpotifyPollingOptions _polling = polling ?? SpotifyPollingOptions.Default;

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

        for (var attempt = 1; ; attempt++)
        {
            var playback = await client.Player.GetCurrentlyPlaying(
                new PlayerCurrentlyPlayingRequest(), cancellationToken);

            // A 204 with no body — nothing is playing — deserializes to null rather than throwing.
            var reported = playback?.Item as FullTrack;

            if (reported is not null && MatchesDetectedTrack(track, reported))
                return await ApplyAsync(track, reported, cancellationToken);

            // A podcast episode is not a track and never will be, whatever we ask again.
            if (playback?.Item is not null && reported is null) return false;

            if (attempt >= _polling.MaximumAttempts)
            {
                Log.Debug(
                    "Spotify still reported {Reported} for {Track} after {Attempts} attempts.",
                    Describe(reported),
                    track,
                    attempt);

                return false;
            }

            Log.Debug(
                "Spotify reported {Reported} while {Track} was detected; asking again.",
                Describe(reported),
                track);

            await Task.Delay(_polling.RetryDelay, cancellationToken);
        }
    }

    private async Task<bool> ApplyAsync(Track track, FullTrack spotifyTrack, CancellationToken cancellationToken)
    {
        SpotifyTrackMapper.Apply(track, spotifyTrack);

        var albumId = spotifyTrack.Album?.Id;
        if (string.IsNullOrEmpty(albumId)) return true;

        var album = await client.Albums.Get(albumId, cancellationToken);
        SpotifyTrackMapper.Apply(track, album);

        return true;
    }

    /// <summary>
    /// Whether the track Spotify says is playing is the one the window title already
    /// identified — comparing on the title only, the one field both sources agree is present
    /// before any enrichment has run.
    /// </summary>
    private static bool MatchesDetectedTrack(Track track, FullTrack spotifyTrack)
    {
        var (titleTags, _) = SpotifyTitleParser.SplitTitle(spotifyTrack.Name ?? string.Empty);

        return SpotifyTitleParser.TagAt(titleTags, 1) == track.Title;
    }

    /// <summary>What Spotify answered, for a log line that can actually be diagnosed.</summary>
    private static string Describe(FullTrack? reported) => reported?.Name ?? "nothing playing";
}
