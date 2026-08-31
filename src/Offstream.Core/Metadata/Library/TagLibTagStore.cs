namespace Offstream.Core.Metadata.Library;

/// <summary>Reads and writes tags on a file that already exists.</summary>
/// <remarks>
/// The seam exists so <see cref="LibraryScanner"/> and <see cref="LibraryTagWriter"/> can be
/// tested without a real audio file for every case, and so the one place that touches TagLib#
/// stays one place.
/// </remarks>
public interface ILibraryTagStore
{
    /// <summary>Reads what <paramref name="path"/> already carries.</summary>
    /// <exception cref="LibraryTagException">The file could not be read.</exception>
    Track Read(string path);

    /// <summary>Writes <paramref name="track"/> into <paramref name="path"/>, cover art included.</summary>
    /// <exception cref="LibraryTagException">The file could not be written.</exception>
    void Write(string path, Track track, byte[]? coverArt);
}

/// <summary>A file could not be read or written, with a reason fit to show the user.</summary>
public sealed class LibraryTagException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>The TagLib# implementation, and the only code here that names TagLib#.</summary>
/// <remarks>
/// <para>
/// <b>Why TagLib# and not ffmpeg.</b> Every tag on a *recording* is written by ffmpeg during the
/// encode, and that does not change. Retagging a file that already exists is a different problem:
/// ffmpeg cannot alter a tag in place, so the equivalent would be remuxing the whole file to
/// change one string — slower, and it rewrites audio that had nothing wrong with it. TagLib#
/// edits the tag and leaves the stream alone. The rule that all *conversion* goes through ffmpeg
/// is untouched, because none of this converts anything.
/// </para>
/// <para>
/// <b>Tags and picture go in one session.</b> <see cref="CoverArtWriter"/> opens and saves the
/// file by itself, which is right for the encode path where it is the only thing left to do.
/// Calling it here would save the file twice for one edit, so the picture is written alongside
/// the text instead. Its <see cref="CoverArtWriter.MimeTypeFor"/> is still reused — sniffing a
/// MIME type is the part worth sharing.
/// </para>
/// </remarks>
public sealed class TagLibTagStore : ILibraryTagStore
{
    /// <summary>
    /// Pins written ID3 tags to v2.3.
    /// </summary>
    /// <remarks>
    /// TagLib# defaults to writing ID3v2.4, and this project deliberately writes v2.3 — the tag
    /// version Windows Explorer, Windows Media Player and a long tail of car stereos actually
    /// read. Letting a retag flip an MP3 to v2.4 would make tags vanish from exactly the places
    /// the user is most likely to be looking, on files that displayed correctly before Offstream
    /// touched them. Static because TagLib# exposes it as global state; set once, on first use.
    /// </remarks>
    static TagLibTagStore()
    {
        TagLib.Id3v2.Tag.DefaultVersion = 3;
        TagLib.Id3v2.Tag.ForceDefaultVersion = true;
    }

    /// <inheritdoc />
    public Track Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;

            var track = new Track
            {
                Album = NullIfBlank(tag.Album),
                Genres = tag.Genres is { Length: > 0 } genres ? genres : null,
                Year = tag.Year > 0 ? (int)tag.Year : null,
                AlbumArtists = tag.AlbumArtists is { Length: > 0 } albumArtists ? albumArtists : null,
                Performers = tag.Performers is { Length: > 0 } performers ? performers : null,
                AlbumPosition = PositiveOrNull(tag.Track),
                AlbumTrackCount = PositiveOrNull(tag.TrackCount),
                Disc = PositiveOrNull(tag.Disc),
                Copyright = NullIfBlank(tag.Copyright),
                AlbumArtImage = tag.Pictures is [var picture, ..] ? picture.Data?.Data : null,
            };

            // Through the ordinary setters on purpose: these write the "scraped" half of Track,
            // which a provider's answer is allowed to override. Seeding the API half instead
            // would make every lookup return the tags the file already had.
            track.Artist = NullIfBlank(tag.FirstPerformer) ?? NullIfBlank(tag.FirstAlbumArtist);
            track.Title = NullIfBlank(tag.Title);

