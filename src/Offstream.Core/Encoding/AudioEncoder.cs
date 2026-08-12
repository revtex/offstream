using Offstream.Core.Metadata;

namespace Offstream.Core.Encoding;

/// <summary>What one completed encode produced.</summary>
/// <param name="Request">The request that was encoded.</param>
/// <param name="CoverArtFailure">
/// Set when the audio encoded cleanly but the picture could not be embedded. This is a warning,
/// not a failure: the recording is on disk and playable, and losing it over album art would be
/// the wrong trade.
/// </param>
public sealed record EncodeOutcome(EncodeRequest Request, CoverArtException? CoverArtFailure = null)
{
    /// <summary>The finished file.</summary>
    public string OutputPath => Request.OutputPath;

    /// <summary>Whether anything is worth telling the user about.</summary>
    public bool HasWarning => CoverArtFailure is not null;
}

/// <summary>Encodes one captured WAV into the user's chosen format.</summary>
public interface IAudioEncoder
{
    /// <summary>Encodes <paramref name="request"/>, embedding cover art if the request carries any.</summary>
    /// <exception cref="FFmpegException">ffmpeg failed; the recording did not complete.</exception>
    Task<EncodeOutcome> EncodeAsync(EncodeRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The encode step end to end: ffmpeg, then the per-container cover-art fallback.
/// </summary>
/// <remarks>
/// <para>
/// Cover art takes one of three routes, chosen by the format profile rather than by a branch
/// here (plan §5.1): MP3, FLAC and M4A get it as a second ffmpeg input in the same pass; Ogg/Opus
/// gets it written afterwards by <see cref="CoverArtWriter"/>, because ffmpeg's
/// <c>METADATA_BLOCK_PICTURE</c> support for that container is unreliable (§5.2); WAV has
/// nowhere to put it and is skipped without complaint.
/// </para>
/// <para>
/// This is the seam the encode queue depends on, which is why it is behind
/// <see cref="IAudioEncoder"/> — the queue's own behaviour is then testable without ffmpeg.
/// </para>
/// </remarks>
public sealed class AudioEncoder(FFmpegRunner runner, TimeSpan? timeout = null) : IAudioEncoder
{
    /// <summary>
    /// Generous, because it is a backstop against a wedged encoder, not a performance budget:
    /// a lossless hour-long recording is still minutes of work on a slow machine.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    /// <inheritdoc />
    public async Task<EncodeOutcome> EncodeAsync(
        EncodeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await runner.RunOrThrowAsync(FFmpegArguments.Build(request), _timeout, cancellationToken);

        var profile = EncodingProfiles.For(request.Format);

        if (request.CoverArtPath is null || profile.CoverArt != CoverArtSupport.PostProcess)
            return new EncodeOutcome(request);

        try
        {
            CoverArtWriter.Write(request.OutputPath, request.CoverArtPath);
            return new EncodeOutcome(request);
        }
        catch (CoverArtException ex)
        {
            return new EncodeOutcome(request, ex);
        }
        catch (FileNotFoundException ex)
        {
            // The art was fetched to a temp file that has since gone; same trade-off.
            return new EncodeOutcome(request, new CoverArtException(ex.Message, ex));
        }
    }
}
