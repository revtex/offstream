using System.IO.Abstractions;
using System.Net.Http;
using Offstream.Core;
using Offstream.Core.Audio;
using Offstream.Core.Diagnostics;
using Offstream.Core.Encoding;
using Offstream.Core.Interop;
using Offstream.Core.Metadata;
using Offstream.Core.Metadata.Providers;
using Offstream.Core.Recording;
using Offstream.Core.Settings;
using Offstream.Core.Spotify;
using Offstream.Core.Spotify.Smtc;
using Serilog;

namespace Offstream.App.Services;

/// <summary>
/// Builds a configured <see cref="RecordingSession"/>.
/// </summary>
/// <remarks>
/// The seam that keeps <see cref="RecordingController"/> testable. Everything on the other side
/// of it needs a render endpoint, a running Spotify and an ffmpeg binary; everything on this
/// side is state machinery — start, stop, what to show when starting fails — which is the part
/// that can actually be wrong in a way a user notices.
/// </remarks>
public interface IRecordingSessionFactory
{
    /// <summary>Builds a session for one run. Sessions are not restartable, so this is per-start.</summary>
    /// <exception cref="FFmpegNotFoundException">No usable ffmpeg was found.</exception>
    RecordingSession Create(OffstreamSettings settings, IProgress<RecordingProgress> progress);
}

