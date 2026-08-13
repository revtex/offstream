using Offstream.Core.Metadata;

namespace Offstream.Core.Encoding;

/// <summary>
/// How one output format is produced: codec flags, file extension, and how it takes cover art.
/// </summary>
/// <param name="Format">The format this profile produces.</param>
/// <param name="Extension">File extension, without the dot.</param>
/// <param name="CodecArguments">
/// Codec flags. <c>{rate}</c> is substituted with the configured bitrate in kbps.
/// </param>
/// <param name="SupportsBitrate">Whether the bitrate setting means anything for this format.</param>
/// <param name="CoverArt">How, or whether, ffmpeg can attach cover art to this container.</param>
/// <param name="ContainerArguments">
/// Muxer flags, applied after the codec flags. Separate from <paramref name="CodecArguments"/>
/// because they configure the file being written rather than the audio going into it.
/// </param>
public sealed record EncodingProfile(
    MediaFormat Format,
    string Extension,
    IReadOnlyList<string> CodecArguments,
    bool SupportsBitrate,
    CoverArtSupport CoverArt,
    IReadOnlyList<string>? ContainerArguments = null)
{
    /// <inheritdoc cref="ContainerArguments"/>
    public IReadOnlyList<string> ContainerArguments { get; init; } = ContainerArguments ?? [];
}

/// <summary>How a container takes embedded cover art.</summary>
public enum CoverArtSupport
{
    /// <summary>The container cannot carry cover art at all.</summary>
    None,

    /// <summary>ffmpeg attaches it as a second video stream with <c>attached_pic</c> disposition.</summary>
    AttachedPicture,

    /// <summary>
    /// ffmpeg's support is unreliable here; TagLib# writes it after encoding instead (§5.2).
    /// </summary>
    PostProcess,
}

/// <summary>
/// The format profiles, as data rather than code.
/// </summary>
/// <remarks>
/// Plan §5.1 keeps these declarative on purpose: adding a format should be a table entry plus
/// a golden test, not a new branch in the encoder.
/// </remarks>
public static class EncodingProfiles
{
    private static readonly Dictionary<MediaFormat, EncodingProfile> All = new()
    {
        [MediaFormat.Mp3] = new(
            MediaFormat.Mp3,
            "mp3",
            ["-c:a", "libmp3lame", "-b:a", "{rate}k"],
            SupportsBitrate: true,
            CoverArtSupport.AttachedPicture,

            // ID3v2.3, not ffmpeg's default of 2.4. Windows Explorer's thumbnail handler and
            // Windows Media Player have never read v2.4 properly: the APIC picture is in the
            // file and neither shows it, which is exactly the "art works in VLC and nowhere
            // else" report. The predecessor tagged with TagLib#, which writes v2.3, so this is
            // also what restores parity. Nothing is lost by it — the full date and "4/12"
            // track numbers both survive, verified with ffprobe.
            ContainerArguments: ["-id3v2_version", "3"]),

        [MediaFormat.Wav] = new(
            MediaFormat.Wav,
            "wav",
            ["-c:a", "pcm_s16le"],
            SupportsBitrate: false,

            // WAV has no standard picture frame worth writing.
            CoverArtSupport.None),

        [MediaFormat.Opus] = new(
            MediaFormat.Opus,
            "opus",
            ["-c:a", "libopus", "-b:a", "{rate}k"],
            SupportsBitrate: true,

            // ffmpeg's METADATA_BLOCK_PICTURE support for Ogg is weak; TagLib# handles it (§5.2).
            CoverArtSupport.PostProcess),

        [MediaFormat.Flac] = new(
            MediaFormat.Flac,
            "flac",

            // Lossless, so bitrate does not apply; compression_level trades CPU for size only.
            ["-c:a", "flac", "-compression_level", "8"],
            SupportsBitrate: false,
            CoverArtSupport.AttachedPicture),

        [MediaFormat.Aac] = new(
            MediaFormat.Aac,
            "m4a",
            ["-c:a", "aac", "-b:a", "{rate}k"],
            SupportsBitrate: true,
            CoverArtSupport.AttachedPicture),
    };

    /// <summary>The profile for <paramref name="format"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">No profile is defined for the format.</exception>
    public static EncodingProfile For(MediaFormat format) =>
        All.TryGetValue(format, out var profile)
            ? profile
            : throw new ArgumentOutOfRangeException(nameof(format), format, "No encoding profile is defined.");

    /// <summary>Every defined profile.</summary>
    public static IReadOnlyCollection<EncodingProfile> Known => All.Values;
}
