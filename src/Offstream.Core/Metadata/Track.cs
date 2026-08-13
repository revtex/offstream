using Offstream.Core.Spotify;

namespace Offstream.Core.Metadata;

/// <summary>
/// One track, as assembled from the Spotify window title and then enriched by a metadata provider.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the reference implementation's <c>Track</c>. The central design point is the
/// pair of backing fields per text property: values scraped from the window title are
/// overridden by values fetched from Last.fm or the Spotify Web API, and the API value always
/// wins once set. Reverting that would regress tagging for every track whose window title is
/// abbreviated or decorated.
/// </para>
/// </remarks>
public sealed class Track
{
    private string? _apiArtist;
    private string? _apiTitle;
    private string? _apiTitleExtended;
    private string? _artist;
    private string? _title;
    private string? _titleExtended;

    public Track()
    {
    }

    /// <summary>Copy constructor. The recorder snapshots a track so later updates cannot mutate it mid-write.</summary>
    public Track(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        Artist = track.Artist;
        Title = track.Title;
        Ad = track.Ad;
        Playing = track.Playing;

        TitleExtended = track.TitleExtended;
        TitleExtendedSeparatorType = track.TitleExtendedSeparatorType;

        Album = track.Album;
        Genres = track.Genres;
        AlbumPosition = track.AlbumPosition;

        CurrentPosition = track.CurrentPosition;
        Length = track.Length;

        Performers = track.Performers;
        Disc = track.Disc;
        AlbumArtists = track.AlbumArtists;
        Year = track.Year;
        ReleaseDate = track.ReleaseDate;
        AlbumTrackCount = track.AlbumTrackCount;
        Copyright = track.Copyright;

        AlbumArtUrl = track.AlbumArtUrl;
        AlbumArtImage = track.AlbumArtImage;
    }

    /// <summary>Album artists when known, otherwise the artist plus any featured performers.</summary>
    public string Artists
    {
        get
        {
            if (AlbumArtists is { Length: > 0 }) return string.Join(", ", AlbumArtists);

            return string.Join(", ", new[] { Artist }.Concat(Performers ?? []).Distinct());
        }
    }

    public string? Artist
    {
        get => _apiArtist ?? _artist;
        set => _artist = string.IsNullOrEmpty(value) ? null : value;
    }

    public string? Title
    {
        get => _apiTitle ?? _title;
        set => _title = string.IsNullOrEmpty(value) ? null : value;
    }

    public string? TitleExtended
    {
        get => _apiTitleExtended ?? _titleExtended;
        set => _titleExtended = string.IsNullOrEmpty(value) ? null : value;
    }

    public TitleSeparatorType TitleExtendedSeparatorType { get; set; } = TitleSeparatorType.None;

    public bool Ad { get; set; }
    public bool Playing { get; set; }
    public bool? MetadataUpdated { get; set; }

    public string? Album { get; set; }
    public string[]? Genres { get; set; }
    public int? AlbumPosition { get; set; }

    public int? CurrentPosition { get; set; }
    public int? Length { get; set; }

    public string[]? Performers { get; set; }
    public int? Disc { get; set; }
    public string[]? AlbumArtists { get; set; }
    public int? Year { get; set; }

    /// <summary>
    /// The release date at whatever precision the provider knows it, as an ISO-8601 string.
    /// </summary>
    /// <remarks>
    /// Kept alongside <see cref="Year"/> rather than replacing it. Spotify's <c>release_date</c>
    /// is often a full date, and every container Offstream writes stores one happily — but the
    /// <c>{year}</c> filename token is an integer and folder names should not suddenly gain a
    /// month and a day. So the tag gets the precise value and the file name keeps the year.
    /// </remarks>
    public string? ReleaseDate { get; set; }

    /// <summary>How many tracks the album has, for the "4 of 12" form of the track tag.</summary>
    public int? AlbumTrackCount { get; set; }

    /// <summary>
    /// The copyright line, as the provider states it.
    /// </summary>
    /// <remarks>
    /// There is deliberately no ISRC or label alongside this. Both would be worth writing and
    /// neither is available: Spotify removed <c>external_ids</c> and <c>label</c> from its API,
    /// and <c>SpotifyAPI.Web</c> marks both obsolete with "field has been removed". Last.fm never
    /// supplied either. Adding the properties back means adding them with nothing to fill them.
    /// </remarks>
    public string? Copyright { get; set; }

    public string? AlbumArtUrl { get; set; }
    public byte[]? AlbumArtImage { get; set; }

    private bool IsNormal =>
        !string.IsNullOrEmpty(Artist) && !string.IsNullOrEmpty(Title) && !Ad;

    /// <summary>A recordable track is currently playing.</summary>
    public bool IsNormalPlaying => IsNormal && Playing;

    /// <summary>
    /// The window title had no " - " separator, so the whole thing is in <see cref="Artist"/>
    /// and it is not one of Spotify's idle titles.
    /// </summary>
    public bool IsUnknown => string.IsNullOrEmpty(Title) && !Artist.IsNullOrAdOrIdle();

    public bool IsUnknownPlaying => IsUnknown && Playing;

    public void SetArtistFromApi(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _apiArtist = value;
    }

    public void SetTitleFromApi(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _apiTitle = value;
    }

    public void SetTitleExtendedFromApi(string? value, TitleSeparatorType separatorType)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        _apiTitleExtended = value;
        TitleExtendedSeparatorType = separatorType;
    }

    private string? GetTitleExtended() =>
        string.IsNullOrEmpty(TitleExtended)
            ? null
            : TitleExtendedSeparatorType switch
            {
                TitleSeparatorType.Dash => $" - {TitleExtended}",
                TitleSeparatorType.Parenthesis => $" ({TitleExtended})",
                _ => string.Empty,
            };

    public override string ToString()
    {
        if (!string.IsNullOrEmpty(Artist) && !string.IsNullOrEmpty(Title))
        {
            var song = $"{Artists} - {Title}";
            if (!string.IsNullOrEmpty(TitleExtended)) song += GetTitleExtended();
            return song;
        }

        return !string.IsNullOrEmpty(Artist) ? Artist : SpotifyWindowTitles.Spotify;
    }

    /// <summary>The title as it should appear in a file name or tag.</summary>
    public string ToTitleString()
    {
        var song = IsUnknownPlaying ? Artist : Title;

        if (!string.IsNullOrEmpty(Title) && !string.IsNullOrEmpty(TitleExtended)) song += GetTitleExtended();

        return song ?? string.Empty;
    }

    public override bool Equals(object? obj) =>
        obj is Track other
        && other.Artist == Artist
        && other.Title == Title
        && other.TitleExtended == TitleExtended
        && other.Ad == Ad;

    public override int GetHashCode() => HashCode.Combine(Artist, Title, TitleExtended, Ad);
}
