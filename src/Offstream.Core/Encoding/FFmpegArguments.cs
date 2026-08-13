using System.Globalization;
using Offstream.Core.Metadata;

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
public sealed record EncodeRequest(
    string InputPath,
    string OutputPath,
    MediaFormat Format,
    int BitrateKbps,
    Track? Track = null,
    string? CoverArtPath = null,
    int? TrackNumberOverride = null);

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
        }

        foreach (var argument in profile.CodecArguments)
        {
            args.Add(argument.Replace(
                RateToken,
                request.BitrateKbps.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal));
        }

        if (request.Track is not null)
            args.AddRange(MetadataArguments(request.Track, request.TrackNumberOverride));

        args.Add(request.OutputPath);

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
    /// "number the files" setting is on.
    /// </param>
    public static IReadOnlyList<string> MetadataArguments(Track track, int? trackNumberOverride = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        var args = new List<string>();

        Add("title", track.ToTitleString());
        Add("artist", track.Artists);
        Add("album", track.Album);
        Add("album_artist", track.AlbumArtists is { Length: > 0 } ? string.Join(", ", track.AlbumArtists) : null);
        Add("genre", track.Genres is { Length: > 0 } ? string.Join(", ", track.Genres) : null);
        Add("date", track.Year?.ToString(CultureInfo.InvariantCulture));
        Add("track", (trackNumberOverride ?? track.AlbumPosition)?.ToString(CultureInfo.InvariantCulture));
        Add("disc", track.Disc?.ToString(CultureInfo.InvariantCulture));

        return args;

        void Add(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            // One argv element: ffmpeg parses "key=value" itself, so the value is never re-parsed
            // by a shell and needs no escaping.
            args.AddRange(["-metadata", $"{key}={value}"]);
        }
    }
}
