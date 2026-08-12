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
/// <param name="Track">Human-readable track description, when one is known.</param>
/// <param name="Elapsed">Time spent on the current track, when recording.</param>
/// <param name="Message">Free-text detail for the log pane.</param>
public sealed record RecordingProgress(
    RecordingStage Stage,
    string? Track = null,
    TimeSpan? Elapsed = null,
    string? Message = null)
{
    public static RecordingProgress Idle { get; } = new(RecordingStage.Idle);

    public static RecordingProgress Info(string message) =>
        new(RecordingStage.Idle, Message: message);
}
