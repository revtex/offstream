namespace Offstream.Core.Metadata;

/// <summary>Embedding cover art failed after the audio was already encoded.</summary>
public sealed class CoverArtException : Exception
{
    public CoverArtException()
    {
    }

    public CoverArtException(string message) : base(message)
    {
    }

    public CoverArtException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Writes a front-cover picture into an already-encoded file, for the one container ffmpeg
/// cannot be trusted with.
/// </summary>
/// <remarks>
/// <para>
/// This is all that remains of the reference implementation's <c>MapperID3</c>. Every textual
/// tag now goes in during the encode as <c>-metadata</c> arguments, and MP3, FLAC and M4A take
/// their picture as a second ffmpeg input stream (<see cref="Encoding.CoverArtSupport.AttachedPicture"/>).
/// Ogg/Opus is the exception: ffmpeg's <c>METADATA_BLOCK_PICTURE</c> support for that container
/// is weak, and TagLib# writes it correctly — plan §5.2, which says to keep TagLib# for
/// precisely the containers that need it and no others.
/// </para>
/// <para>
/// The narrow surface is deliberate. A general-purpose tag writer here would duplicate what
/// ffmpeg already does and give two code paths that could disagree about the same file.
/// </para>
/// </remarks>
public static class CoverArtWriter
{
    /// <summary>Embeds <paramref name="imagePath"/> into <paramref name="audioFilePath"/> as the front cover.</summary>
    /// <exception cref="ArgumentException">Either path is blank.</exception>
    /// <exception cref="FileNotFoundException">Either file is missing.</exception>
    /// <exception cref="CoverArtException">The file could not be tagged.</exception>
    public static void Write(string audioFilePath, string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        if (!File.Exists(audioFilePath)) throw new FileNotFoundException("Encoded file not found.", audioFilePath);
        if (!File.Exists(imagePath)) throw new FileNotFoundException("Cover art file not found.", imagePath);

        Write(audioFilePath, File.ReadAllBytes(imagePath), MimeTypeFor(imagePath));
    }

    /// <summary>Embeds an in-memory image, for art fetched from a metadata provider.</summary>
    /// <exception cref="CoverArtException">The file could not be tagged.</exception>
    public static void Write(string audioFilePath, byte[] image, string mimeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentNullException.ThrowIfNull(image);

        if (image.Length == 0) throw new ArgumentException("Cover art is empty.", nameof(image));

        try
        {
            using var file = TagLib.File.Create(audioFilePath);

            file.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(image))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = mimeType,
                    Description = "Cover",
                },
            ];

            file.Save();
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException
                                       or TagLib.UnsupportedFormatException
                                       or IOException
                                       or UnauthorizedAccessException)
        {
            // The audio itself is fine; only the picture is missing. Callers downgrade this to
            // a warning rather than losing a finished recording over album art.
            throw new CoverArtException($"Could not embed cover art in '{audioFilePath}'.", ex);
        }
    }

    /// <summary>
    /// The MIME type players expect in the picture frame. Only the two formats cover art
    /// actually arrives in are recognised; anything else is passed off as JPEG, which is what
    /// every provider returns in practice.
    /// </summary>
    public static string MimeTypeFor(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        return Path.GetExtension(imagePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            _ => "image/jpeg",
        };
    }
}
