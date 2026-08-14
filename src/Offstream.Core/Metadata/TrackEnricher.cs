using Offstream.Core.Metadata.Providers;
using Serilog;

namespace Offstream.Core.Metadata;

/// <summary>What one track's enrichment produced.</summary>
/// <param name="Updated">Whether the provider wrote anything onto the track.</param>
/// <param name="CoverArtPath">
/// A temporary image file to embed, or null. The caller owns it and must delete it once the
/// encode has finished with it.
/// </param>
public readonly record struct TrackEnrichment(bool Updated, string? CoverArtPath)
{
    /// <summary>Nothing was found, and nothing was fetched.</summary>
    public static TrackEnrichment None => new(Updated: false, CoverArtPath: null);
}

/// <summary>Runs the selected metadata provider over a track and fetches its cover art.</summary>
public interface ITrackEnricher
{
    /// <summary>Enriches <paramref name="track"/> in place and fetches its art.</summary>
    /// <remarks>Never throws: every failure is reported as <see cref="TrackEnrichment.None"/>.</remarks>
    Task<TrackEnrichment> EnrichAsync(Track track, CancellationToken cancellationToken = default);
}

/// <summary>
/// The orchestration the reference implementation kept inside each API class: look the track up,
/// then fetch its art, under a deadline, never failing the recording.
/// </summary>
/// <remarks>
/// <para>
/// <b>The deadline is the point.</b> Enrichment runs concurrently with the recording it belongs
/// to, so in the normal case it costs nothing — a lookup takes under a second and the track plays
/// for minutes. What it must never do is hold the finished recording hostage to a provider that
/// has stopped answering, so the whole thing is bounded and a timeout is simply "no metadata".
/// </para>
/// <para>
/// <b>Nothing here throws.</b> Tags are worth having; they are not worth a lost recording. Every
/// failure is logged and downgraded, which is also why the pipeline can treat "no provider
/// configured" and "the provider found nothing" identically.
/// </para>
/// </remarks>
public sealed class TrackEnricher : ITrackEnricher
{
    /// <summary>How long a lookup and its art fetch get, together.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(20);

    private readonly IMetadataProvider _provider;
    private readonly ICoverArtFetcher _coverArt;
    private readonly TimeSpan _deadline;
    private readonly IGenreFallback? _genreFallback;

    public TrackEnricher(
        IMetadataProvider provider,
        ICoverArtFetcher coverArt,
        TimeSpan? deadline = null,
        IGenreFallback? genreFallback = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(coverArt);

        _provider = provider;
        _coverArt = coverArt;
        _deadline = deadline ?? DefaultDeadline;
        _genreFallback = genreFallback;
    }

    /// <inheritdoc />
    public async Task<TrackEnrichment> EnrichAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (_provider.Kind == MetadataProvider.None) return TrackEnrichment.None;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline);

        try
        {
            var updated = await _provider.EnrichAsync(track, deadline.Token);

            track.MetadataUpdated = updated;

            if (!updated)
            {
                Log.Information("{Provider} had no metadata for {Track}.", _provider.Kind, track);
                return TrackEnrichment.None;
            }

            await ApplyGenreFallbackAsync(track, deadline.Token);

            var coverArtPath = await _coverArt.FetchAsync(track, deadline.Token);

            Log.Information(
                "{Provider} tagged {Track}: album {Album}, track {Position}.",
                _provider.Kind,
                track,
                track.Album ?? "unknown",
                track.AlbumPosition?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");

            return new TrackEnrichment(Updated: true, coverArtPath);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning(
                "{Provider} did not answer within {Seconds:F0}s for {Track}; recording it untagged.",
                _provider.Kind,
                _deadline.TotalSeconds,
                track);

            return TrackEnrichment.None;
        }
        catch (OperationCanceledException)
        {
            // The session is stopping. Not worth a line of its own.
            return TrackEnrichment.None;
        }
#pragma warning disable CA1031 // A provider fault must not reach the recording; that is this class's job.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Warning(ex, "{Provider} failed for {Track}; recording it untagged.", _provider.Kind, track);
            return TrackEnrichment.None;
        }
    }

    /// <summary>
    /// Fills the genre tag from a second source when the chosen provider left it empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only on the success path, and only into a gap. A provider that found nothing at all
    /// produces an untagged recording, and a lone genre on an otherwise bare file is not worth a
    /// second request; a provider that did supply genres is not second-guessed.
    /// </para>
    /// <para>
    /// A failure here is swallowed rather than downgrading the enrichment. Everything else on the
    /// track is already correct at this point, and losing an album because a genre lookup timed
    /// out would be the tail wagging the dog.
    /// </para>
    /// </remarks>
    private async Task ApplyGenreFallbackAsync(Track track, CancellationToken cancellationToken)
    {
        if (_genreFallback is null || track.Genres is { Length: > 0 }) return;

        try
        {
            var genres = await _genreFallback.GetGenresAsync(track, cancellationToken);

            if (genres.Length == 0) return;

            track.Genres = genres;

            Log.Debug("Genre for {Track} came from the fallback source: {Genres}.", track, genres);
        }
        catch (OperationCanceledException)
        {
            // The deadline or the session; either way the rest of the tags stand.
        }
#pragma warning disable CA1031 // A genre is never worth failing an otherwise good enrichment.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Debug(ex, "The genre fallback failed for {Track}; leaving the tag empty.", track);
        }
    }
}
