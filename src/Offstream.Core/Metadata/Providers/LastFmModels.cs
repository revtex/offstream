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

/// <summary>The track node of a Last.fm response.</summary>
[XmlRoot(ElementName = "track")]
public sealed class LastFmTrack
{
    [XmlElement(ElementName = "name")]
    public string? Name { get; set; }

    [XmlElement(ElementName = "duration")]
    public int? Duration { get; set; }

    [XmlElement(ElementName = "album")]
    public LastFmAlbum? Album { get; set; }
}
