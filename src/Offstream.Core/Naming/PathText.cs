using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using Offstream.Core.Text;

namespace Offstream.Core.Naming;

/// <summary>
/// Turns arbitrary track metadata into text that is safe as a Windows path segment.
/// </summary>
/// <remarks>
/// Ported from the reference implementation's <c>Normalize</c> and
/// <c>FileManager.GetCleanFileFolder</c>. The behaviour here is load-bearing and subtle:
/// invalid characters are <em>stripped</em>, never substituted, because substitution changes
/// how a name sorts and how it matches an existing file on a re-record.
/// </remarks>
public static partial class PathText
{
    /// <summary>Folder name used when a segment cleans down to nothing.</summary>
    public const string InvalidSegmentPlaceholder = "INVALID";

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RepeatedWhitespace { get; }

    [GeneratedRegex(@"^[\s\-_.]+")]
    private static partial Regex LeadingSeparators { get; }

    [GeneratedRegex(@"[\s\-_.]+$")]
    private static partial Regex TrailingSeparators { get; }

    [GeneratedRegex(@"\(\s*\)|\[\s*\]")]
    private static partial Regex EmptyBrackets { get; }

    private static readonly SearchValues<char> InvalidFileNameChars =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>
    /// Drops characters Windows forbids in a file name, preserving accented letters.
    /// </summary>
    /// <remarks>
    /// Round-trips through Unicode form D and back to C so a combining mark that happens to
    /// be an invalid path character can be removed without destroying its base letter.
    /// </remarks>
    public static string RemoveDiacritics(string? text)
    {
        if (text is null) return string.Empty;

        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (!InvalidFileNameChars.Contains(c)) builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Cleans a single path segment and truncates it to <paramref name="maxLength"/>.
    /// </summary>
    public static string CleanSegment(string name, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(name);

        var trimmed = name.TrimEndPath() ?? string.Empty;
        var builder = new StringBuilder(trimmed.Length);

        foreach (var c in trimmed)
        {
            if (!InvalidFileNameChars.Contains(c)) builder.Append(c);
        }

        var cleaned = builder.ToString().ToMaxLength(maxLength);

        return string.IsNullOrWhiteSpace(cleaned) ? InvalidSegmentPlaceholder : cleaned;
    }

    /// <summary>
    /// Cleans up what an empty token leaves behind: doubled spaces, dangling separators such
    /// as the " - " in "{track} - {title}" with no track number, and empty bracket pairs.
    /// </summary>
    public static string Tidy(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        value = RepeatedWhitespace.Replace(value, " ");
        value = LeadingSeparators.Replace(value, string.Empty);
        value = TrailingSeparators.Replace(value, string.Empty);
        value = EmptyBrackets.Replace(value, string.Empty);

        return value.Trim();
    }
}
