using System.Text.RegularExpressions;

namespace Offstream.Core.Text;

/// <summary>
/// String helpers shared across parsing, naming and metadata.
/// </summary>
/// <remarks>
/// Ported from the reference implementation's <c>StringExtensions</c>. Behaviour is
/// preserved exactly — these are covered by tests that encode years of edge cases — while
/// the culture-sensitive calls are pinned to invariant, which is what they always meant.
/// </remarks>
public static partial class StringExtensions
{
    [GeneratedRegex(@"(\d+\.)(\d+\.)?(\d+\.)?(\*|\d+)")]
    private static partial Regex VersionPattern { get; }

    [GeneratedRegex(@"[^\d+\.]")]
    private static partial Regex NonVersionCharacters { get; }

    [GeneratedRegex(@"\((with |feat\. )(?<performers>.*)\)")]
    private static partial Regex PerformersPattern { get; }

    [GeneratedRegex(@", ")]
    private static partial Regex PerformerSeparator { get; }

    /// <summary>Parses an int, returning null rather than throwing or defaulting to zero.</summary>
    public static int? ToNullableInt(this string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;

    /// <summary>
    /// Trims whitespace and trailing path separators and other characters invalid in a file name.
    /// </summary>
    public static string? TrimEndPath(this string? path) =>
        path?.Trim().TrimEnd(Path.GetInvalidFileNameChars());

    /// <summary>
    /// Case-insensitive enum parse that yields null for unknown values instead of throwing.
    /// </summary>
    /// <remarks>
    /// Matches member <em>names</em> only, deliberately. <see cref="Enum.TryParse{T}(string, bool, out T)"/>
    /// also accepts the underlying numeric value, so "0" would silently become the first
    /// member — and these values come from a user-editable settings file. The reference
    /// implementation compared against <c>Enum.GetNames</c> for the same reason.
    /// </remarks>
    public static T? ToEnum<T>(this string? value) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(value)) return null;

        foreach (var name in Enum.GetNames<T>())
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<T>(value, ignoreCase: true);
        }

        return null;
    }

    /// <summary>Strips everything that is not a digit or dot, leaving a bare version string.</summary>
    public static string ToVersionAsString(this string? tag) =>
        string.IsNullOrEmpty(tag) ? string.Empty : NonVersionCharacters.Replace(tag, string.Empty);

    /// <summary>
    /// Parses a release tag such as <c>v1.2.3.4</c> into a <see cref="Version"/>, or null when it
    /// does not describe one.
    /// </summary>
    public static Version? ToVersion(this string? value)
    {
        var versionString = value.ToVersionAsString();

        if (string.IsNullOrEmpty(versionString) || !VersionPattern.IsMatch(versionString)) return null;

        return Version.TryParse(versionString, out var version) ? version : null;
    }

    /// <summary>Upper-cases the first character, leaving the rest untouched.</summary>
    public static string? Capitalize(this string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        return string.Concat(char.ToUpperInvariant(input[0]).ToString(), input.AsSpan(1));
    }

    /// <summary>Truncates to <paramref name="max"/> characters; -1 means no limit.</summary>
    public static string ToMaxLength(this string input, int max = -1)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (max == -1 || input.Length <= max) return input;
        return input[..max];
    }

    /// <summary>
    /// Extracts featured performers from a title's parenthetical, e.g.
    /// <c>Song (feat. A &amp; B)</c> yields <c>A</c> and <c>B</c>.
    /// </summary>
    public static IEnumerable<string> ToPerformers(this string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var match = PerformersPattern.Match(value);
        var performers = match.Groups["performers"].Value.Replace(" & ", ", ", StringComparison.Ordinal);

        return PerformerSeparator.Split(performers);
    }
}
