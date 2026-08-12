using Offstream.Core.Interop;
using Offstream.Core.Metadata;

namespace Offstream.Core.Spotify;

/// <summary>Reports whether Spotify's audio session is currently producing sound.</summary>
/// <remarks>
/// Narrow on purpose. Track detection needs one bit from the audio stack, so it depends on
/// this rather than on the whole session manager — which keeps detection testable without
/// any audio hardware.
/// </remarks>
public interface ISpotifyPlaybackProbe
{
    Task<bool> IsSpotifyPlayingAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Finds the Spotify process and reads the current track from its window title.
/// </summary>
/// <remarks>
/// Ported from the reference implementation's <c>SpotifyProcess</c>. Two behaviours carry
/// over because they are not obvious:
/// <list type="bullet">
///   <item>
///     The process is matched by <em>name</em> against Spotify's idle titles, and only a
///     process with a non-empty window title counts. Spotify runs several helper processes;
///     only one owns the window whose title changes with the track.
///   </item>
///   <item>
///     A title that is not one of Spotify's idle strings is treated as playing even when the
///     audio probe says otherwise. The probe reports silence during the gap between tracks,
///     and trusting it alone drops the first seconds of a song.
///   </item>
/// </list>
/// </remarks>
public sealed class SpotifyTrackDetector(IProcessManager processManager, ISpotifyPlaybackProbe playbackProbe)
{
    private int? _spotifyProcessId = FindMainSpotifyProcess(processManager)?.Id;

    /// <summary>
    /// The currently displayed track, or null when Spotify is not running or has no window title.
    /// </summary>
    public async Task<Track?> GetCurrentTrackAsync(CancellationToken cancellationToken = default)
    {
        var (windowTitle, isAudioPlaying) = await ReadWindowAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(windowTitle)) return null;

        var isIdleTitle = windowTitle.IsNullOrIdle();

        return SpotifyTitleParser.Parse(new SpotifyWindow(windowTitle, isAudioPlaying || !isIdleTitle));
    }

    private async Task<(string? WindowTitle, bool IsAudioPlaying)> ReadWindowAsync(
        CancellationToken cancellationToken)
    {
        if (_spotifyProcessId is null)
        {
            // Spotify was not running last time we looked; check again before giving up.
            _spotifyProcessId = FindMainSpotifyProcess(processManager)?.Id;
            return (null, false);
        }

        var isAudioPlaying = await playbackProbe.IsSpotifyPlayingAsync(cancellationToken);
        var process = processManager.GetProcessById(_spotifyProcessId.Value);

        if (process is null)
        {
            // It exited; re-resolve on the next poll rather than staying stuck on a dead id.
            _spotifyProcessId = null;
            return (null, isAudioPlaying);
        }

        return (process.MainWindowTitle ?? string.Empty, isAudioPlaying);
    }

    /// <summary>Every process whose name matches Spotify.</summary>
    public static IReadOnlyList<IProcessInfo> GetSpotifyProcesses(IProcessManager processManager)
    {
        ArgumentNullException.ThrowIfNull(processManager);

        return [.. processManager.GetProcesses().Where(p => p.ProcessName.IsIdle())];
    }

    /// <summary>The window handle of Spotify's main window, when it has one.</summary>
    public static nint? GetMainSpotifyWindowHandle(IProcessManager processManager) =>
        FindMainSpotifyProcess(processManager)?.MainWindowHandle;

    private static IProcessInfo? FindMainSpotifyProcess(IProcessManager processManager)
    {
        ArgumentNullException.ThrowIfNull(processManager);

        return processManager.GetProcesses()
            .FirstOrDefault(p => p.ProcessName.IsIdle() && !string.IsNullOrEmpty(p.MainWindowTitle));
    }
}
