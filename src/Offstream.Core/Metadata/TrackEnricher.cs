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
    /// <remarks>
    /// Raised from 20s once the Spotify provider learned to wait out an advertisement rather than
    /// counting it as a failed match. A free account's break between tracks runs longer than 20s,
    /// so the old ceiling cut the wait off before the track it was waiting for could start, and
    /// the recording was tagged with nothing. What the deadline is actually for — not letting a
    /// provider that has stopped answering hold a finished recording — is served just as well at
    /// 45s, because this runs concurrently with a recording from the moment the track starts.
    /// </remarks>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(45);

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

            var genreSource = await ApplyGenreFallbackAsync(track, deadline.Token);

            var coverArtPath = await _coverArt.FetchAsync(track, deadline.Token);

            // Genre is on this line because it is the one tag whose source is not obvious from
            // the outside: it may have come from the provider named here or from the fallback
            // below, and until it was printed the only way to know it had been written at all was
            // to run ffprobe over the finished file.
            Log.Information(
                "{Provider} tagged {Track}: album {Album}, track {Position}, genre {Genre}.",
                _provider.Kind,
                track,
                track.Album ?? "unknown",
                track.AlbumPosition?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
                DescribeGenres(track.Genres, genreSource));

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
            // The caller gave up on this lookup: the session is stopping, or the recording it
            // belongs to has been discarded and there is nothing left to tag. Neither is worth a
            // line of its own — and the second must not print "had no metadata", which would
            // report a missing tag on a file that was never written.
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
    /// The genre tag as the activity log prints it, naming the source when it was not the
    /// provider doing the tagging.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>"none"</c> rather than the <c>"unknown"</c> the album and position use, because it is a
    /// different answer: those two are missing from a reply that was received, while an empty
    /// genre means every source in the chain was asked and none had one. Worth distinguishing on
    /// the line where someone is trying to work out whether tagging is working.
    /// </para>
    /// <para>
    /// The source is a suffix on the value rather than a line of its own. It is only interesting
    /// when it is *not* the provider already named at the start of the message — which is exactly
    /// when it says something worth knowing, that the primary had no genre for this artist — and
    /// a second line per track would be noise on every other track to carry it.
    /// </para>
    /// </remarks>
    internal static string DescribeGenres(string[]? genres, MetadataProvider? source = null)
    {
        if (genres is not { Length: > 0 }) return "none";

        var joined = string.Join(", ", genres);

        return source is { } kind ? $"{joined} ({kind})" : joined;
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
    /// <returns>
    /// The provider the genre came from when the fallback supplied it, or null when it did not —
    /// which the caller prints, rather than logging a second line of its own.
    /// </returns>
    private async Task<MetadataProvider?> ApplyGenreFallbackAsync(
        Track track,
        CancellationToken cancellationToken)
    {
        if (track.Genres is { Length: > 0 }) return null;

        // Said out loud, because "no genre" and "nowhere to ask" look identical in the tag and
        // used to look identical in the log too.
        if (_genreFallback is null)
        {
            Log.Debug(
                "{Provider} had no genre for {Track} and no fallback is configured. "
                + "Setting a Last.fm API key on the Settings page would give it a second source.",
                _provider.Kind,
                track);

            return null;
        }

        try
        {
            var genres = await _genreFallback.GetGenresAsync(track, cancellationToken);

            if (genres.Length == 0)
            {
                Log.Debug(
                    "{Fallback} had no genre for {Track} either, so the tag is left empty.",
                    _genreFallback.Kind,
                    track);

                return null;
            }

            track.Genres = genres;

            return _genreFallback.Kind;
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

        return null;
    }
}
