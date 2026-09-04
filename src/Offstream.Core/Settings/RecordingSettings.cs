using System.Globalization;
using Offstream.Core.Encoding;
using Offstream.Core.Metadata;
using Offstream.Core.Naming;

namespace Offstream.Core.Settings;

/// <summary>
/// The subset of user settings the recording pipeline reads.
/// </summary>
/// <remarks>
/// <para>
/// Grown as the port proceeds rather than transcribed wholesale, so every field here is one
/// something actually uses. The JSON schema that persists this lands in Phase 5 (§6).
/// </para>
/// <para>
/// Bitrate is a plain kbps number, not the predecessor's <c>LAMEPreset</c>: NAudio.Lame is
/// removed and ffmpeg takes <c>-b:a {rate}k</c> (§5.1, §8). <see cref="BitrateMode"/> beside it
/// is not that preset returning — the preset bundled the rate and the encoder's rate mode into
/// one value, which is what made it impossible to validate. These are two values, and the
/// number stays a number.
/// </para>
/// </remarks>
public sealed class RecordingSettings
{
    /// <summary>Root folder recordings are written under.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Filename template; see <see cref="FileNameTemplate"/>.</summary>
    public string OutputTemplate { get; set; } = FileNameTemplate.Default;

    public MediaFormat MediaFormat { get; set; }

    /// <summary>Target bitrate in kbps for lossy formats.</summary>
    public int BitrateKbps { get; set; } = 320;

    /// <summary>How the encoder spends that bitrate, where the format offers a choice.</summary>
    public BitrateMode BitrateMode { get; set; }

    /// <summary>What to do when the output file already exists.</summary>
    public Recording.ExistingFilePolicy ExistingFilePolicy { get; set; }

    /// <summary>Whether the chosen policy leaves the recording already on disk alone.</summary>
    /// <remarks>
    /// The two keep-it policies differ only in what they ask Spotify to do next, which is no
    /// business of the code deciding whether to write a file. Asking the question once, here,
    /// is also what stops a fifth policy from having to be remembered at four call sites — and
    /// one of those sites reads <c>!=</c>, where a forgotten member fails silently by recording
    /// over a file the user asked to keep.
    /// </remarks>
    public bool KeepsTheExistingFile =>
        ExistingFilePolicy is Recording.ExistingFilePolicy.Skip
                           or Recording.ExistingFilePolicy.SkipAndMoveOn;

    /// <summary>Whether to ask Spotify to move on past a track already in the library.</summary>
    /// <remarks>
    /// One policy rather than a policy plus a switch, so this cannot be on under a policy that
    /// records the file again anyway — a state a hand-edited <c>settings.json</c> could once
    /// reach, and one the UI had to keep greying out to explain.
    /// </remarks>
    public bool HasSkipPastRecordedEnabled =>
        ExistingFilePolicy == Recording.ExistingFilePolicy.SkipAndMoveOn;

    /// <summary>Running counter used by the <c>{count}</c> token and the track-number tag.</summary>
    public int InternalOrderNumber { get; set; } = 1;

    /// <summary>Write the counter into the track-number tag as well as the file name.</summary>
    public bool OrderNumberInMediaTagEnabled { get; set; }

    /// <summary>How much of what Spotify plays is worth saving.</summary>
    public Recording.RecordSelection RecordSelection { get; set; }

    /// <summary>Discard recordings shorter than this.</summary>
    public int MinimumRecordedLengthSeconds { get; set; } = 30;

    /// <summary>
    /// Stop recording after this long, as a six-digit <c>hhmmss</c> string.
    /// </summary>
    /// <remarks>
    /// Kept as text because that is what the UI field holds and what round-trips through
    /// settings. "000000" means no timer, which is why it is checked explicitly — a zero
    /// duration and "disabled" are the same intent but not the same value.
    /// </remarks>
    public string? RecordingTimer { get; set; }

    /// <summary>Whether <see cref="RecordingTimer"/> describes a usable duration.</summary>
    public bool HasRecordingTimerEnabled =>
        !string.IsNullOrEmpty(RecordingTimer)
        && RecordingTimer.Length == 6
        && RecordingTimer != "000000"
        && RecordingTimer.All(char.IsAsciiDigit);

    /// <summary>The recording timer as a duration; <see cref="TimeSpan.Zero"/> when disabled.</summary>
    public TimeSpan RecordingTimerDuration =>
        HasRecordingTimerEnabled
            ? new TimeSpan(
                int.Parse(RecordingTimer!.AsSpan(0, 2), CultureInfo.InvariantCulture),
                int.Parse(RecordingTimer.AsSpan(2, 2), CultureInfo.InvariantCulture),
                int.Parse(RecordingTimer.AsSpan(4, 2), CultureInfo.InvariantCulture))
            : TimeSpan.Zero;

    /// <summary>Highest counter the current template can represent.</summary>
    public int OrderNumberMax => FileNameTemplate.GetCounterMax(OutputTemplate);

    /// <summary>The counter, when the template actually uses it; otherwise null.</summary>
    /// <remarks>
    /// Clamped to <see cref="OrderNumberMax"/>. Without the clamp a counter past the
    /// template's padding would render wider than the mask — <c>{count:0000}</c> emitting
    /// "10000" — which breaks both sort order and the "have I already recorded this?" check.
    /// Saturating instead lets <see cref="RecordingPolicy.IsMaxOrderNumberAsFileExceeded"/>
    /// notice the ceiling and stop.
    /// </remarks>
    public int? OrderNumberAsFile =>
        FileNameTemplate.UsesCounter(OutputTemplate)
            ? Math.Min(InternalOrderNumber, OrderNumberMax)
            : null;

    /// <summary>Whether a counter is in play at all, for the file name or the tag.</summary>
    public bool HasOrderNumberEnabled =>
        OrderNumberInMediaTagEnabled || FileNameTemplate.UsesCounter(OutputTemplate);

    /// <summary>The counter, when it should go into the track-number tag; otherwise null.</summary>
    public int? OrderNumberAsTag => OrderNumberInMediaTagEnabled ? InternalOrderNumber : null;

    /// <summary>File extension for <see cref="MediaFormat"/>, without the dot.</summary>
    /// <remarks>
    /// Taken from the encoding profile rather than from the enum name, because for AAC the two
    /// disagree: the profile encodes into an MP4 container, so the file is an <c>.m4a</c> and
    /// naming it <c>.aac</c> — which lower-casing the enum member did — produced a file Windows
    /// and most players refused to open.
    /// </remarks>
    public string MediaFormatExtension => EncodingProfiles.For(MediaFormat).Extension;
}
