using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using Serilog;

namespace Offstream.Core.Metadata.Providers;

/// <summary>
/// Looks a track up on Last.fm's 2.0 API and maps the response onto it.
/// </summary>
/// <remarks>
/// <para>
/// The HTTP half of the reference implementation's <c>LastFMAPI</c>; the mapping half is
/// <see cref="LastFmTrackMapper"/>, which stays pure and fixture-tested. Two behaviours from the
/// original are load-bearing and are reproduced exactly:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Retry with a simplified title.</b> Spotify titles carry decorations Last.fm's catalogue
/// does not — "(Remastered 2011)", " - Live at Wembley" — and a first lookup for the decorated
/// title usually returns a track with no album. Stripping the decoration and asking again is
/// what makes the common case work at all.
/// </item>
/// <item>
/// <b>The single-album fallback.</b> A track whose album Last.fm attributes to "Various Artists"
/// — or reports not at all — is looked up again through <c>album.getInfo</c>, which is where a
/// single's own artwork and title live.
/// </item>
/// </list>
/// <para>
/// <b>Changed from the reference:</b> requests go over HTTPS through an injected
/// <see cref="HttpClient"/> rather than <c>XmlDocument.Load(url)</c> over plain HTTP, which
/// fetched and parsed in one blocking call with no timeout and no way to test it. The API key is
/// the user's own (plan §6 settings) rather than one of three keys hard-coded in the source.
/// </para>
/// <para>
/// <b>The parser is locked down</b> — no DTD processing, no external entity resolution — because
/// this is unauthenticated XML from the network being handed to <see cref="XmlSerializer"/>.
/// </para>
/// </remarks>
public sealed partial class LastFmMetadataProvider : IMetadataProvider
{
    /// <summary>Last.fm's API root. HTTPS; the reference used plain HTTP.</summary>
    public static readonly Uri ApiRoot = new("https://ws.audioscrobbler.com/2.0/");

    private static readonly XmlSerializer NodeSerializer = new(typeof(LastFmNode));

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CloseInput = true,
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    /// <param name="httpClient">Used for every request; the caller owns its lifetime and timeout.</param>
    /// <param name="apiKey">The user's Last.fm API key, from settings.</param>
    public LastFmMetadataProvider(HttpClient httpClient, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _httpClient = httpClient;
        _apiKey = apiKey;
    }

    /// <inheritdoc />
    public MetadataProvider Kind => MetadataProvider.LastFm;

    /// <summary>
    /// Strips the decorations Spotify adds to a title but Last.fm's catalogue does not carry:
    /// a trailing parenthesised qualifier, or everything from a dash onwards.
    /// </summary>
    [GeneratedRegex(@" \(.*?\)| \- .*")]
    private static partial Regex TitleDecoration { get; }

    /// <inheritdoc />
    public async Task<bool> EnrichAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title)) return false;

        return await EnrichAsync(track, forcedTitle: null, cancellationToken);
    }

    private async Task<bool> EnrichAsync(Track track, string? forcedTitle, CancellationToken cancellationToken)
    {
        var node = await FetchAsync(
            TrackInfoUri(track.Artist!, forcedTitle ?? track.Title!), cancellationToken);

        if (node?.Track is not { } response) return false;

        await FallbackToSingleAlbumAsync(response, cancellationToken);

        if (response.Album is not null)
        {
            LastFmTrackMapper.Apply(track, response);
            return true;
        }

        // No album on the decorated title. Ask again for the bare one — but only if that is a
        // different question. The reference compared the stripped title against the one already
        // forced, which is null on the first attempt, so an undecorated title always produced a
        // second identical request. Comparing against what was actually asked skips it.
        var simplified = TitleDecoration.Replace(track.Title!, string.Empty);

        return simplified != (forcedTitle ?? track.Title)
               && !string.IsNullOrWhiteSpace(simplified)
               && await EnrichAsync(track, simplified, cancellationToken);
    }

    /// <summary>
    /// Looks a single up on its own when the track response has no usable album.
    /// </summary>
    /// <remarks>
    /// "Various Artists" is treated as no album at all, exactly as the reference did: it is what
    /// Last.fm attributes a compilation appearance to, and tagging a single with it is worse than
    /// tagging it with the single's own name.
    /// </remarks>
    private async Task FallbackToSingleAlbumAsync(LastFmTrack response, CancellationToken cancellationToken)
    {
        if (response.Album is not null
            && !string.Equals(response.Album.Artist, "Various Artists", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var artist = response.Artist?.Name;
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(response.Name)) return;

        var node = await FetchAsync(AlbumInfoUri(artist, response.Name), cancellationToken);
        if (node?.Album is not { } album) return;

        response.Album = album.ToTrackAlbum();
    }

    private Uri TrackInfoUri(string artist, string title) => BuildUri(
        "track.getInfo",
        ("artist", artist),
        ("track", title));

    private Uri AlbumInfoUri(string artist, string album) => BuildUri(
        "album.getInfo",
        ("artist", artist),
        ("album", album));

    private Uri BuildUri(string method, params (string Key, string Value)[] parameters)
    {
        var query = string.Join(
            '&',
            parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        return new Uri(
            ApiRoot,
            string.Create(
                CultureInfo.InvariantCulture,
                $"?method={method}&api_key={Uri.EscapeDataString(_apiKey)}&{query}"));
    }

    /// <summary>
    /// Fetches and deserializes one response, or returns null for every way that can fail.
    /// </summary>
    /// <remarks>
    /// Network and parse failures are swallowed rather than thrown, and that is deliberate: a
    /// Last.fm outage, a rate-limit, or a response shape that changed under us must cost the user
    /// their tags, never their recording. The failure is logged once per call so it is visible in
    /// the activity log without burying it.
    /// </remarks>
    private async Task<LastFmNode?> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await _httpClient.GetStreamAsync(uri, cancellationToken);
            using var reader = XmlReader.Create(stream, ReaderSettings);

            if (NodeSerializer.Deserialize(reader) is not LastFmNode node) return null;

            if (node.Status == LastFmNodeStatus.Ok) return node;

            if (node.Error is { } error)
            {
                Log.Debug(
                    "Last.fm declined the lookup: {Code} {Message}",
                    error.Code,
                    error.Message);
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or XmlException)
        {
            Log.Warning(ex, "Last.fm lookup failed.");
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The HttpClient's own timeout, which surfaces as a cancellation with no token behind
            // it. Distinguished from a real cancellation so a slow Last.fm does not look like a
            // stopped session.
            Log.Warning("Last.fm did not answer in time.");
            return null;
        }
    }
}
