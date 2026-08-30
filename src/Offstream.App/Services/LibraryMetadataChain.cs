using System.Net.Http;
using Offstream.Core.Metadata.Library;
using Offstream.Core.Metadata.Providers;
using Offstream.Core.Settings;
using Serilog;

namespace Offstream.App.Services;

/// <summary>Assembles the provider chain the Metadata page looks tracks up with.</summary>
public interface ILibraryMetadataChain
{
    /// <summary>
    /// Builds the chain from whatever is configured right now.
    /// </summary>
    /// <remarks>
    /// Built per run rather than held, so signing in to Spotify or pasting a Last.fm key takes
    /// effect on the next fetch instead of at the next restart.
    /// </remarks>
    FallbackMetadataProvider Create();
}

/// <inheritdoc cref="ILibraryMetadataChain" />
/// <remarks>
/// <para>
/// <b>This ignores the Settings page's provider choice on purpose.</b> That setting answers "who
/// tags a recording as it is made", where picking one source keeps a library internally
/// consistent. Repairing files already on disk is the opposite problem: the user is trying to
/// fill gaps, and refusing to ask Last.fm because the dropdown says Spotify would leave a track
/// untagged that one of their own configured accounts could have identified.
/// </para>
/// <para>
/// Spotify leads when it is available because it carries cover art and a dependable album; Last.fm
/// covers the long tail Spotify's catalogue does not. Whatever is not configured is simply left
/// out, so an empty chain means "nothing is set up" — which the page says plainly rather than
/// reporting every file as unmatched.
/// </para>
/// </remarks>
public sealed class LibraryMetadataChain(
    SettingsDocument settingsDocument,
    ISpotifyAccount spotifyAccount,
    IHttpClientFactory httpClientFactory) : ILibraryMetadataChain
{
    /// <inheritdoc />
    public FallbackMetadataProvider Create()
    {
        var settings = settingsDocument.Current;
        var httpClient = httpClientFactory.CreateClient(RecordingSessionFactory.MetadataHttpClient);
        var providers = new List<IMetadataProvider>();

        var spotify = CreateSpotify(settings);

        if (spotify is not null) providers.Add(spotify);

        var lastFmKey = settings.Metadata.LastFmApiKey;

        if (!string.IsNullOrWhiteSpace(lastFmKey))
        {
            providers.Add(new LastFmMetadataProvider(httpClient, lastFmKey));
        }

        if (providers.Count == 0)
        {
            Log.Information(
                "The Metadata page has no source configured. Sign in to Spotify or add a Last.fm "
                + "API key on the Settings page.");
        }

        return new FallbackMetadataProvider(providers);
    }

    /// <summary>The search provider, or null when nobody is signed in.</summary>
    /// <remarks>
    /// Deliberately <see cref="SpotifySearchMetadataProvider"/> and not the recording path's
    /// <see cref="SpotifyMetadataProvider"/>: that one reports what the account is playing at this
    /// instant, which for a file on disk is either nothing or — worse — whatever happens to be
    /// playing while the user runs a scan.
    /// </remarks>
    private SpotifySearchMetadataProvider? CreateSpotify(OffstreamSettings settings)
    {
        var client = spotifyAccount.CreateClient(
            settings.Metadata.SpotifyClientId,
            settings.Metadata.SpotifyRefreshToken,
            StoreRotatedRefreshToken);

        return client is null ? null : new SpotifySearchMetadataProvider(client);
    }

    /// <summary>
    /// Persists the refresh token Spotify hands back on renewal.
    /// </summary>
    /// <remarks>
    /// Spotify rotates the refresh token on every renewal, so the replacement has to be stored or
    /// a long-lived install silently stops being able to sign itself in. The recording path does
    /// the same thing for the same reason; this page can equally be the first thing to trigger a
    /// renewal, so it cannot leave the write to whichever ran first.
    /// </remarks>
    private void StoreRotatedRefreshToken(string refreshToken)
    {
        var problem = settingsDocument.Update(current => current with
        {
            Metadata = current.Metadata with { SpotifyRefreshToken = refreshToken },
        });

        if (problem is not null) Log.Warning("Could not store the renewed Spotify token: {Problem}", problem);
    }
}
