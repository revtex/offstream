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

            // Only fields the user can actually see and correct on the page. Writing everything
            // Track can hold would let a provider's idea of, say, disc number silently replace a
            // value the user curated, on a page that never showed it to them.
            if (!string.IsNullOrWhiteSpace(track.Title)) tag.Title = track.Title;
            if (!string.IsNullOrWhiteSpace(track.Album)) tag.Album = track.Album;
            if (track.Year is > 0) tag.Year = (uint)track.Year.Value;
            if (track.Genres is { Length: > 0 }) tag.Genres = track.Genres;

            if (!string.IsNullOrWhiteSpace(track.Artist))
            {
                tag.Performers = [track.Artist];

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
}