/// <summary>Builds the real thing: WASAPI loopback, window-title detection, ffmpeg.</summary>
public sealed class RecordingSessionFactory(
    IFileSystem fileSystem,
    IProcessManager processManager,
    IHttpClientFactory httpClientFactory,
    ISpotifyAccount spotifyAccount,
    SettingsDocument settingsDocument)
    : IRecordingSessionFactory
{
    /// <summary>
    /// The named client metadata lookups and cover-art fetches share.
    /// </summary>
    /// <remarks>
    /// Named rather than default so it can carry a short timeout of its own: a provider that has
    /// stopped answering must not hold a finished recording for the default hundred seconds.
    /// </remarks>
    public const string MetadataHttpClient = "Offstream.Metadata";

    /// <summary>How long a single metadata or cover-art request gets.</summary>
    public static readonly TimeSpan MetadataRequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    private readonly IProcessManager _processManager =
        processManager ?? throw new ArgumentNullException(nameof(processManager));

    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

    private readonly ISpotifyAccount _spotifyAccount =
        spotifyAccount ?? throw new ArgumentNullException(nameof(spotifyAccount));

    private readonly SettingsDocument _settingsDocument =
        settingsDocument ?? throw new ArgumentNullException(nameof(settingsDocument));

    /// <inheritdoc />
    public RecordingSession Create(OffstreamSettings settings, IProgress<RecordingProgress> progress)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(progress);

        // The watcher ends the capture when its endpoint disappears mid-recording. Without it the
        // loss is silent: WASAPI stops delivering, the session keeps believing it is recording,
        // and the file that lands is whatever arrived before the device went away.
        var capture = new LoopbackAudioCapture(
            settings.Recording.AudioEndpointDeviceId,
            endpoints: new AudioEndpointWatcher());

        // SMTC first, window title second. The title is only readable while Spotify has a window,
        // so on its own it stops detecting the moment the user minimises to the tray; the media
        // session survives that, and hands over separate artist and title fields rather than one
        // string to split. See PreferredTrackSource for what "prefers" means and when it hands back.
        var detector = new PreferredTrackSource(
            new SmtcTrackSource(new WindowsSmtcSessions()),
            new SpotifyTrackDetector(_processManager, new SpotifyPlaybackProbe(_processManager)));

        return new RecordingSession(
            capture,
            new SpotifyPoller(detector),
            settings.ToRecordingSettings(),
            CreateEncoder(settings),
            _fileSystem,
            CreateEnricher(settings),
            progress);
    }

    /// <summary>
    /// Builds the metadata lookup for this session, from the provider the user chose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A provider that is selected but not usable — Last.fm with no API key, Spotify with nobody
    /// signed in — degrades to no enrichment and says so, rather than refusing to record. Tags
    /// are worth having; they are not worth the session.
    /// </para>
    /// <para>
    /// Built per session, like the encoder and for the same reason: a user who signs in to
    /// Spotify or pastes an API key while the app is open gets a working session on the next
    /// start rather than after a restart.
    /// </para>
    /// </remarks>
    private TrackEnricher CreateEnricher(OffstreamSettings settings)
    {
        var httpClient = _httpClientFactory.CreateClient(MetadataHttpClient);

        return new TrackEnricher(
            CreateProvider(settings, httpClient),
            new CoverArtFetcher(httpClient, _fileSystem),
            deadline: null,
            CreateGenreFallback(settings, httpClient));
    }

    /// <summary>
    /// The second source for genre, or null when there is nothing to fall back to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only for Spotify, and only when a Last.fm key is already configured. Spotify has no genre
    /// on a track at any endpoint — it models genre as an attribute of the artist — so the best
    /// it can say about a track is what its performer is generally known for, and for a good part
    /// of the catalogue it says nothing at all. Last.fm's per-track tags answer the question that
    /// was actually asked, so a user who has both configured gets Spotify's albums and Last.fm's
    /// genres rather than having to pick.
    /// </para>
    /// <para>
    /// Not offered the other way round: when Last.fm is the chosen provider it has already
    /// supplied its own tags, and Spotify's artist-level answer would be strictly worse. Nor is
    /// it offered without a key — that is the same "nothing will be tagged" case the provider
    /// switch warns about, and warning twice for one missing key helps nobody.
    /// </para>
    /// </remarks>
    private static LastFmGenreFallback? CreateGenreFallback(OffstreamSettings settings, HttpClient httpClient)
    {
        if (settings.Metadata.Provider != MetadataProvider.Spotify) return null;

        var apiKey = settings.Metadata.LastFmApiKey;

        return string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new LastFmGenreFallback(new LastFmMetadataProvider(httpClient, apiKey));
    }

    private IMetadataProvider CreateProvider(OffstreamSettings settings, HttpClient httpClient)
    {
        switch (settings.Metadata.Provider)
        {
            case MetadataProvider.LastFm:
                var apiKey = settings.Metadata.LastFmApiKey;

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    Log.Warning(
                        "Last.fm is selected but no API key is set, so nothing will be tagged. "
                        + "Add one on the Settings page.");

                    return new NoMetadataProvider();
                }

                return new LastFmMetadataProvider(httpClient, apiKey);

            case MetadataProvider.Spotify:
                var client = _spotifyAccount.CreateClient(
                    settings.Metadata.SpotifyClientId,
                    settings.Metadata.SpotifyRefreshToken,
                    StoreRotatedRefreshToken);

                if (client is null)
                {
                    Log.Warning(
                        "Spotify is selected but nobody is signed in, so nothing will be tagged. "
                        + "Sign in on the Settings page.");

                    return new NoMetadataProvider();
                }

                var provider = new SpotifyMetadataProvider(client);

                provider.AuthorizationExpired += (_, _) => ClearExpiredRefreshToken();

                return provider;

            case MetadataProvider.None:
            default:
                return new NoMetadataProvider();
        }
    }

    /// <summary>
    /// Drops a refresh token Spotify has stopped honouring, so the user is asked to sign in again.
    /// </summary>
    /// <remarks>
    /// A refresh token dies when it is revoked from the user's Spotify account page, when the
    /// dashboard app is deleted, or when the granted scopes no longer cover what is asked for.
    /// None of those recover on their own, so keeping the dead token would mean retrying it on
    /// every track forever while the Settings page went on claiming the account was connected.
    /// Clearing it raises <c>SettingsDocument.Changed</c>, which is what turns that page back to
    /// its signed-out state and puts the sign-in button in front of the user.
    /// </remarks>
    private void ClearExpiredRefreshToken()
    {
        var problem = _settingsDocument.Update(current => current with
        {
            Metadata = current.Metadata with { SpotifyRefreshToken = null },
        });

        if (problem is not null)
        {
            Log.Warning("The expired Spotify token could not be cleared: {Problem}", problem);
        }
    }

    /// <summary>Persists the replacement Spotify hands back every time it renews a token.</summary>
    private void StoreRotatedRefreshToken(string refreshToken)
    {
        var problem = _settingsDocument.Update(current => current with
        {
            Metadata = current.Metadata with { SpotifyRefreshToken = refreshToken },
        });

        if (problem is not null) Log.Warning("The renewed Spotify token could not be saved: {Problem}", problem);
    }

    /// <summary>
    /// Resolves ffmpeg once per session rather than once per app.
    /// </summary>
    /// <remarks>
    /// A user who installs ffmpeg while Offstream is open, or corrects the configured path on
    /// the settings page, gets a working session on the next start instead of having to restart
    /// the app. Resolution order and why a wrong configured path is an error rather than a
    /// fallback are in <see cref="FFmpegLocator"/>.
    /// </remarks>
    private AudioEncoder CreateEncoder(OffstreamSettings settings)
    {
        var locator = new FFmpegLocator(_fileSystem, AppContext.BaseDirectory);
        var location = locator.Locate(settings.App.FfmpegPath);

        return new AudioEncoder(new FFmpegRunner(location.ExecutablePath));
    }
}
