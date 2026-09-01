using Serilog;

namespace Offstream.Core.Metadata.Library;

/// <summary>What happened when one file was saved.</summary>
/// <param name="Saved">Whether the file on disk now carries the suggested tags.</param>
/// <param name="FailureReason">Why not, in words fit to put in a table cell.</param>
public readonly record struct LibraryWriteResult(bool Saved, string? FailureReason)
{
    /// <summary>It worked.</summary>
    public static LibraryWriteResult Success => new(Saved: true, FailureReason: null);

    /// <summary>It did not, and here is what to tell the user.</summary>
    public static LibraryWriteResult Failed(string reason) => new(Saved: false, reason);
}

/// <summary>Commits a reviewed <see cref="LibraryTrack"/> to the file it came from.</summary>
public interface ILibraryTagWriter
{
    /// <summary>Writes <paramref name="track"/>'s suggested tags into its file.</summary>
    /// <remarks>
    /// Never throws for a problem with the file. A locked file, a read-only file and a damaged
    /// one are all ordinary outcomes of pointing this at a real folder, and one of them must not
    /// end a run over the other two hundred.
    /// </remarks>
    Task<LibraryWriteResult> SaveAsync(LibraryTrack track, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ILibraryTagWriter" />
public sealed class LibraryTagWriter(ILibraryTagStore tagStore, ICoverArtFetcher coverArtFetcher)
    : ILibraryTagWriter
{
    /// <inheritdoc />
    public async Task<LibraryWriteResult> SaveAsync(
        LibraryTrack track,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        // Saving a row nothing changed would rewrite the file for no benefit, and a rewrite still
        // moves the modified time — enough to make a sync client re-upload a folder that did not
        // actually change.
        if (!track.HasChanges) return LibraryWriteResult.Success;

        var coverArt = await ResolveCoverArtAsync(track, cancellationToken);

        try
        {
            tagStore.Write(track.Path, track.Suggested, coverArt);

            Log.Information("Wrote tags to {Path}.", track.Path);

            return LibraryWriteResult.Success;
        }
        catch (LibraryTagException ex)
        {
            Log.Warning(ex, "Could not write tags to {Path}.", track.Path);

            return LibraryWriteResult.Failed(ex.Message);
        }
    }

    /// <summary>The picture to embed, downloading it only if a provider offered a URL.</summary>
    /// <remarks>
    /// A failed art fetch is never a failed save. The tags are what the user asked for and are
    /// worth having on their own; losing them because an image host was briefly unreachable would
    /// be the wrong trade, and the row would report a failure for a file whose text was fine.
    /// </remarks>
    private async Task<byte[]?> ResolveCoverArtAsync(LibraryTrack track, CancellationToken cancellationToken)
    {
        if (track.Suggested.AlbumArtImage is { Length: > 0 } embedded) return embedded;
        if (string.IsNullOrWhiteSpace(track.Suggested.AlbumArtUrl)) return null;

        string? downloaded = null;

        try
        {
            downloaded = await coverArtFetcher.FetchAsync(track.Suggested, cancellationToken);

            return downloaded is null ? null : await File.ReadAllBytesAsync(downloaded, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            Log.Warning(ex, "Could not fetch cover art for {Path}; saving tags without it.", track.Path);

            return null;
        }
        finally
        {
            // The fetcher's contract puts the temporary file in our hands to delete.
            if (downloaded is not null) TryDelete(downloaded);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Could not delete temporary cover art {Path}.", path);
        }
    }
}
