using Offstream.Core.Metadata;

namespace Offstream.Core.Spotify;

/// <summary>What Spotify's window says at one moment in time.</summary>
/// <param name="WindowTitle">The raw window title.</param>
/// <param name="IsPlaying">Whether playback is running.</param>
public readonly record struct SpotifyWindow(string WindowTitle, bool IsPlaying);

/// <summary>
/// Turns a Spotify window title into a <see cref="Track"/>.
/// </summary>
/// <remarks>
/// <para>
/// The window title is the only track signal the app has without an API key, and its format
/// is undocumented and inconsistent. These rules were established against the real client
/// over years — do not "simplify" them without a failing test to justify it.
/// </para>
/// <para>
/// <b>Split from the reference implementation's <c>SpotifyStatus</c> deliberately.</b> That
/// class both parsed the title and reached into an <c>ExternalAPI.Instance</c> singleton to
/// enrich the result, which made parsing untestable without a live API. Parsing is pure and
/// lives here; enrichment is a separate concern in the metadata layer.
/// </para>
/// </remarks>
public static class SpotifyTitleParser
{
    private const string DashSeparator = " - ";
    private const string ParenthesisSeparator = " (";

    /// <summary>Parses a window observation into a track.</summary>
    public static Track Parse(SpotifyWindow window)
    {
        var windowTitle = window.WindowTitle ?? string.Empty;

        var tags = SplitOnDash(windowTitle, 2);
        var longTitlePart = TagAt(tags, 2);
        var (titleTags, separatorType) = SplitTitle(longTitlePart ?? string.Empty);

        // One dash-separated part means no "artist - title" shape at all. While playing,
        // that is how an advertisement or a podcast-style title presents itself.
        var looksLikeAnAd = tags.Length < 2;

        return new Track
        {
            Ad = windowTitle.IsAdvertisement() || (looksLikeAnAd && window.IsPlaying),
            Playing = window.IsPlaying,
            Artist = TagAt(tags, 1),
            Title = TagAt(titleTags, 1),
            TitleExtended = TagAt(titleTags, 2),
            TitleExtendedSeparatorType = separatorType,
        };
    }

    /// <summary>Splits a title on " - ", keeping at most <paramref name="maxSize"/> parts.</summary>
    public static string[] SplitOnDash(string title, int maxSize = 3)
    {
        ArgumentNullException.ThrowIfNull(title);

        return title.Split(DashSeparator, maxSize, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Separates a title from its trailing qualifier, e.g. "Song - Live" or "Song (Remix)",
    /// reporting which separator was used so the file name can reproduce it.
    /// </summary>
    public static (string[]? Tags, TitleSeparatorType SeparatorType) SplitTitle(string title, int maxSize = 2)
    {
        if (string.IsNullOrWhiteSpace(title)) return (null, TitleSeparatorType.None);

        var byDash = SplitOnDash(title, maxSize);
        var byParenthesis = title.Split(ParenthesisSeparator, maxSize, StringSplitOptions.RemoveEmptyEntries);

        if (byParenthesis.Length == 2)
            byParenthesis[1] = byParenthesis[1].Replace(")", string.Empty, StringComparison.Ordinal);

        if (byDash.Length > 1) return (byDash, TitleSeparatorType.Dash);
        if (byParenthesis.Length > 1) return (byParenthesis, TitleSeparatorType.Parenthesis);

        return ([title], TitleSeparatorType.None);
    }

    /// <summary>One-based element access that yields null rather than throwing.</summary>
    public static string? TagAt(string[]? tags, int position) =>
        tags is not null && position != 0 && tags.Length >= position ? tags[position - 1] : null;
}
