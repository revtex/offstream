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
public sealed class PlayStateChangedEventArgs(bool playing) : EventArgs
{
    public bool Playing { get; } = playing;
}

/// <summary>The elapsed position within the current track changed.</summary>
public sealed class TrackTimeChangedEventArgs(int trackTimeSeconds) : EventArgs
{
    public int TrackTimeSeconds { get; } = trackTimeSeconds;
}
