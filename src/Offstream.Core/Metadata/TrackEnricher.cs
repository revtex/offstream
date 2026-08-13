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

    public TrackEnricher(IMetadataProvider provider, ICoverArtFetcher coverArt, TimeSpan? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(coverArt);

        _provider = provider;
        _coverArt = coverArt;
        _deadline = deadline ?? DefaultDeadline;
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
}
