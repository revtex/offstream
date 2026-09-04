using System.Globalization;
using Offstream.Core.Metadata;
using Offstream.Core.Naming;

namespace Offstream.Core.Encoding;

/// <summary>Everything needed to encode one recording.</summary>
/// <param name="InputPath">The captured WAV.</param>
/// <param name="OutputPath">Where the finished file goes.</param>
/// <param name="Format">Output format.</param>
/// <param name="BitrateKbps">Target bitrate, ignored by lossless formats.</param>
/// <param name="Track">Track metadata to write as tags, or null to write none.</param>
/// <param name="CoverArtPath">A local image to embed, or null.</param>
/// <param name="TrackNumberOverride">
/// Written into the track-number tag instead of the album position, for the "number the files"
/// setting. Only the tag is affected — the <c>{track}</c> filename token keeps meaning the
/// position within the album, which is what the reference implementation did too.
/// </param>
/// <param name="BitrateMode">
/// How the encoder spends <paramref name="BitrateKbps"/>. Ignored by every format whose
/// profile declares no <see cref="EncodingProfile.AverageBitrateArguments"/>.
/// </param>
public sealed record EncodeRequest(
    string InputPath,
    string OutputPath,
    MediaFormat Format,
    int BitrateKbps,
    Track? Track = null,
    string? CoverArtPath = null,
    int? TrackNumberOverride = null,
    BitrateMode BitrateMode = BitrateMode.Average);

/// <summary>
/// Builds the ffmpeg argument vector for an <see cref="EncodeRequest"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>An argument list, never a command string.</b> Track metadata comes from Spotify window
/// titles, which is untrusted input: a title containing a quote or an ampersand would break
/// or subvert a concatenated command line. Passing argv elements individually removes the
/// entire injection class structurally, which is why the predecessor needed hand-written
/// <c>CommandLineToArgvW</c> escaping and this does not (§5.3).
/// </para>
/// <para>
/// Construction is pure so the exact vector can be asserted without invoking ffmpeg —
/// regression suite 2 in §9.2. Flag drift is then caught by a diff, not by a failed encode.
/// </para>
/// </remarks>
public static class FFmpegArguments
{
    /// <summary>Substituted with the bitrate in profile codec arguments.</summary>
    private const string RateToken = "{rate}";

    /// <summary>Builds the full argument vector, in the order ffmpeg expects.</summary>
    public static IReadOnlyList<string> Build(EncodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = EncodingProfiles.For(request.Format);
        var attachCover = request.CoverArtPath is not null && profile.CoverArt == CoverArtSupport.AttachedPicture;

        var args = new List<string>
        {
            // Quieter output, and never block waiting on stdin for an overwrite prompt.
            "-hide_banner",
            "-nostdin",
            "-y",
            "-i", request.InputPath,
        };

        if (attachCover)
        {
            args.AddRange(["-i", request.CoverArtPath!]);

            // With two inputs the mapping must be explicit, or ffmpeg picks one stream per type.
            args.AddRange(["-map", "0:a", "-map", "1:v"]);
            args.AddRange(["-c:v", "mjpeg", "-disposition:v", "attached_pic"]);

            // Types the picture and names it. -disposition:v attached_pic only says "this
            // stream is cover art" — it leaves the picture type at 0, which both ID3's APIC
            // frame and FLAC's METADATA_BLOCK_PICTURE spell "Other", and software looking for a
            // front cover specifically then passes over a file that has one.
            //
            // The two arguments do different jobs, and neither is free text. "comment" is read
            // by the muxer as the picture type and only takes the spellings the format defines:
            // "Cover (front)" selects type 3, and anything it does not recognise silently falls
            // back to "Other" — so this string is a constant, not a caption. "title" is the
            // description, and that one is genuinely free text. Both verified by encoding with
            // each argument alone and reading the result back.
            //
            // M4A takes them and stores neither: the mov muxer keeps a cover as a bare atom with
            // nowhere to put a type or a description. They are still written in the shared branch
            // rather than per format, because the alternative is an exception list that has to
            // stay right about a muxer detail nobody re-checks. English on purpose: this is tag
            // content other software matches on, not a string the user reads.
            args.AddRange(["-metadata:s:v", "title=Album cover"]);
            args.AddRange(["-metadata:s:v", "comment=Cover (front)"]);
        }

        foreach (var argument in profile.CodecArguments)
        {
            args.Add(argument.Replace(
                RateToken,
                request.BitrateKbps.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal));
        }

        // After the codec flags, because the switch modifies the -b:a that precedes it. A
        // profile with nothing to declare contributes nothing, which is how the lossless and
        // already-variable formats opt out without a branch on the format here.
        if (request.BitrateMode == BitrateMode.Average)
            args.AddRange(profile.AverageBitrateArguments);

        args.AddRange(profile.ContainerArguments);

        if (request.Track is not null)
            args.AddRange(MetadataArguments(request.Track, request.TrackNumberOverride));

        // Extended form when it is long enough to need it. ffmpeg is a separate process with its
        // own manifest, so it does not inherit any long-path opt-in of ours; the \\?\ prefix is
        // what carries past MAX_PATH, and it survives ArgumentList untouched. Verified with
        // ffmpeg 8.1 writing to a 298-character destination. See LongPath.
        args.Add(LongPath.Extended(request.OutputPath));

        return args;
    }

