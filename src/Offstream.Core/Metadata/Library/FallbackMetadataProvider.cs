using Offstream.Core.Metadata.Providers;
using Serilog;

namespace Offstream.Core.Metadata.Library;

/// <summary>Tries each provider in turn and keeps the first answer.</summary>
/// <remarks>
/// <para>
/// <b>Order is preference, not fallback-on-error alone.</b> Spotify goes first because it carries
/// the cover art and a reliable album, and Last.fm answers for the long tail Spotify's catalogue
/// does not have. The first provider to return <c>true</c> wins outright — there is no merging of
/// two providers' answers, because a track assembled from two catalogues can end up with one
/// service's album and another's year, which is worse than either on its own and impossible to
/// explain when the user asks where a wrong tag came from.
/// </para>
/// <para>
/// <b>An error is not a "no".</b> If every provider failed outright, that is reported rather than
/// quietly reading as "this track is not in any catalogue" — the two look identical on the page
/// and want opposite responses from the user. But a provider that *threw* never stops the next
/// one from being asked: an expired Spotify token should not make a configured Last.fm useless.
/// </para>
/// </remarks>
public sealed class FallbackMetadataProvider : IMetadataProvider
{
    private readonly IReadOnlyList<IMetadataProvider> _providers;

    /// <summary>Creates a chain over <paramref name="providers"/>, most-preferred first.</summary>
    /// <remarks>
    /// Providers that are not configured are expected to be left out by the caller rather than
    /// passed as <see cref="NoMetadataProvider"/>, so that "nothing was configured" and "nothing
    /// matched" stay distinguishable — an empty chain reports the former.
    /// </remarks>
    public FallbackMetadataProvider(params IReadOnlyList<IMetadataProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = [.. providers.Where(provider => provider is not null and not NoMetadataProvider)];
    }

    /// <summary>Whether any provider at all is configured.</summary>
    public bool IsEmpty => _providers.Count == 0;

    /// <inheritdoc />
    /// <remarks>
    /// Reports the chain's most-preferred member rather than whichever one answered. The kind is
    /// only used for logging, and the per-track log line already names the provider that matched.
    /// </remarks>
    public MetadataProvider Kind => _providers.Count > 0 ? _providers[0].Kind : MetadataProvider.None;

    /// <inheritdoc />
    /// <exception cref="MetadataLookupException">
    /// Nothing matched and at least one provider failed outright. The message is the first
    /// failure's, because that is the most-preferred provider and the one worth fixing.
    /// </exception>
    public async Task<bool> EnrichAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        MetadataLookupException? firstFailure = null;

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await provider.EnrichAsync(track, cancellationToken))
                {
                    Log.Information("{Provider} matched {Artist} - {Title}.", provider.Kind, track.Artist, track.Title);

                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                // The user stopped the run. Not a provider failure and not something to fall back
                // from — the next provider would only be cancelled too.
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "{Provider} failed; trying the next source.", provider.Kind);

                firstFailure ??= ex as MetadataLookupException
                    ?? new MetadataLookupException($"{provider.Kind} could not be reached.", ex);
            }
        }

        // Nothing matched. If that is because every source was broken, say so — "not found" would
        // send the user looking for a track that is in the catalogue all along.
        if (firstFailure is not null) throw firstFailure;

        return false;
    }
}
