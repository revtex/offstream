using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;

namespace Offstream.Core.Encoding;

/// <summary>Where the ffmpeg being used came from. Worth logging: it explains version surprises.</summary>
public enum FFmpegSource
{
    /// <summary>An explicit path from settings.</summary>
    Configured,

    /// <summary>The copy shipped alongside the application.</summary>
    Bundled,

    /// <summary>Found by walking <c>PATH</c>.</summary>
    SystemPath,
}

/// <summary>A resolved ffmpeg installation.</summary>
/// <param name="ExecutablePath">The ffmpeg executable, verified to exist.</param>
/// <param name="ProbePath">
/// The sibling ffprobe path. Best-effort and <em>not</em> verified: encoding never needs it,
/// and only diagnostics and the integration tests do.
/// </param>
/// <param name="Source">Which rung of the search order produced this.</param>
public sealed record FFmpegLocation(string ExecutablePath, string ProbePath, FFmpegSource Source);

/// <summary>No usable ffmpeg was found, or a configured one was not where it was said to be.</summary>
public sealed class FFmpegNotFoundException : Exception
{
    public FFmpegNotFoundException()
    {
    }

    public FFmpegNotFoundException(string message) : base(message)
    {
    }

    public FFmpegNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Finds the ffmpeg the app should run: configured path, then the bundled copy, then <c>PATH</c>.
/// </summary>
/// <remarks>
/// <para>
/// Plan §5.1 settles on bundling an LGPL build "with runtime override", which fixes the order:
/// <b>a configured path wins outright</b>. An override that loses to the bundle is not an
/// override, so a configured path that does not exist is an error rather than a cue to fall
/// back — silently encoding with a different ffmpeg than the user pointed at is exactly the
/// kind of thing that makes a bug report unreadable.
/// </para>
/// <para>
/// Takes an <see cref="IFileSystem"/> and an explicit search path so the whole search order is
/// testable without installing anything.
/// </para>
/// </remarks>
public sealed class FFmpegLocator(IFileSystem fileSystem, string applicationDirectory, string? searchPath = null)
{
    /// <summary>The executable this looks for. Offstream is Windows-only (§2.2).</summary>
    public const string ExecutableName = "ffmpeg.exe";

    /// <summary>ffprobe's file name, resolved as a sibling of ffmpeg.</summary>
    public const string ProbeName = "ffprobe.exe";

    /// <summary>Subfolder of the application directory that a bundled build is published into.</summary>
    public const string BundleFolderName = "ffmpeg";

    /// <summary>Resolves ffmpeg, or explains what was searched.</summary>
    /// <param name="configuredPath">
    /// The user's override: the executable itself, or the folder holding it. Null or blank
    /// means "no override".
    /// </param>
    /// <exception cref="FFmpegNotFoundException">
    /// The configured path does not exist, or nothing was found on any rung.
    /// </exception>
    public FFmpegLocation Locate(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return LocateConfigured(configuredPath)
                   ?? throw new FFmpegNotFoundException(
                       $"The configured ffmpeg path '{configuredPath}' does not exist. " +
                       "Clear the setting to fall back to the bundled copy or PATH.");
        }

        return LocateBundled()
               ?? LocateOnSearchPath()
               ?? throw new FFmpegNotFoundException(
                   $"ffmpeg was not found. Looked for '{ExecutableName}' beside the application " +
                   $"(in '{applicationDirectory}' and its '{BundleFolderName}' subfolder), then on PATH.");
    }

    /// <summary>Non-throwing <see cref="Locate"/>, for a settings screen validating as you type.</summary>
    public bool TryLocate(string? configuredPath, [NotNullWhen(true)] out FFmpegLocation? location)
    {
        location = !string.IsNullOrWhiteSpace(configuredPath)
            ? LocateConfigured(configuredPath)
            : LocateBundled() ?? LocateOnSearchPath();

        return location is not null;
    }

    /// <summary>
    /// Accepts either the executable or the folder containing it, because both are things a
    /// user reasonably pastes into a path box.
    /// </summary>
    private FFmpegLocation? LocateConfigured(string configuredPath)
    {
        var trimmed = configuredPath.Trim().Trim('"');

        if (fileSystem.Directory.Exists(trimmed)) return InFolder(trimmed, FFmpegSource.Configured);

        return fileSystem.File.Exists(trimmed)
            ? new FFmpegLocation(
                trimmed,
                fileSystem.Path.Combine(
                    fileSystem.Path.GetDirectoryName(trimmed) ?? string.Empty, ProbeName),
                FFmpegSource.Configured)
            : null;
    }

    private FFmpegLocation? LocateBundled() =>
        InFolder(fileSystem.Path.Combine(applicationDirectory, BundleFolderName), FFmpegSource.Bundled)
        ?? InFolder(applicationDirectory, FFmpegSource.Bundled);

    private FFmpegLocation? LocateOnSearchPath()
    {
        var path = searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = folder.Trim().Trim('"');
            if (candidate.Length == 0) continue;

            // A malformed PATH entry is normal on real machines; skip it rather than fail the
            // whole search over one bad segment.
            FFmpegLocation? found;
            try
            {
                found = InFolder(candidate, FFmpegSource.SystemPath);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (found is not null) return found;
        }

        return null;
    }

    private FFmpegLocation? InFolder(string folder, FFmpegSource source)
    {
        var executable = fileSystem.Path.Combine(folder, ExecutableName);

        return fileSystem.File.Exists(executable)
            ? new FFmpegLocation(executable, fileSystem.Path.Combine(folder, ProbeName), source)
            : null;
    }
}
