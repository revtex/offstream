using System.Text.RegularExpressions;
using Offstream.Core.Text;

namespace Offstream.Core.Metadata.Providers;

/// <summary>
/// Applies a Last.fm track response onto a <see cref="Track"/> scraped from the window title.
/// </summary>
/// <remarks>
/// Split out from the reference implementation's <c>LastFMAPI</c>, which mixed HTTP calls,
/// retry-with-simplified-title logic and this mapping in one class. Mapping is pure, so it is
/// tested directly against fixture nodes with no network involved (plan §9.3).
/// </remarks>
public static partial class LastFmTrackMapper
{
    /// <summary>
    /// Last.fm serves several sizes at fixed URL shapes; the 300px variants are the best
    /// trade-off between tag bloat and looking right in a player.
    /// </summary>
    [GeneratedRegex(@"\/300x300\/|\/300s\/")]
    private static partial Regex PreferredCoverSize { get; }

    /// <summary>How many of Last.fm's community tags are trustworthy enough to write.</summary>
    private const int MaximumGenres = 3;

    /// <summary>Copies album, duration, cover art and performers onto <paramref name="track"/>.</summary>
    /// <remarks>
    /// Only fields Last.fm actually supplies are written. The window title stays the source
    /// of truth for artist and title, because Last.fm's search can match a different release.
    /// </remarks>
    public static void Apply(Track track, LastFmTrack response)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(response);

        // Fills, never clears — the media session may already have supplied both, and Last.fm
        // not knowing an album is not the same as the track not having one.
        track.Album = string.IsNullOrWhiteSpace(response.Album?.Title) ? track.Album : response.Album.Title;
        track.AlbumPosition = response.Album?.TrackPosition ?? track.AlbumPosition;

        // Last.fm reports milliseconds; everything downstream works in seconds.
        track.Length = response.Duration is > 0 ? response.Duration / 1000 : null;
        track.AlbumArtUrl = ChooseCoverUrl(response.Album);

        // An artist-less track yields an empty array rather than a one-element array of null:
        // Track.Artists joins AlbumArtists when non-empty, so [null] would render as "".
        string[] albumArtists = track.Artist is null ? [] : [track.Artist];

        track.Performers = [.. albumArtists.Concat(track.ToString().ToPerformers())];

        // Last.fm has no album-artist field, so the line above is the track's own artist standing
        // in for one — right for a window title, wrong the moment a media session has reported a
        // real one. A compilation is the case that shows it: the session says "Various Artists"
        // and the stand-in would overwrite that with whoever performed this one track. So the
        // stand-in fills a gap and never replaces an answer, like every other field here.
        if (track.AlbumArtists is not { Length: > 0 } && albumArtists.Length > 0)
        {
            track.AlbumArtists = albumArtists;
        }
    }

    /// <summary>
    /// The track's most-applied community tags, as genres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference set genres to an empty array here and never looked at the <c>toptags</c>
    /// node, so a Last.fm-tagged library had no genre on anything. Spotify has since stopped
    /// returning album genres for most of its catalogue, which left the genre tag empty
    /// regardless of which provider was chosen.
    /// </para>
    /// <para>
    /// <b>Only the top few, because these are a folksonomy rather than a taxonomy.</b> Last.fm's
    /// tags are whatever listeners typed, so the tail of the list is personal
    /// ("favourites", "seen live") far more often than it is a genre. The most-applied ones are
    /// reliably genre-like; taking the whole cloud would put someone else's listening habits in
    /// the file.
    /// </para>
    /// </remarks>
    public static string[] ChooseGenres(LastFmTopTags? topTags) =>
    [
        .. (topTags?.Tags ?? [])
            .Select(tag => tag.Name?.Trim())
            .Where(name => !string.IsNullOrEmpty(name))
            .Take(MaximumGenres)!,
    ];

    /// <summary>Prefers a 300px cover, falling back to the largest available.</summary>
    public static string? ChooseCoverUrl(LastFmAlbum? album)
    {
        if (album is null) return null;

        string?[] candidates =
        [
            album.ExtraLargeCoverUrl,
            album.LargeCoverUrl,
            album.MediumCoverUrl,
            album.SmallCoverUrl,
        ];

        var urls = candidates.Where(url => url is not null).ToArray();

        return urls.FirstOrDefault(url => PreferredCoverSize.IsMatch(url!)) ?? urls.FirstOrDefault();
    }
}
