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

    /// <summary>Copies album, duration, cover art and performers onto <paramref name="track"/>.</summary>
    /// <remarks>
    /// Only fields Last.fm actually supplies are written. The window title stays the source
    /// of truth for artist and title, because Last.fm's search can match a different release.
    /// </remarks>
    public static void Apply(Track track, LastFmTrack response)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(response);

        track.Album = response.Album?.Title;
        track.AlbumPosition = response.Album?.TrackPosition;

        // Last.fm reports milliseconds; everything downstream works in seconds.
        track.Length = response.Duration is > 0 ? response.Duration / 1000 : null;
        track.Genres = [];
        track.AlbumArtUrl = ChooseCoverUrl(response.Album);

        // An artist-less track yields an empty array rather than a one-element array of null:
        // Track.Artists joins AlbumArtists when non-empty, so [null] would render as "".
        string[] albumArtists = track.Artist is null ? [] : [track.Artist];

        track.Performers = [.. albumArtists.Concat(track.ToString().ToPerformers())];
        track.AlbumArtists = albumArtists;
    }

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
