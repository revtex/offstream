using Windows.Media.Control;

namespace Offstream.Core.Spotify.Smtc;

/// <summary>
/// Reads Spotify's session from the real Windows media transport controls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately thin.</b> Everything decidable — what counts as an advertisement, when to fall
/// back, how a snapshot becomes a track — lives in <see cref="SmtcTrackSource"/> and
/// <see cref="PreferredTrackSource"/>, where it is testable. This class does the one thing that
/// cannot be faked: talk to the system. Keeping the untestable part free of decisions is the point.
/// </para>
/// <para>
/// <b>The session manager is fetched once and kept.</b> <c>RequestAsync</c> is a cross-process
/// call, and this is polled several times a second; re-requesting it per poll was the obvious
/// version and the wrong one. The manager stays valid as sessions come and go, so only
/// <c>GetSessions</c> needs to run each time.
/// </para>
/// <para>
/// <b>Spotify is matched by app id, loosely.</b> The desktop build reports <c>Spotify.exe</c> and
/// the Store build a packaged identity of the form <c>SpotifyAB.SpotifyMusic_…!Spotify</c>. A
/// substring match covers both without hard-coding either, and nothing else on a normal system
/// registers a media session whose id contains "Spotify".
/// </para>
/// </remarks>
public sealed class WindowsSmtcSessions : ISmtcSessions
{
    private const string SpotifyAppId = "Spotify";

    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    /// <inheritdoc />
    public async Task<SmtcSnapshot?> GetSpotifySnapshotAsync(CancellationToken cancellationToken = default)
    {
        var manager = _manager ??= await GlobalSystemMediaTransportControlsSessionManager
            .RequestAsync()
            .AsTask(cancellationToken);

        var session = FindSpotify(manager);

        if (session is null) return null;

        var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);

        if (properties is null) return null;

        var status = session.GetPlaybackInfo()?.PlaybackStatus;

        return new SmtcSnapshot(
            properties.Artist,
            properties.Title,
            properties.AlbumTitle,
            status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            properties.AlbumArtist,
            properties.TrackNumber);
    }

    private static GlobalSystemMediaTransportControlsSession? FindSpotify(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        foreach (var session in manager.GetSessions())
        {
            if (session.SourceAppUserModelId?.Contains(SpotifyAppId, StringComparison.OrdinalIgnoreCase) == true)
            {
                return session;
            }
        }

        return null;
    }
}