            return track;
        }
        catch (Exception ex) when (IsExpectedTagFailure(ex))
        {
            throw new LibraryTagException(Describe(ex, path), ex);
        }
    }

    /// <inheritdoc />
    public void Write(string path, Track track, byte[]? coverArt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(track);

        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;

            // Every tag the recording path writes, and nothing beyond it. The two lists being the
            // same list is the point: a tag Offstream puts into a file when it records it and
            // cannot put right afterwards is a tag the user has no way to fix at all. Composer,
            // comment and BPM are absent from both, so this is not a general-purpose tag editor.
            //
            // Empty is still never written. Clearing a box leaves the file's own value alone
            // rather than erasing it, which is the same promise a lookup makes.
            if (!string.IsNullOrWhiteSpace(track.Title)) tag.Title = track.Title;
            if (!string.IsNullOrWhiteSpace(track.Album)) tag.Album = track.Album;
            if (track.Year is > 0) tag.Year = (uint)track.Year.Value;
            if (track.Genres is { Length: > 0 }) tag.Genres = track.Genres;
            if (track.AlbumPosition is > 0) tag.Track = (uint)track.AlbumPosition.Value;
            if (track.AlbumTrackCount is > 0) tag.TrackCount = (uint)track.AlbumTrackCount.Value;
            if (track.Disc is > 0) tag.Disc = (uint)track.Disc.Value;
            if (!string.IsNullOrWhiteSpace(track.Copyright)) tag.Copyright = track.Copyright;

            // Before the artist block on purpose: that block fills the album artist in from the
            // artist only when nothing else has, so an album artist the user typed has to be in
            // place by the time it runs or it would lose to the fallback.
            if (track.AlbumArtists is { Length: > 0 } albumArtists) tag.AlbumArtists = albumArtists;

            if (!string.IsNullOrWhiteSpace(track.Artist))
            {
                tag.Performers = KeepsEveryPerformer(track) ? track.Performers! : [track.Artist];

                // Without this the album artist keeps whatever was there before, and players that
                // group by album artist file the corrected track under the old, wrong name.
                if (tag.AlbumArtists is not { Length: > 0 }) tag.AlbumArtists = [track.Artist];
            }

            if (coverArt is { Length: > 0 })
            {
                tag.Pictures =
                [
                    new TagLib.Picture(new TagLib.ByteVector(coverArt))
                    {
                        Type = TagLib.PictureType.FrontCover,
                        MimeType = "image/jpeg",
                        Description = "Cover",
                    },
                ];
            }

            file.Save();
        }
        catch (Exception ex) when (IsExpectedTagFailure(ex))
        {
            throw new LibraryTagException(Describe(ex, path), ex);
        }
    }

    /// <summary>The failures a file on someone's disk produces in normal use.</summary>
    /// <remarks>
    /// Deliberately a closed list. Anything outside it is a defect here rather than a fact about
    /// the file, and swallowing it would turn a bug into a row that quietly says "failed".
    /// </remarks>
    private static bool IsExpectedTagFailure(Exception ex) =>
        ex is TagLib.CorruptFileException
            or TagLib.UnsupportedFormatException
            or IOException
            or UnauthorizedAccessException;

    /// <summary>Turns a failure into something worth showing in a table cell.</summary>
    /// <remarks>
    /// The locked-file case earns its own sentence because it is both the most common and the
    /// only one the user can fix in five seconds. Windows reports it as a plain
    /// <see cref="IOException"/> whose message names no application, so "the file is in use" is
    /// more useful than what the exception actually says.
    /// </remarks>
    private static string Describe(Exception ex, string path) => ex switch
    {
        IOException or UnauthorizedAccessException =>
            $"'{System.IO.Path.GetFileName(path)}' is in use or read-only. Close whatever is "
            + "playing or editing it and try again.",
        TagLib.UnsupportedFormatException => $"'{System.IO.Path.GetFileName(path)}' is not a "
            + "format Offstream can tag.",
        _ => $"'{System.IO.Path.GetFileName(path)}' could not be read; the file may be damaged.",
    };

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Whether the performer list still agrees with the artist box, and so survives the save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An artist tag can hold more than one value, and the page shows one box. Writing
    /// <c>[track.Artist]</c> unconditionally therefore threw the rest away: a file recorded as
    /// <c>AC/DC</c> is stored as the two values <c>AC</c> and <c>DC</c> — ID3v2.3 treats the
    /// slash as its separator — so the box read "AC" and Save narrowed the tag to "AC" on a page
    /// whose whole purpose is repairing tags.
    /// </para>
    /// <para>
    /// The first element is what the box was filled from, so it agreeing means nobody has typed
    /// over it and the original list can go back verbatim. It disagreeing means the user or a
    /// match replaced the artist, and their one value is what should be written. A Spotify match
    /// satisfies this by construction — the mapper sets the artist from the first performer it
    /// assigns — so a match still writes its full performer list, exactly as the recorder does.
    /// </para>
    /// <para>
    /// Deliberately not "split the box on commas". That reads <c>Earth, Wind &amp; Fire</c> as
    /// three artists, which is a worse corruption than the one being fixed.
    /// </para>
    /// </remarks>
    private static bool KeepsEveryPerformer(Track track) =>
        track.Performers is { Length: > 0 } performers
        && string.Equals(performers[0], track.Artist, StringComparison.Ordinal);

    /// <summary>TagLib# reports an absent number as zero, which is not a track number.</summary>
    private static int? PositiveOrNull(uint value) => value > 0 ? (int)value : null;
}
