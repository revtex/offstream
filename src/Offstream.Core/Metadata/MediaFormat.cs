namespace Offstream.Core.Metadata;

/// <summary>Output container/codec for a recording.</summary>
/// <remarks>
/// <see cref="Flac"/> and <see cref="Aac"/> are plan §11 additions, added in Phase 3: once
/// ffmpeg owns every conversion they cost only a profile entry. Adding them changes what
/// <c>"flac"</c> parses to, which is why the parsing test moved with them.
/// </remarks>
public enum MediaFormat
{
    Mp3,
    Wav,
    Opus,
    Flac,
    Aac,
}

/// <summary>Where track metadata is fetched from.</summary>
public enum MetadataProvider
{
    None = -1,
    LastFm = 0,
    Spotify,
}

/// <summary>Requested album-art size, as Last.fm names them.</summary>
/// <remarks>Lower-case members are load-bearing: values parse case-insensitively from API responses.</remarks>
public enum AlbumCoverSize
{
    Small,
    Medium,
    Large,
    ExtraLarge,
}

/// <summary>Status field of a Last.fm response node.</summary>
public enum LastFmNodeStatus
{
    Ok,
    Failed,
}

/// <summary>How a track's extra title information is separated from the title.</summary>
public enum TitleSeparatorType
{
    None,
    Dash,
    Parenthesis,
}
