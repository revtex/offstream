using System.Collections.Concurrent;
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

    /// <summary>
    /// Artist to tags, for the life of this provider — which is the life of one session.
    /// </summary>
    /// <remarks>
    /// An artist's tags do not move while a session runs, and recording an album asks about the
    /// same artist once per track. Keyed by name rather than id because Last.fm has no ids;
    /// compared case-insensitively, since it is the name as some source spelled it.
    /// </remarks>
    private readonly ConcurrentDictionary<string, string[]> _artistTags =
        new(StringComparer.OrdinalIgnoreCase);

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

        // Last.fm's album for a track is whichever release its community database happens to
        // associate, and for a well-known track that is regularly a DJ set or a radio show the
        // recording once appeared on rather than the record it came from. Nothing upstream caught
        // that, because this provider had no equivalent of Spotify's DetectedTrackMatch: whatever
        // came back was written. When the media session has already named the album it is not
        // guessing — it is reporting what the client is playing the track *out of* — so a Last.fm
        // release that disagrees is a bad match, and its artwork, its track listing and its
        // credited artists are wrong along with its name.
        if (response.Album is { } reported
            && !DetectedTrackMatch.AlbumAgrees(track.Album, reported.Title))
        {
            Log.Debug(
                "Last.fm puts {Track} on \"{Reported}\", but the client is playing it from "
                + "\"{Detected}\"; ignoring that release and everything hanging off it.",
                track,
                reported.Title,
                track.Album);

            response.Album = null;
        }

        if (response.Album is not null)
        {
            await ApplyAsync(track, response, cancellationToken);

            return true;
        }

        // No album on the decorated title. Ask again for the bare one — but only if that is a
        // different question. The reference compared the stripped title against the one already
        // forced, which is null on the first attempt, so an undecorated title always produced a
        // second identical request. Comparing against what was actually asked skips it.
        var simplified = TitleDecoration.Replace(track.Title!, string.Empty);

        if (simplified != (forcedTitle ?? track.Title)
            && !string.IsNullOrWhiteSpace(simplified)
            && await EnrichAsync(track, simplified, cancellationToken))
        {
            return true;
        }

        // Last.fm knows this recording; it just has no release for it that this track can be
        // tagged with. Tags describe the *recording*, so they survive the release being rejected —
        // and the mapper fills rather than clears, which leaves the media session's album, its
        // position and its album artist standing exactly where they were. Returning false here
        // instead would throw a genre away over an album that was never in doubt.
        await ApplyAsync(track, response, cancellationToken);

        // Genre alone is the whole of what this path can add, so it is also the whole test of
        // whether it added anything. Duration is deliberately not counted: it goes into no tag,
        // and letting it decide would report "tagged" for a track nothing was written onto.
        return track.Genres is { Length: > 0 };
    }

    /// <summary>Maps a response onto a track, then fills the genre gap Last.fm usually leaves.</summary>
    /// <remarks>
    /// The mapper takes genres from the track's own tag cloud, which Last.fm leaves empty for a
    /// great many tracks — so as the chosen provider it would tag album, position and artwork
    /// correctly and then hand back no genre at all. The artist's tags are the same second
    /// question the genre fallback asks, and this is the same answer.
    /// </remarks>
    private async Task ApplyAsync(Track track, LastFmTrack response, CancellationToken cancellationToken)
    {
        LastFmTrackMapper.Apply(track, response);

        if (track.Genres is null or { Length: 0 })
        {
            track.Genres = await ArtistTagsAsync(track.Artist, cancellationToken);
        }
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

    /// <summary>
    /// Genres for one recording, asked for directly rather than as a side effect of a full lookup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Track tags first, then the artist's.</b> Track-level tags are the better answer — they
    /// describe this recording rather than its performer — but Last.fm simply does not have them
    /// for a great many tracks, and returns an empty cloud rather than an error. ATB's
    /// "9Pm (Till I Come)" is the case that prompted this: no track tags at all, while the artist
    /// carries trance, electronic and dance. Stopping at the empty track answer threw away a
    /// perfectly good one sitting behind it.
    /// </para>
    /// <para>
    /// This deliberately does not go through <see cref="EnrichAsync(Track, CancellationToken)"/>.
    /// That path only maps anything — genres included — when Last.fm also returns an
    /// <i>album</i>, which is the wrong gate for a question that never asked about albums, and it
    /// fetches a release, its artwork and its track listing to read three strings off the side.
    /// </para>
    /// <para>
    /// <c>autocorrect=1</c> on both, so Last.fm canonicalises the spelling it was given rather
    /// than missing on punctuation or case.
    /// </para>
    /// </remarks>
    public async Task<string[]> GetGenresAsync(
        string? artist,
        string? title,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artist)) return [];

        if (!string.IsNullOrWhiteSpace(title))
        {
            var trackTags = await FetchAsync(TrackTagsUri(artist, title), cancellationToken);
            var genres = LastFmTrackMapper.ChooseGenres(trackTags?.TopTags);

            if (genres.Length > 0)
            {
                Log.Debug("Last.fm tagged the track {Artist} - {Title}: {Genres}.", artist, title, genres);
                return genres;
            }

            Log.Debug(
                "Last.fm has no tags for the track {Artist} - {Title}; asking about the artist.",
                artist,
                title);
        }

        return await ArtistTagsAsync(artist, cancellationToken);
    }

    /// <summary>The artist's own tags, from cache when this session has already asked.</summary>
    private async Task<string[]> ArtistTagsAsync(string? artist, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artist)) return [];

        if (_artistTags.TryGetValue(artist, out var cached)) return cached;

        var artistTags = await FetchAsync(ArtistTagsUri(artist), cancellationToken);
        var genres = LastFmTrackMapper.ChooseGenres(artistTags?.TopTags);

        // Cached even when empty, for the same reason Spotify's is: an artist Last.fm has no tags
        // for still has none on the next track, and re-asking every track is the cost this avoids.
        _artistTags[artist] = genres;

        Log.Debug(
            "Last.fm gave the artist {Artist} the tags: {Genres}.",
            artist,
            genres.Length == 0 ? "none" : string.Join(", ", genres));

        return genres;
    }

    private Uri TrackTagsUri(string artist, string title) => BuildUri(
        "track.getTopTags",
        ("artist", artist),
        ("track", title),
        ("autocorrect", "1"));

    private Uri ArtistTagsUri(string artist) => BuildUri(
        "artist.getTopTags",
        ("artist", artist),
        ("autocorrect", "1"));

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
