using System.Globalization;
using System.Text.RegularExpressions;

namespace Offstream.Core.Encoding;

/// <summary>
/// The version of an ffmpeg build, parsed from its <c>-version</c> banner.
/// </summary>
/// <remarks>
/// <para>
/// Plan §5.1 asks for this to be asserted at startup and logged in diagnostics, because encoder
/// flags drift across major versions: the argument vectors in <see cref="FFmpegArguments"/> are
/// golden-tested against a build that is assumed to understand them, and an ancient ffmpeg on
/// a user's <c>PATH</c> would fail at encode time with a message nobody connects to the cause.
/// </para>
/// <para>
/// Unknown versions are tolerated. Nightly builds identify themselves as <c>N-118488-g1e1e4d1</c>
/// with no version number at all, and those are newer than any release, so refusing to run on
/// an unparseable banner would reject exactly the builds most likely to work.
/// </para>
/// </remarks>
/// <param name="Major">Major version, or 0 when the banner carried no version number.</param>
/// <param name="Minor">Minor version, 0 when absent.</param>
/// <param name="Patch">Patch version, 0 when absent.</param>
/// <param name="Raw">The version token exactly as ffmpeg printed it, for the log.</param>
public sealed partial record FFmpegVersion(int Major, int Minor, int Patch, string Raw)
{
    /// <summary>
    /// The oldest ffmpeg Offstream claims to support. 6.0 (2023) predates every flag used in
    /// <see cref="EncodingProfiles"/> by years; the floor exists to make an unsupportably old
    /// build fail loudly at startup rather than obscurely at the first encode.
    /// </summary>
    public static readonly FFmpegVersion Minimum = new(6, 0, 0, "6.0");

    /// <summary>Whether a version number could be read from the banner at all.</summary>
    public bool IsKnown => Major > 0;

    /// <summary>Whether this build is at or above <see cref="Minimum"/>, or unidentifiable.</summary>
    public bool IsSupported => !IsKnown || IsAtLeast(Minimum);

    /// <summary>Ordering comparison against another version.</summary>
    public bool IsAtLeast(FFmpegVersion other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Major != other.Major) return Major > other.Major;
        if (Minor != other.Minor) return Minor > other.Minor;

        return Patch >= other.Patch;
    }

    /// <summary>Runs <c>ffmpeg -version</c> and parses the banner.</summary>
    /// <exception cref="FFmpegException">ffmpeg could not be run, or exited non-zero.</exception>
    public static async Task<FFmpegVersion> QueryAsync(
        FFmpegRunner runner,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var result = await runner.RunAsync(
            ["-hide_banner", "-version"], timeout ?? TimeSpan.FromSeconds(15), cancellationToken);

        if (!result.Succeeded)
        {
            throw new FFmpegException(
                $"'{runner.ExecutablePath} -version' exited with code {result.ExitCode}.",
                result.ExitCode,
                result.StandardError);
        }

        // The banner goes to stdout, but a build that logs oddly should not defeat the parse.
        return Parse(result.StandardOutput + Environment.NewLine + result.StandardError);
    }

    /// <summary>Queries the version and rejects anything below <see cref="Minimum"/>.</summary>
    /// <exception cref="FFmpegException">The build is too old, or could not be run.</exception>
    public static async Task<FFmpegVersion> RequireSupportedAsync(
        FFmpegRunner runner,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var version = await QueryAsync(runner, timeout, cancellationToken);

        if (!version.IsSupported)
        {
            throw new FFmpegException(
                $"ffmpeg {version.Raw} is too old; Offstream needs {Minimum.Major}.{Minimum.Minor} or newer.");
        }

        return version;
    }

    /// <summary>Parses a <c>-version</c> banner. Never throws; an unreadable banner is "unknown".</summary>
    public static FFmpegVersion Parse(string banner)
    {
        ArgumentNullException.ThrowIfNull(banner);

        var token = BannerPattern().Match(banner);
        if (!token.Success) return Unknown(string.Empty);

        var raw = token.Groups["raw"].Value;
        var numbers = NumberPattern().Match(raw);

        return numbers.Success
            ? new FFmpegVersion(
                Number(numbers, "major"), Number(numbers, "minor"), Number(numbers, "patch"), raw)
            : Unknown(raw);
    }

    /// <summary>A readable form for logs: the raw token, which is what a bug report needs.</summary>
    public override string ToString() => Raw.Length > 0 ? Raw : "unknown";

    private static FFmpegVersion Unknown(string raw) => new(0, 0, 0, raw);

    private static int Number(Match match, string group) =>
        match.Groups[group].Success
            ? int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture)
            : 0;

    /// <summary>Matches the version token in <c>ffmpeg version 8.1.2-essentials Copyright ...</c>.</summary>
    [GeneratedRegex(@"^ffmpeg version\s+(?<raw>\S+)", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BannerPattern();

    /// <summary>
    /// Reads the leading number out of a version token. The optional lowercase <c>n</c> is the
    /// git release-tag prefix (<c>n7.1</c>); it is deliberately case-sensitive, because an
    /// uppercase <c>N</c> starts a nightly build id (<c>N-118488-g1e1e4d1</c>) whose digits are
    /// a revision counter, not a version.
    /// </summary>
    [GeneratedRegex(@"^n?(?<major>\d+)(?:[.\-](?<minor>\d+))?(?:[.\-](?<patch>\d+))?",
        RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumberPattern();
}
