using System.Xml.Serialization;
using Offstream.Core.Text;

namespace Offstream.Core.Metadata.Providers;

/// <summary>One cover-art image from a Last.fm album node.</summary>
[XmlRoot(ElementName = "image")]
public sealed class LastFmImage
{
    [XmlAttribute(AttributeName = "size")]
    public string? Size { get; set; }

    [XmlText]
    public string? Url { get; set; }

    /// <summary>The size as an enum, or null when Last.fm sends one we do not know.</summary>
    public AlbumCoverSize? CoverSize => Size.ToEnum<AlbumCoverSize>();
}

/// <summary>The album node of a Last.fm track response.</summary>
[XmlRoot(ElementName = "album")]
public sealed class LastFmAlbum
{
    [XmlElement(ElementName = "artist")]
    public string? Artist { get; set; }

    [XmlElement(ElementName = "title")]
    public string? Title { get; set; }

    [XmlElement(ElementName = "image")]
    public List<LastFmImage> Images { get; set; } = [];

    [XmlAttribute(AttributeName = "position")]
    public string? Position { get; set; }

    /// <summary>Track position within the album, when Last.fm reports a numeric one.</summary>
    public int? TrackPosition => Position.ToNullableInt();

    public string? ExtraLargeCoverUrl => UrlForSize(AlbumCoverSize.ExtraLarge);
    public string? LargeCoverUrl => UrlForSize(AlbumCoverSize.Large);
    public string? MediumCoverUrl => UrlForSize(AlbumCoverSize.Medium);
    public string? SmallCoverUrl => UrlForSize(AlbumCoverSize.Small);

    private string? UrlForSize(AlbumCoverSize size) =>
        Images.FirstOrDefault(image => image?.CoverSize == size)?.Url;
}

/// <summary>The artist node of a Last.fm track response.</summary>
[XmlRoot(ElementName = "artist")]
public sealed class LastFmArtist
{
    [XmlElement(ElementName = "name")]
    public string? Name { get; set; }
}

/// <summary>One of Last.fm's community tags.</summary>
[XmlRoot(ElementName = "tag")]
public sealed class LastFmTag
{
    [XmlElement(ElementName = "name")]
    public string? Name { get; set; }
}

/// <summary>The tag cloud a Last.fm track carries, most-applied first.</summary>
[XmlRoot(ElementName = "toptags")]
public sealed class LastFmTopTags
{
    [XmlElement(ElementName = "tag")]
    public List<LastFmTag> Tags { get; set; } = [];
}

/// <summary>The track node of a Last.fm response.</summary>
[XmlRoot(ElementName = "track")]
public sealed class LastFmTrack
{
    [XmlElement(ElementName = "name")]
    public string? Name { get; set; }

    [XmlElement(ElementName = "duration")]
    public int? Duration { get; set; }

    [XmlElement(ElementName = "artist")]
    public LastFmArtist? Artist { get; set; }

    [XmlElement(ElementName = "album")]
    public LastFmAlbum? Album { get; set; }
}

/// <summary>
/// The album node of an <c>album.getInfo</c> response, which is not shaped like the album node
/// nested inside a track.
/// </summary>
/// <remarks>
/// The two differ in the one field that matters: <c>album.getInfo</c> names the album in
/// <c>&lt;name&gt;</c>, while the album nested in a track uses <c>&lt;title&gt;</c>. Modelling
/// them as one type is how the standalone lookup silently returns an album with no title.
/// </remarks>
[XmlRoot(ElementName = "album")]
public sealed class LastFmAlbumInfo
{
    [XmlElement(ElementName = "name")]
    public string? Name { get; set; }

    [XmlElement(ElementName = "artist")]
    public string? Artist { get; set; }

    [XmlElement(ElementName = "image")]
    public List<LastFmImage> Images { get; set; } = [];

    /// <summary>Projects this onto the album shape a track response carries.</summary>
    /// <remarks>
    /// Position 1 is asserted rather than known: a single has one track, and that is the only
    /// case this lookup is used for.
    /// </remarks>
    public LastFmAlbum ToTrackAlbum() => new()
    {
        Title = Name,
        Artist = Artist,
        Images = Images,
        Position = "1",
    };
}

/// <summary>The error node Last.fm returns instead of a result.</summary>
[XmlRoot(ElementName = "error")]
public sealed class LastFmError
{
    [XmlAttribute(AttributeName = "code")]
    public string? Code { get; set; }

    [XmlText]
    public string? Message { get; set; }
}

/// <summary>The <c>lfm</c> document element every Last.fm 2.0 response is wrapped in.</summary>
[XmlRoot(ElementName = "lfm")]
public sealed class LastFmNode
{
    [XmlAttribute(AttributeName = "status")]
    public string? StatusText { get; set; }

    [XmlElement(ElementName = "track")]
    public LastFmTrack? Track { get; set; }

    [XmlElement(ElementName = "album")]
    public LastFmAlbumInfo? Album { get; set; }

    /// <summary>The tag cloud, when the response came from <c>artist.getTopTags</c>.</summary>
    /// <remarks>
    /// That method puts it directly under the root, where <c>track.getInfo</c> nests its own
    /// inside the track node — so it belongs here rather than on <see cref="LastFmTrack"/>,
    /// which no longer carries one at all now that genre comes from the artist.
    /// </remarks>
    [XmlElement(ElementName = "toptags")]
    public LastFmTopTags? TopTags { get; set; }

    [XmlElement(ElementName = "error")]
    public LastFmError? Error { get; set; }

    /// <summary>The status as an enum, or null when Last.fm sends one we do not know.</summary>
    public LastFmNodeStatus? Status => StatusText.ToEnum<LastFmNodeStatus>();
}
