namespace Offstream.Core.Naming;

/// <summary>
/// Extended-length path support, so a deep template is not truncated to fit a 1995 limit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the prefix rather than the manifest.</b> Windows offers two ways past <c>MAX_PATH</c>:
/// opt the process in (a <c>longPathAware</c> manifest plus the machine's <c>LongPathsEnabled</c>
/// registry switch), or prefix each path with <c>\\?\</c>. Only the second is available here,
/// because Offstream does not write the file — <b>ffmpeg does</b>, and it is a separate process
/// with its own manifest and no interest in ours. The prefix travels with the path, through
/// <c>ArgumentList</c>, into whatever ffmpeg passes to <c>CreateFile</c>.
/// </para>
/// <para>
/// Verified rather than assumed, since the whole feature rests on it: ffmpeg 8.1 wrote an MP3 to a
/// 298-character destination through a prefixed path, on a machine with the registry switch off.
/// </para>
/// <para>
/// <b>The prefix disables normalisation, which is the trap.</b> Windows stops expanding
/// <c>.</c> and <c>..</c>, stops converting <c>/</c> to <c>\</c>, and stops trimming trailing
/// spaces and dots. A path that was merely untidy before becomes one the filesystem rejects. So it
/// is applied only to a fully-qualified, already-normalised path, and only when the path is
/// actually long enough to need it — a short path keeps its ordinary behaviour.
/// </para>
/// </remarks>
public static class LongPath
{
    /// <summary>The legacy <c>MAX_PATH</c>, still the limit for any path without the prefix.</summary>
    public const int LegacyMaxLength = 260;

    /// <summary>
    /// The ceiling with the prefix applied. Well beyond anything a template produces; it is here
    /// so the budgeting has a number rather than a special case.
    /// </summary>
    public const int ExtendedMaxLength = 32767;

    /// <summary>
    /// NTFS's limit on a single file or folder name, which the prefix does <b>not</b> lift.
    /// </summary>
    /// <remarks>
    /// The one that catches people out. Extended paths raise the total, never the component, so a
    /// budget spread across few levels must still be clamped or it produces a name the filesystem
    /// refuses whatever the total length is.
    /// </remarks>
    public const int MaxComponentLength = 255;

    private const string Prefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>Prefixes <paramref name="path"/> when it is long enough to need it.</summary>
    /// <remarks>
    /// Returns the path unchanged when it is short, already prefixed, a device path, or not
    /// fully qualified — in every one of those cases prefixing would change meaning rather than
    /// just capacity.
    /// </remarks>
    public static string Extended(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length < LegacyMaxLength) return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        if (!Path.IsPathFullyQualified(path)) return path;

        // Normalisation is off once prefixed, so it has to happen first.
        var full = Path.GetFullPath(path);

        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? string.Concat(UncPrefix, full.AsSpan(2))
            : string.Concat(Prefix, full);
    }

    /// <summary>The total path budget, given that <see cref="Extended"/> is available.</summary>
    public static int MaxLength => ExtendedMaxLength;
}
