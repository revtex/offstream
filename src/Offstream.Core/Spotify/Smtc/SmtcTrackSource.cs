using Offstream.Core.Metadata;

namespace Offstream.Core.Spotify.Smtc;

/// <summary>What Windows' media transport controls report about Spotify at one moment.</summary>
/// <param name="Artist">The performing artist, as Spotify published it to the system.</param>
/// <param name="Title">The track title.</param>
/// <param name="Album">The album, when Spotify supplied one.</param>
/// <param name="IsPlaying">Whether the session is playing rather than paused or stopped.</param>
/// <param name="AlbumArtist">
/// Who the album is credited to, which differs from <paramref name="Artist"/> on compilations
/// and features — the distinction a library groups by.
/// </param>
/// <param name="TrackNumber">The position on the album, or null when Spotify did not say.</param>
/// <remarks>
/// <para>
/// A snapshot rather than the live WinRT session: it makes the mapping below a pure function of
/// its values, which is what lets every rule in it be tested without a media session, an audio
/// endpoint, or Spotify installed.
/// </para>
/// <para>
/// The last two are optional so that every existing construction site — and the window-title
/// path, which has no such information — keeps working unchanged.
/// </para>
/// </remarks>
public readonly record struct SmtcSnapshot(
    string? Artist,
    string? Title,
    string? Album,
    bool IsPlaying,
    string? AlbumArtist = null,
    int? TrackNumber = null);

/// <summary>Reads Spotify's media transport session, if it has one.</summary>
public interface ISmtcSessions
{
    /// <summary>The current snapshot, or null when Spotify has no session registered.</summary>
    Task<SmtcSnapshot?> GetSpotifySnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reports the current track from the Windows media transport controls (SMTC) rather than from
/// Spotify's window title.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The window title is only readable while Spotify has a window. Minimised
/// to the tray it has none, and the predecessor simply stopped detecting tracks — the recorder sat
/// idle through a whole session because the user tidied the taskbar. SMTC is the same information
/// published deliberately by the app for the system to display on the lock screen and volume
/// overlay, so it survives having no window at all.
/// </para>
/// <para>
/// <b>It is also better information.</b> The window title is one string that has to be split back
/// into artist and title on a separator that can legitimately appear inside either of them; SMTC
/// hands over separate fields, and an album with them. So this is the primary source and the title
/// is the fallback (see <see cref="PreferredTrackSource"/>) — not the other way round.
/// </para>
/// <para>
/// <b>What it does not report is play position.</b> SMTC exposes a timeline, but Spotify populates
/// it inconsistently, and the recording pipeline already derives elapsed time from a monotonic
/// clock anchored at the track change. Reading a second, disagreeing clock here would reintroduce
/// exactly the drift that was removed.
/// </para>
/// </remarks>
public sealed class SmtcTrackSource(ISmtcSessions sessions) : ITrackSource
{
    private readonly ISmtcSessions _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    /// <inheritdoc />
    public async Task<Track?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessions.GetSpotifySnapshotAsync(cancellationToken);

        return snapshot is { } reported ? ToTrack(reported) : null;
    }

    /// <summary>
    /// Maps a snapshot onto a track, matching what the title parser produces for the same moment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Advertisements are detected on the same two rules as the title path</b>, because Spotify
    /// announces them the same way to both: the literal placeholder title, or a track that is
    /// playing with no artist attached. Getting this wrong is expensive in a way a missing tag is
    /// not — an undetected ad is recorded as a song and written to the library.
    /// </para>
    /// <para>
    /// A session that is paused is never an ad, whatever it says. The placeholder lingers after
    /// playback stops, and treating it as an ad then would suppress the next real track.
    /// </para>
    /// <para>
    /// <b>Album artist and track number are carried even though a provider usually replaces
    /// them.</b> They are the floor: when no metadata provider is configured, or when the one
    /// that is cannot match the track, these are what the file is tagged with — and they come
    /// from the client that is playing the track, so they describe it with certainty rather than
    /// with a lookup's confidence. See the mappers, which fill rather than clear.
    /// </para>
    /// </remarks>
    public static Track ToTrack(SmtcSnapshot snapshot)
    {
        var hasArtist = !string.IsNullOrWhiteSpace(snapshot.Artist);
        var albumArtist = Trimmed(snapshot.AlbumArtist);

        return new Track
        {
            Artist = Trimmed(snapshot.Artist),
            Title = Trimmed(snapshot.Title),
            Album = Trimmed(snapshot.Album),
            AlbumArtists = albumArtist is null ? null : [albumArtist],

            // Spotify numbers from 1, so a zero means "not reported" rather than a zeroth track.
            AlbumPosition = snapshot.TrackNumber is > 0 ? snapshot.TrackNumber : null,
            Playing = snapshot.IsPlaying,
            Ad = snapshot.IsPlaying && (snapshot.Title.IsAdvertisement() || !hasArtist),
        };
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
