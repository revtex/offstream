using Offstream.Core.Spotify;
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
public interface ISpotifyMetadataProvider : IMetadataProvider;

/// <summary>
/// The read half of the reference implementation's <c>SpotifyAPI.UpdateTrack</c>: given an
/// authenticated client, fetch what Spotify says is playing and map it onto a track.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrower than the original. That method also retried on a delay, fell back to
/// Last.fm and reopened the auth dialog after repeated failures — orchestration that belongs
/// with whatever in the recording pipeline actually calls this, not in the HTTP-fetch-and-map
/// step itself. It is not wired into <c>RecordingSession</c> yet; that is provider-selection
/// work for Phase 5 (settings) and Phase 6 (the shell), once there is a UI to choose Spotify
/// over Last.fm in the first place.
/// </para>
/// <para>
/// <b>The title-match guard is kept.</b> Detection and this enrichment race independently: by
/// the time this call returns, the window title may have already moved to the next track. The
/// reference's <c>IsPlaybackTrackDetectedTrack</c> check — does what Spotify just reported
/// still match what was detected? — stops that race from tagging one track with another's
/// metadata, and is reproduced exactly.
/// </para>
/// </remarks>
public sealed class SpotifyMetadataProvider(ISpotifyClient client) : ISpotifyMetadataProvider
{
    /// <inheritdoc />
    public MetadataProvider Kind => MetadataProvider.Spotify;

    /// <inheritdoc />
    public async Task<bool> EnrichAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        var playback = await client.Player.GetCurrentlyPlaying(
            new PlayerCurrentlyPlayingRequest(), cancellationToken);

        // A 204 with no body — nothing is playing — deserializes to null rather than throwing.
        if (playback?.Item is not FullTrack spotifyTrack) return false;

        if (!MatchesDetectedTrack(track, spotifyTrack)) return false;

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
}
