using System.Buffers;
using System.Globalization;
using System.IO.Abstractions;
using Offstream.Core.Metadata;
using Offstream.Core.Recording;
using Offstream.Core.Settings;
using Offstream.Core.Spotify;
using Offstream.Core.Text;

namespace Offstream.Core.Naming;

/// <summary>
/// Turns a track plus the user's settings into a destination path, creates the folders, and
/// manages renames and cleanup.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the reference implementation's <c>FileManager</c>. Takes an
/// <see cref="IFileSystem"/> so every path rule is testable against an in-memory file system
/// rather than the real disk.
/// </para>
/// <para>
/// The 260-character budgeting is the subtle part: the limit is divided across however many
/// folder levels the template asks for, so a deep template degrades by truncating each level
/// rather than by failing at write time. Long-path support (plan §11) will relax this, but
/// the budgeting stays as the fallback for volumes without it.
/// </para>
/// </remarks>
public sealed class OutputPaths(
    RecordingSettings settings,
    Track track,
    IFileSystem fileSystem,
    DateTime now)
{
    /// <summary>
    /// The total path budget. Extended-length now, rather than the legacy 260 — see
    /// <see cref="LongPath"/> for why the <c>\\?\</c> prefix is the mechanism and how it was
    /// verified against ffmpeg, which is the process that actually writes the file.
    /// </summary>
    private static readonly int MaxPathLength = LongPath.MaxLength;

    private const int MinPathLeftLength = 100;

    /// <summary>Head-room reserved for a " 12" duplicate suffix plus ".flac".</summary>
    private const int CounterAndExtensionLength = 10;

    private static readonly SearchValues<char> InvalidPathChars =
        SearchValues.Create(Path.GetInvalidPathChars());

    /// <summary>A temp file path for the raw capture, tagged so strays are identifiable.</summary>
    /// <remarks>
    /// Deliberately not <c>Path.GetTempFileName</c>, which the reference implementation used:
    /// it is obsolete on modern .NET because it creates the file eagerly and gives up after
    /// 65535 names in a directory — a real ceiling for an app that records overnight. The
    /// <c>.tmp</c> extension is load-bearing: <see cref="DeleteFile"/> uses it to tell a
    /// scratch file from a finished recording whose folders should be tidied up.
    /// </remarks>
    public string GetTempFile()
    {
        var directory = fileSystem.Path.GetTempPath();
        var name = fileSystem.Path.GetRandomFileName();

        return fileSystem.Path.Combine(directory, $"{name}.offstream.tmp");
    }

    /// <summary>Builds the destination, creating any folders the template implies.</summary>
    public OutputFile GetOutputFileAndInitDirectories()
    {
        var (folders, fileName) = BuildFromTemplate(track, settings, now);

        CreateDirectories(folders);

        var outputFile = new OutputFile
        {
            BasePath = settings.OutputPath,
            FoldersPath = ConcatPaths(folders),
            MediaFile = fileName,
            Extension = settings.MediaFormatExtension,
        };

        if (settings.ExistingFilePolicy == ExistingFilePolicy.Duplicate)
        {
            while (fileSystem.File.Exists(outputFile.ToMediaFilePath())) outputFile.Increment();
        }

        return outputFile;
    }

    /// <summary>
    /// Renders the user template into folder segments plus a file name, budgeting the
    /// 260-character path limit across however many folder levels the template asks for.
    /// </summary>
    /// <exception cref="UnrecognizedTrackException">The track has no artist, or renders to nothing usable.</exception>
    public static (string[] Folders, string FileName) BuildFromTemplate(
        Track track, RecordingSettings settings, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrEmpty(track.Artist))
            throw new UnrecognizedTrackException("Cannot recognize this type of track.");

        if (track.Ad && !track.IsUnknownPlaying)
        {
            var stamp = now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            return ([], $"{SpotifyWindowTitles.Advertisement} {stamp}");
        }

        var template = settings.OutputTemplate;
        var levels = Math.Max(1, template.Split('\\', '/').Length);
        var budget = Math.Max(
            MaxPathLength - (settings.OutputPath ?? string.Empty).Length - CounterAndExtensionLength - levels,
            0);
        // Clamped to a single component's limit, which extended paths do NOT lift: they raise the
        // total only. Without this the relaxed budget divides into per-level allowances of
        // thousands of characters and renders a folder name the filesystem refuses outright —
        // trading "truncated at 260" for "cannot be written at all".
        var perLevel = Math.Clamp(budget / levels, 1, LongPath.MaxComponentLength);

        var (folders, fileName) = FileNameTemplate.Render(
            template, track, settings.OrderNumberAsFile, now, perLevel, perLevel);

        if (string.IsNullOrWhiteSpace(fileName))
            throw new UnrecognizedTrackException("File name cannot be empty.");

        if (fileName.IsNullOrIdle())
            throw new UnrecognizedTrackException("File name cannot be a Spotify idle state.");

        return (folders, fileName);
    }

    /// <summary>Deletes a file, then any folders the template created that are now empty.</summary>
    public void DeleteFile(string currentFile)
    {
        if (string.IsNullOrWhiteSpace(currentFile))
            throw new ArgumentException("File name cannot be null.", nameof(currentFile));

        if (fileSystem.File.Exists(currentFile)) fileSystem.File.Delete(currentFile);

        var isTempFile = string.Equals(
            fileSystem.Path.GetExtension(currentFile), ".tmp", StringComparison.OrdinalIgnoreCase);

        if (!isTempFile && fileSystem.Path.GetDirectoryName(currentFile) != settings.OutputPath)
        {
            DeleteEmptyFolders(currentFile);
        }
    }

    /// <summary>Moves <paramref name="source"/> onto <paramref name="destination"/>, replacing it.</summary>
    /// <exception cref="SourceFileNotFoundException">The source is gone.</exception>
    /// <exception cref="DestinationPathNotFoundException">The destination folder is gone.</exception>
    public void RenameFile(string source, string destination)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("Source / destination file name cannot be null.", nameof(source));

        if (!fileSystem.File.Exists(source))
            throw new SourceFileNotFoundException($"Source file was not found: {source}");

        if (fileSystem.File.Exists(destination)) fileSystem.File.Delete(destination);

        var destinationDirectory = fileSystem.Path.GetDirectoryName(destination);

        if (!fileSystem.Directory.Exists(destinationDirectory))
        {
            throw new DestinationPathNotFoundException(
                $"Destination path was not found: {destinationDirectory}");
        }

        fileSystem.File.Move(source, destination);
    }

    /// <summary>Whether this track already has a recording on disk under the current settings.</summary>
    public bool IsPathFileNameExists(Track candidate, RecordingSettings candidateSettings)
    {
        ArgumentNullException.ThrowIfNull(candidateSettings);

        var (folders, fileName) = BuildFromTemplate(candidate, candidateSettings, now);
        var folderPath = ConcatPaths(candidateSettings.OutputPath, ConcatPaths(folders));
        var filePath = ConcatPaths(folderPath, $"{fileName}.{candidateSettings.MediaFormatExtension}");

        return fileSystem.File.Exists(filePath);
    }

    /// <summary>Strips characters Windows forbids anywhere in a path.</summary>
    public static string GetCleanPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var trimmed = path.TrimEndPath() ?? string.Empty;
        var builder = new System.Text.StringBuilder(trimmed.Length);

        foreach (var c in trimmed)
        {
            if (!InvalidPathChars.Contains(c)) builder.Append(c);
        }

        return builder.ToString();
    }

    /// <inheritdoc cref="PathText.CleanSegment"/>
    public static string GetCleanFileFolder(string name, int maxLength) => PathText.CleanSegment(name, maxLength);

    /// <summary>Joins path parts with a backslash, skipping blanks.</summary>
    public static string ConcatPaths(params string?[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return string.Join('\\', paths.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>
    /// Whether an output root leaves too little room for a rendered file name.
    /// </summary>
    public static bool IsOutputPathTooLong(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return MaxPathLength - path.Length <= MinPathLeftLength;
    }

    private void CreateDirectories(string[] folders)
    {
        if (folders.Length == 0) return;

        for (var i = 1; i <= folders.Length; i++)
        {
            // Extended form for the filesystem call only. A deep template can push an intermediate
            // folder past 260 before the file name is even appended, and CreateDirectory has no
            // idea what the caller intends to put inside it.
            var path = LongPath.Extended(ConcatPaths([settings.OutputPath, .. folders.Take(i)]));

            if (!fileSystem.Directory.Exists(path)) fileSystem.Directory.CreateDirectory(path);
        }
    }

    /// <summary>
    /// Removes the folder a deleted file lived in, and its now-empty parents, stopping at the
    /// output root so a user's own directories are never touched.
    /// </summary>
    private void DeleteEmptyFolders(string currentFile)
    {
        var folderPath = fileSystem.Path.GetDirectoryName(currentFile);
        if (string.IsNullOrEmpty(folderPath)) return;
        if (fileSystem.Directory.EnumerateFiles(folderPath).Any()) return;

        fileSystem.Directory.Delete(folderPath);

        var parent = fileSystem.Path.GetDirectoryName(folderPath);
        if (string.IsNullOrEmpty(parent) || settings.OutputPath is null) return;

        if (parent != settings.OutputPath && parent.Contains(settings.OutputPath, StringComparison.Ordinal))
        {
            DeleteEmptyFolders(folderPath);
        }
    }
}
