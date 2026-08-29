namespace Offstream.Core.Spotify;

/// <summary>Drives Spotify's transport — the buttons a user would otherwise press themselves.</summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="ITrackSource"/> on purpose.</b> Reading what is playing and telling
/// Spotify to play something else are different privileges, and most of the app only needs the
/// first. A session handed no implementation simply never skips, which is how every existing
/// construction site keeps behaving exactly as it did.
/// </para>
/// <para>
/// <b>Not the Web API.</b> <c>POST /me/player/next</c> would work, but it needs the
/// <c>user-modify-playback-state</c> scope, a signed-in account and a Premium subscription — three
/// requirements for a convenience feature, and a third scope on a consent screen CLAUDE.md holds
/// to two. The Windows media transport controls carry the same command with no account, no scope
/// and no network, and they reach Spotify while it is minimised to the tray.
/// </para>
/// </remarks>
public interface IPlaybackControl
{
    /// <summary>
    /// Asks Spotify to move to the next track.
    /// </summary>
    /// <returns>
    /// Whether the command was accepted. False is ordinary, not a fault: Spotify refuses while an
    /// advertisement is playing and when there is nothing queued after the current track.
    /// </returns>
    Task<bool> TrySkipNextAsync(CancellationToken cancellationToken = default);
}
