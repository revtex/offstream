using NAudio.CoreAudioApi.Interfaces;
using Offstream.Core.Interop;
using Offstream.Core.Spotify;

namespace Offstream.Core.Audio;

/// <summary>
/// Answers "is Spotify making sound right now?" from its WASAPI session state.
/// </summary>
/// <remarks>
/// <para>
/// This is the one bit of the audio stack that track detection needs, which is why
/// <see cref="ISpotifyPlaybackProbe"/> is a single method rather than a reference to the
/// whole session manager.
/// </para>
/// <para>
/// Note what "playing" means here: a session is <c>Active</c> while audio is flowing and
/// drops to <c>Inactive</c> in the gap between tracks. The detector deliberately treats a
/// real window title as playing even when this returns false, because otherwise the first
/// seconds of every song are lost — see <see cref="SpotifyTrackDetector"/>.
/// </para>
/// </remarks>
public sealed class SpotifyPlaybackProbe(IProcessManager processManager) : ISpotifyPlaybackProbe
{
    public Task<bool> IsSpotifyPlayingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var spotifyProcessIds = SpotifyTrackDetector
            .GetSpotifyProcesses(processManager)
            .Select(p => p.Id)
            .ToHashSet();

        if (spotifyProcessIds.Count == 0) return Task.FromResult(false);

        var playing = AudioSessions.List()
            .Any(session =>
                spotifyProcessIds.Contains(session.ProcessId)
                && session.State == AudioSessionState.AudioSessionStateActive);

        return Task.FromResult(playing);
    }
}
