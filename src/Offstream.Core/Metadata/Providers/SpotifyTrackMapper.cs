using System.Globalization;
using System.Text.RegularExpressions;
using Offstream.Core.Spotify;
using SpotifyAPI.Web;

namespace Offstream.Core.Metadata.Providers;

/// <summary>
/// Applies a Spotify Web API track and album onto a <see cref="Track"/> scraped from the window
/// title.
/// </summary>
/// <remarks>
/// <para>
/// Split out from the reference implementation's <c>SpotifyAPI</c> class, which mixed this
/// mapping with the HTTP calls, the PKCE dialog and token refresh in one type. Mapping is pure,
/// so it is tested directly against SDK model fixtures with no network involved (plan §9.3) —
/// the same split already made for <see cref="LastFmTrackMapper"/>.
/// </para>
/// <para>
/// <b>Title splitting is the window-title parser's job, not a second implementation of it.</b>
/// The reference duplicated its <c>SpotifyStatus.GetTitleTags</c> logic here; this reuses
/// <see cref="SpotifyTitleParser.SplitTitle"/> so "Song - Live" and "Song (Remix)" are split
/// exactly the same way regardless of which source supplied the title.
/// </para>
/// </remarks>
public static partial class SpotifyTrackMapper
{
    /// <summary>The three release-date precisions Spotify documents, and nothing else.</summary>
    [GeneratedRegex(@"^\d{4}(-\d{2}(-\d{2})?)?$")]
    private static partial Regex IsoDate { get; }

    /// <summary>Copies title, artist and track/disc position from a Spotify track.</summary>
    /// <remarks>
    /// <see cref="FullTrack.TrackNumber"/> and <see cref="FullTrack.DiscNumber"/> are plain
    /// non-nullable <c>int</c> on the SDK model, defaulting to 0 on a track the SDK could not
    /// fully populate. Spotify numbers both from 1, so 0 is never a real position — it is
    /// mapped to <see langword="null"/> rather than written as a literal zeroth track, which a
    /// tag reader would otherwise show as-is.
    /// </remarks>
    public static void Apply(Track track, FullTrack spotifyTrack)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(spotifyTrack);

        var performers = ArtistNames(spotifyTrack.Artists);
        var (titleTags, separatorType) = SpotifyTitleParser.SplitTitle(spotifyTrack.Name ?? string.Empty);

        track.SetArtistFromApi(performers.FirstOrDefault());
        track.SetTitleFromApi(SpotifyTitleParser.TagAt(titleTags, 1));
        track.SetTitleExtendedFromApi(SpotifyTitleParser.TagAt(titleTags, 2), separatorType);

        // Fills, never clears. The detected track may already carry a position from the media
        // session, and a provider that has none of its own must not take that away — see ToTrack.
        track.AlbumPosition = PositiveOrNull(spotifyTrack.TrackNumber) ?? track.AlbumPosition;
        track.Performers = performers;
        track.Disc = PositiveOrNull(spotifyTrack.DiscNumber);
    }

    /// <summary>Copies album, genres, release year and cover art from a Spotify album.</summary>
    /// <remarks>
    /// Spotify's own API has stopped returning <c>genres</c> on album objects for most catalog
    /// entries as of its late-2024 changes; <see cref="FullAlbum.Genres"/> is copied as-is, so
    /// an empty result here can mean either "no genres" or "Spotify no longer tells us" — there
    /// is nothing this mapping can do about that distinction.
    /// </remarks>
    public static void Apply(Track track, FullAlbum spotifyAlbum)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(spotifyAlbum);

        var albumArtists = ArtistNames(spotifyAlbum.Artists);

        // As above: the media session already supplied both for the track being recorded, so an
        // album object that is missing either leaves what is there rather than blanking it.
        track.AlbumArtists = albumArtists is { Length: > 0 } ? albumArtists : track.AlbumArtists;
        track.Album = string.IsNullOrWhiteSpace(spotifyAlbum.Name) ? track.Album : spotifyAlbum.Name;
        track.Genres = spotifyAlbum.Genres?.ToArray() ?? [];
        track.Year = ParseReleaseYear(spotifyAlbum.ReleaseDate);
        track.AlbumArtUrl = ChooseCoverUrl(spotifyAlbum.Images);

        // Kept at Spotify's own precision for the tag, while Year stays an integer for the
        // {year} filename token. A release Spotify knows to the day was being truncated.
        track.ReleaseDate = NormalizeReleaseDate(spotifyAlbum.ReleaseDate);

        // Also as above, now that the media session reports a count of its own: an album object
        // that does not carry one leaves the session's standing.
        track.AlbumTrackCount = PositiveOrNull(spotifyAlbum.TotalTracks) ?? track.AlbumTrackCount;
        track.Copyright = ChooseCopyright(spotifyAlbum.Copyrights);
    }

    /// <summary>
    /// The release date, if it is one of the three shapes Spotify documents.
    /// </summary>
    /// <remarks>
    /// Passed through rather than reformatted: <c>2010</c>, <c>2010-10</c> and <c>2010-10-10</c>
    /// are all valid tag values and all say something true about the precision. Anything else is
    /// dropped rather than written, since a malformed date in a tag is worse than none — and the
    /// shape is matched rather than merely parsed, so a locale-shaped <c>10/10/2010</c> that
    /// <see cref="DateTime.TryParse(string?, out DateTime)"/> would happily accept does not reach
    /// the file as a date no tag reader agrees on.
    /// </remarks>
    private static string? NormalizeReleaseDate(string? releaseDate) =>
        releaseDate is not null && IsoDate.IsMatch(releaseDate) && ParseReleaseYear(releaseDate) is not null
            ? releaseDate
            : null;

    /// <summary>
    /// The copyright line, preferring the recording's (<c>P</c>) over the composition's (<c>C</c>).
    /// </summary>
    /// <remarks>
    /// Offstream records audio, so the phonogram line is the one that describes what is in the
    /// file. Spotify returns both on most albums.
    /// </remarks>
    private static string? ChooseCopyright(IEnumerable<Copyright>? copyrights)
    {
        var all = copyrights?.Where(c => !string.IsNullOrWhiteSpace(c.Text)).ToArray() ?? [];

        return (all.FirstOrDefault(c => c.Type == "P") ?? all.FirstOrDefault())?.Text;
    }

    /// <summary>
    /// Prefers the largest cover at or under 300px — big enough to look right in a player,
    /// small enough not to bloat every tagged file. Falls back to nothing if every image
    /// Spotify sent is larger than that.
    /// </summary>
    public static string? ChooseCoverUrl(IEnumerable<Image>? images) =>
        images?
            .Where(image => image.Width <= 300)
            .OrderByDescending(image => image.Width)
            .Select(image => image.Url)
            .FirstOrDefault();

    private static string[] ArtistNames(IEnumerable<SimpleArtist>? artists) =>
        artists?.Select(a => a.Name).ToArray() ?? [];

    private static int? PositiveOrNull(int value) => value > 0 ? value : null;

    /// <summary>
    /// Spotify's <c>release_date</c> shortens to whatever precision the catalog entry actually
    /// has: a full <c>2010-10-10</c>, a bare month <c>2010-10</c>, or just a year <c>2010</c>.
    /// <see cref="DateTime.TryParse(string?, out DateTime)"/> accepts the first two but rejects
    /// a lone four-digit year outright, which silently dropped the year for every album whose
    /// only known precision is the year itself.
    /// </summary>
    private static int? ParseReleaseYear(string? releaseDate)
    {
        if (DateTime.TryParse(releaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.Year;

        return releaseDate is { Length: 4 } && int.TryParse(
            releaseDate, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }
}
