namespace Offstream.Core.Spotify;

/// <summary>
/// The window titles Spotify shows when it is not playing a recordable track.
/// </summary>
/// <remarks>
/// Track detection reads the Spotify window title, so these literals are the difference
/// between recording a song and recording an advertisement. They come from the reference
/// implementation, where they were established against the real client over years.
/// </remarks>
public static class SpotifyWindowTitles
{
    public const string Spotify = "Spotify";
    public const string SpotifyFree = "Spotify Free";
    public const string SpotifyPremium = "Spotify Premium";
    public const string Advertisement = "Advertisement";

    /// <summary>Album name used when a track reports none.</summary>
    public const string UntitledAlbum = "Untitled";

    private static readonly string[] IdleTitles = [Spotify, SpotifyFree, SpotifyPremium];

    /// <summary>True when the title means "idle", "advertisement", or nothing at all.</summary>
    public static bool IsNullOrAdOrIdle(this string? value) =>
        value.IsNullOrIdle() || value.IsAdvertisement();

    /// <summary>True when the title is Spotify's advertisement placeholder.</summary>
    public static bool IsAdvertisement(this string? value) =>
        string.Equals(value, Advertisement, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the title is blank or one of Spotify's idle titles.</summary>
    public static bool IsNullOrIdle(this string? value) =>
        string.IsNullOrWhiteSpace(value) || value.IsIdle();

    /// <summary>True when the title is one of Spotify's idle titles.</summary>
    public static bool IsIdle(this string? value) =>
        value is not null && IdleTitles.Contains(value, StringComparer.OrdinalIgnoreCase);
}
