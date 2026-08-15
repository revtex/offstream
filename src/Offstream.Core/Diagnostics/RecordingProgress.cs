namespace Offstream.Core.Diagnostics;

/// <summary>What the recorder is currently doing.</summary>
public enum RecordingStage
{
    Idle,
    WaitingForTrack,
    Recording,
    Encoding,
    Tagging,
    Stopped,
}

/// <summary>
/// A single progress update from the recording pipeline.
/// </summary>
/// <remarks>
/// This type is why <c>Offstream.Core</c> needs no UI reference. The predecessor passed its
/// form interface straight into the watcher and recorder so they could write to the console
/// pane, which made the whole pipeline untestable without a form mock. The core reports
/// through <see cref="IProgress{T}"/> of this type instead, and the shell decides what to
/// render.
/// </remarks>
/// <param name="Stage">Where the pipeline is.</param>
/// <param name="Track">
/// Human-readable description of the track this report is <i>about</i>, when one is known —
/// which is not always the one playing. Encoding and tagging run on the previous track while
/// the next one records, so those reports name a song that finished minutes ago.
/// </param>
/// <param name="Elapsed">Time spent on the current track, when recording.</param>
/// <param name="Message">Free-text detail for the log pane.</param>
/// <param name="NowPlaying">
/// What is playing at the instant of the report, regardless of what the report is about. This is
/// what a now-playing display wants; <paramref name="Track"/> is what a log line wants.
/// </param>
/// <param name="ConcernsNowPlaying">
/// Whether <paramref name="Track"/> and <paramref name="NowPlaying"/> are the same recording, so
/// that <paramref name="Elapsed"/> and <paramref name="Stage"/> describe the live track rather
/// than the tail end of an earlier one.
/// </param>
public sealed record RecordingProgress(
    RecordingStage Stage,
    string? Track = null,
    TimeSpan? Elapsed = null,
    string? Message = null,
    string? NowPlaying = null,
    bool ConcernsNowPlaying = true)
{
    public static RecordingProgress Idle { get; } = new(RecordingStage.Idle);

    public static RecordingProgress Info(string message) =>
        new(RecordingStage.Idle, Message: message);
}
