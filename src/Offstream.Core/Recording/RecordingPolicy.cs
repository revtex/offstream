using Offstream.Core.Metadata;
using Offstream.Core.Settings;

namespace Offstream.Core.Recording;

/// <summary>
/// Decides whether the current track should be recorded.
/// </summary>
/// <remarks>
/// <para>
/// These predicates lived on the reference implementation's <c>Watcher</c>, mixed in with
/// audio-session routing, sleep prevention, timers and direct calls into the WinForms form.
/// Testing one rule therefore meant constructing the whole orchestrator with a form mock.
/// </para>
/// <para>
/// The rules are pure functions of the track plus settings, so they live here, and the
/// orchestration that needs real audio stays separate. This is the §3 decoupling applied
/// where it actually pays: every rule below is directly testable.
/// </para>
/// </remarks>
public sealed class RecordingPolicy(RecordingSettings settings)
{
    /// <summary>
    /// Whether to record something that is not a recognisable "artist - title" track.
    /// </summary>
    /// <remarks>
    /// Requires "record everything" and that ad muting is off — muting an ad and recording
    /// it at the same time would write a file of silence. Ads themselves are only included
    /// when explicitly enabled.
    /// </remarks>
    public bool IsRecordUnknownActive(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        return !settings.MuteAdsEnabled
            && settings.RecordEverythingEnabled
            && (track.IsUnknownPlaying || settings.RecordAdsEnabled);
    }

    /// <summary>Whether this track's type may be recorded at all.</summary>
    public bool IsTypeAllowed(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        return track.IsNormalPlaying || IsRecordUnknownActive(track);
    }

    /// <summary>
    /// Whether the file counter has reached the ceiling its padding allows, e.g. 9999 for
    /// <c>{count:0000}</c>. Recording past it would overwrite or misorder files.
    /// </summary>
    public bool IsMaxOrderNumberAsFileExceeded =>
        settings.OrderNumberAsFile.HasValue && settings.OrderNumberAsFile == settings.OrderNumberMax;

    /// <summary>
    /// Whether <paramref name="candidate"/> is a different track from <paramref name="current"/>.
    /// </summary>
    /// <remarks>
    /// A null or wholly empty track is never "new": Spotify reports blank titles while
    /// starting up and between tracks, and treating those as changes would split recordings.
    /// </remarks>
    public static bool IsNewTrack(Track? current, Track? candidate)
    {
        if (candidate is null || new Track().Equals(candidate)) return false;

        return !(current?.Equals(candidate) ?? false);
    }
}
