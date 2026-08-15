using Offstream.Core.Metadata;

namespace Offstream.Core.Spotify;

/// <summary>The displayed track changed.</summary>
public sealed class TrackChangedEventArgs(Track? oldTrack, Track newTrack) : EventArgs
{
    /// <summary>What was playing before, or null on the first observation.</summary>
    public Track? OldTrack { get; } = oldTrack;

    public Track NewTrack { get; } = newTrack;
}

/// <summary>Playback started or stopped.</summary>
/// <param name="playing">Whether Spotify is playing now.</param>
/// <param name="track">
/// What it is playing, as this observation saw it. Carried on the event because
/// <see cref="SpotifyPoller.CurrentTrack"/> is not updated until the poll finishes — a handler
/// reading it here would get the previous observation, whose play state is the one that just
/// stopped being true.
/// </param>
public sealed class PlayStateChangedEventArgs(bool playing, Track track) : EventArgs
{
    public bool Playing { get; } = playing;

    public Track Track { get; } = track;
}

/// <summary>The elapsed position within the current track changed.</summary>
public sealed class TrackTimeChangedEventArgs(int trackTimeSeconds) : EventArgs
{
    public int TrackTimeSeconds { get; } = trackTimeSeconds;
}
