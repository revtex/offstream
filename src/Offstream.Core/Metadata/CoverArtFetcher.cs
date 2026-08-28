using System.IO.Abstractions;
using System.Net.Http;
using Serilog;

namespace Offstream.Core.Metadata;

/// <summary>Downloads a track's cover art to a local file the encoder can embed.</summary>
public interface ICoverArtFetcher
{
    /// <summary>
    /// Fetches <see cref="Track.AlbumArtUrl"/> to a temporary file.
    /// </summary>
    /// <returns>
    /// The local path, or null when there is no art to fetch or it could not be fetched. The
    /// caller owns the file and is responsible for deleting it.
    /// </returns>
    Task<string?> FetchAsync(Track track, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches cover art over HTTP into the temp directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a file and not a byte array.</b> ffmpeg embeds art for MP3, FLAC and M4A by taking it
/// as a second input stream, and an input stream is a path. Fetching to disk once and handing
/// the path to both that route and the TagLib# route (Ogg/Opus) keeps one fetch per recording
/// rather than one per container strategy.
/// </para>
/// <para>
/// <b>The extension is carried across from the URL</b> because <see cref="CoverArtWriter"/>
/// derives the MIME type it writes into the picture frame from it. Dropping it would tag every
/// PNG as a JPEG.
/// </para>
/// </remarks>
public sealed class CoverArtFetcher : ICoverArtFetcher
{
    /// <summary>
    /// Refuses anything larger. Cover art is tens of kilobytes; a response this size is a
    /// misconfigured URL or a redirect to something that is not an image, and embedding it would
    /// bloat every file the session writes.
    /// </summary>
    public const int MaximumBytes = 8 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IFileSystem _fileSystem;

    public CoverArtFetcher(HttpClient httpClient, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _httpClient = httpClient;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public async Task<string?> FetchAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (string.IsNullOrWhiteSpace(track.AlbumArtUrl)) return null;

        if (!Uri.TryCreate(track.AlbumArtUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            Log.Debug("Ignoring cover art at an unusable address: {Url}", track.AlbumArtUrl);
            return null;
        }

        try
        {
            var image = await _httpClient.GetByteArrayAsync(uri, cancellationToken);

            if (image.Length == 0) return null;

            if (image.Length > MaximumBytes)
            {
                Log.Warning("Cover art at {Url} is {Size} bytes; not embedding it.", uri, image.Length);
                return null;
            }

            var path = TempFileFor(uri);

            try
            {
                await _fileSystem.File.WriteAllBytesAsync(path, image, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The path is about to be lost with the exception, so nothing downstream can ever
                // delete what was written of it. Cancellation reaches here whenever the recording
                // this art belongs to is discarded mid-fetch, which is common enough at a track
                // boundary to be worth not leaving half an image in the temp directory each time.
                TryDelete(path);
                throw;
            }

            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            // Art is the one part of a recording that is worth nothing on its own. Losing it must
            // never cost the audio.
            Log.Warning(ex, "Cover art could not be fetched from {Url}.", uri);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning("Cover art at {Url} did not arrive in time.", uri);
            return null;
        }
    }

    /// <summary>Removes a file this fetch had started writing, if it got that far.</summary>
    private void TryDelete(string path)
    {
        try
        {
            if (_fileSystem.File.Exists(path)) _fileSystem.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temp file is not worth failing a cancellation over.
        }
    }

    /// <summary>A scratch path in the temp directory, keeping the URL's image extension.</summary>
    private string TempFileFor(Uri uri)
    {
        var extension = _fileSystem.Path.GetExtension(uri.AbsolutePath);

        // Anything that is not a plain image extension is treated as JPEG, which is what every
        // provider actually serves; CoverArtWriter makes the same assumption.
        if (extension is not (".jpg" or ".jpeg" or ".png")) extension = ".jpg";

        var name = _fileSystem.Path.GetRandomFileName();

        return _fileSystem.Path.Combine(
            _fileSystem.Path.GetTempPath(),
            $"{_fileSystem.Path.GetFileNameWithoutExtension(name)}.offstream-cover{extension}");
    }
}