    /// <summary>
    /// The tag set, matching the reference implementation's coverage (§5.2).
    /// </summary>
    /// <remarks>
    /// Empty values are omitted rather than written blank: an empty tag displays as a blank
    /// field in players, whereas an absent one lets them fall back to the file name.
    /// </remarks>
    /// <param name="track">The track to tag.</param>
    /// <param name="trackNumberOverride">
    /// Takes the place of <see cref="Track.AlbumPosition"/> in the track-number tag when the
    /// "number the files" setting is on. Suppresses the album's track total with it: a counter
    /// of 42 is not the forty-second of anything.
    /// </param>
    public static IReadOnlyList<string> MetadataArguments(Track track, int? trackNumberOverride = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        var args = new List<string>();

        Add("title", track.ToTitleString());
        Add("artist", PerformerCredit(track));
        Add("album", track.Album);
        Add("album_artist", Join(track.AlbumArtists));
        Add("genre", Join(track.Genres));

        // The full date when the provider knows one, the bare year otherwise. Every container
        // Offstream writes stores either; ID3v2.3 splits it into TYER and TDAT itself.
        Add("date", track.ReleaseDate ?? track.Year?.ToString(CultureInfo.InvariantCulture));

        Add("track", TrackNumber(track, trackNumberOverride));
        Add("disc", track.Disc?.ToString(CultureInfo.InvariantCulture));
        Add("copyright", track.Copyright);

        return args;

        void Add(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            // One argv element: ffmpeg parses "key=value" itself, so the value is never re-parsed
            // by a shell and needs no escaping.
            args.AddRange(["-metadata", $"{key}={value}"]);
        }
    }

    /// <summary>
    /// Who the <c>artist</c> tag credits.
    /// </summary>
    /// <remarks>
    /// <b>The track's own performers, not the album's artists.</b> <see cref="Track.Artists"/>
    /// returns the album artists whenever they are known, which is almost always once a provider
    /// has run — so using it here wrote the album artist into <c>artist</c>, made it identical to
    /// <c>album_artist</c> on every file, and dropped featured artists entirely. The predecessor
    /// wrote the two to separate frames (TPE1 from <c>Performers</c>, TPE2 from
    /// <c>AlbumArtists</c>), and this restores that. The <c>{artist}</c> filename token still
    /// renders from <see cref="Track.Artists"/>, so names on disk are unaffected.
    /// </remarks>
    private static string? PerformerCredit(Track track) => Join(track.Performers) ?? track.Artists;

    /// <summary>
    /// The track number, as "4/12" when the album's length is known.
    /// </summary>
    /// <remarks>
    /// The "of how many" form is what players use to tell a partial rip from a complete one, and
    /// it costs nothing — the album call that supplies it is one Offstream already makes.
    /// </remarks>
    private static string? TrackNumber(Track track, int? trackNumberOverride)
    {
        if (trackNumberOverride is { } counter) return counter.ToString(CultureInfo.InvariantCulture);

        if (track.AlbumPosition is not { } position) return null;

        return track.AlbumTrackCount is { } total && total >= position
            ? string.Create(CultureInfo.InvariantCulture, $"{position}/{total}")
            : position.ToString(CultureInfo.InvariantCulture);
    }

    private static string? Join(string[]? values) =>
        values is { Length: > 0 } ? string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v))) : null;
}
