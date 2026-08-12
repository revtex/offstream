using System.Globalization;
using System.Text.RegularExpressions;
using Offstream.Core.Metadata;

namespace Offstream.Core.Naming;

/// <summary>
/// Renders the user's output path template, e.g. <c>{artist}\{album}\{track:00} - {title}</c>.
/// A backslash separates folders; the last segment becomes the file name (without extension).
/// </summary>
/// <remarks>
/// <para>
/// Ported with its behaviour intact. The template <em>syntax</em> is user-facing and must not
/// drift: users have templates saved and file libraries already laid out by them (plan §7).
/// </para>
/// <para>
/// The reference implementation also had a <c>FromLegacySettings</c> helper that rebuilt a
/// template from the predecessor's pre-template checkbox settings. It is deliberately not
/// ported: Offstream has no settings importer (plan §6), so nothing can ever supply those
/// inputs.
/// </para>
/// </remarks>
public static partial class FileNameTemplate
{
    public const string Default = @"{artist} - {title}";
    public const string Grouped = @"{artist}\{album} ({year})\{title}";

    /// <summary>Default counter padding when the template does not specify one.</summary>
    private const string DefaultCounterMask = "000";

    [GeneratedRegex(@"\{(?<name>[a-z_]+)(?::(?<format>[^}]+))?\}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern { get; }

    /// <summary>Tokens the user may write, in the order they are offered in the UI.</summary>
    public static IReadOnlyList<string> KnownTokens { get; } =
    [
        "artist", "title", "album", "album_artist", "year", "track", "disc", "count", "date", "time",
    ];

    /// <summary>
    /// Splits a rendered template into folder segments plus the file name.
    /// </summary>
    /// <remarks>
    /// Segments that render empty are dropped, so a missing album does not leave a blank
    /// directory level in the output tree.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Every segment rendered empty.</exception>
    public static (string[] Folders, string FileName) Render(
        string? template, Track track, int? counter, DateTime now, int folderMaxLength, int fileMaxLength)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (string.IsNullOrWhiteSpace(template)) template = Default;

        var segments = template
            .Split(['\\', '/'])
            .Select(segment => RenderSegment(segment, track, counter, now))
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();

        if (segments.Count == 0) throw new InvalidOperationException("File name cannot be empty.");

        var name = segments[^1];
        segments.RemoveAt(segments.Count - 1);

        var folders = segments.Select(f => PathText.CleanSegment(f, folderMaxLength)).ToArray();

        return (folders, PathText.CleanSegment(name, fileMaxLength));
    }

    private static string RenderSegment(string segment, Track track, int? counter, DateTime now)
    {
        var rendered = TokenPattern.Replace(segment, match =>
            Resolve(
                match.Groups["name"].Value.ToLowerInvariant(),
                match.Groups["format"].Success ? match.Groups["format"].Value : null,
                track,
                counter,
                now));

        return PathText.Tidy(rendered);
    }

    private static string Resolve(string name, string? format, Track track, int? counter, DateTime now) =>
        name switch
        {
            "artist" => PathText.RemoveDiacritics(track.Artists),
            "album_artist" => PathText.RemoveDiacritics(
                track.AlbumArtists is { Length: > 0 } ? string.Join(", ", track.AlbumArtists) : track.Artist),
            "title" => PathText.RemoveDiacritics(track.ToTitleString()),
            "album" => PathText.RemoveDiacritics(track.Album),
            "year" => Number(track.Year, format),
            "track" => Number(track.AlbumPosition, format),
            "disc" => Number(track.Disc, format),
            "count" => Number(counter, format),
            "date" => now.ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
            "time" => now.ToString(format ?? "HHmmss", CultureInfo.InvariantCulture),

            // Unknown tokens render empty rather than leaking braces into a file name.
            _ => string.Empty,
        };

    private static string Number(int? value, string? format)
    {
        if (value is null or 0) return string.Empty;

        return format is not null
            ? value.Value.ToString(format, CultureInfo.InvariantCulture)
            : value.Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The highest counter the template can represent, from the padding on <c>{count}</c>
    /// (so <c>{count:000}</c> gives 999). Templates without the token are unlimited.
    /// </summary>
    public static int GetCounterMax(string? template)
    {
        var format = CounterFormat(template);
        if (format is null) return int.MaxValue;

        var digits = format.Count(c => c == '0');
        if (digits is <= 0 or > 9) return int.MaxValue;

        return (int)Math.Pow(10, digits) - 1;
    }

    /// <summary>Padding mask for the counter field in the UI, e.g. "000" for <c>{count:000}</c>.</summary>
    public static string GetCounterMask(string? template)
    {
        var format = CounterFormat(template);

        return format is not null && format.All(c => c == '0') ? format : DefaultCounterMask;
    }

    /// <summary>Whether the template uses <c>{count}</c> at all.</summary>
    public static bool UsesCounter(string? template) => FindCounterToken(template) is not null;

    private static string? CounterFormat(string? template)
    {
        var match = FindCounterToken(template);
        return match?.Groups["format"] is { Success: true } group ? group.Value : null;
    }

    private static Match? FindCounterToken(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        foreach (var match in TokenPattern.Matches(template).Cast<Match>())
        {
            if (match.Groups["name"].Value.Equals("count", StringComparison.OrdinalIgnoreCase)) return match;
        }

        return null;
    }

    /// <summary>Returns null when the template is usable, otherwise why it is not.</summary>
    public static string? Validate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "Template cannot be empty.";

        if (IsRooted(template)) return "Template must be a relative path.";

        var unknown = TokenPattern.Matches(template).Cast<Match>()
            .Select(m => m.Groups["name"].Value.ToLowerInvariant())
            .Where(n => !KnownTokens.Contains(n))
            .Distinct()
            .ToArray();

        if (unknown.Length > 0)
            return $"Unknown token(s): {string.Join(", ", unknown.Select(u => $"{{{u}}}"))}";

        var withoutTokens = TokenPattern.Replace(template, string.Empty);
        if (withoutTokens.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)
            return "Template contains characters that are not allowed in a path.";

        var last = template.Split('\\', '/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(last)) return "Template must end with a file name.";
        if (!TokenPattern.IsMatch(last)) return "The file name part must contain at least one token.";

        return null;
    }

    private static bool IsRooted(string template) =>
        template.StartsWith('\\')
        || template.StartsWith('/')
        || (template.Length > 1 && template[1] == ':');
}
